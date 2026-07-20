using Microsoft.Data.Sqlite;
using LogViewer.Api.Models;

namespace LogViewer.Api.Services;

/// <summary>
/// 文件打开历史记录服务，使用 SQLite 持久化到 Data/logviewer.db。
/// 删除/清空记录时同步删除 Data/uploads 下对应的上传文件。
/// </summary>
public class HistoryService
{
    private readonly string _uploadsDir;
    private readonly string _connectionString;

    public HistoryService(DatabaseInitializer db, IWebHostEnvironment env)
    {
        _uploadsDir = Path.Combine(env.ContentRootPath, "Data", "uploads");
        _connectionString = db.ConnectionString;
    }

    /// <summary>获取所有记录，支持排序和搜索</summary>
    public List<FileRecord> GetAll(string sort = "recent", string? search = null)
    {
        var records = new List<FileRecord>();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        var orderBy = sort switch
        {
            "frequent" => "ORDER BY OpenCount DESC, LastOpened DESC",
            _          => "ORDER BY LastOpened DESC"
        };

        var sql = $"SELECT * FROM file_history";
        if (!string.IsNullOrWhiteSpace(search))
            sql += " WHERE FileName LIKE @search";

        sql += $" {orderBy} LIMIT 50";

        using var cmd = new SqliteCommand(sql, conn);
        if (!string.IsNullOrWhiteSpace(search))
            cmd.Parameters.AddWithValue("@search", $"%{search}%");

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            records.Add(MapRecord(reader));

        return records;
    }

    /// <summary>
    /// 添加记录（UPSERT）：同一路径打开时更新而非新增。
    /// 新增时写入 FileSize/TotalLines/FirstOpened，更新时递增 OpenCount。
    /// </summary>
    public FileRecord Add(string filePath, string fileName, long fileSize, long totalLines, string source = "local")
    {
        var now = DateTime.Now.ToString("O");
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO file_history
                (FilePath, FileName, FileSize, TotalLines, OpenCount, LastOpened, FirstOpened, Source)
            VALUES
                (@path, @name, @size, @lines, 1, @now, @now, @source)
            ON CONFLICT(FilePath) DO UPDATE SET
                FileName   = COALESCE(@name, FileName),
                FileSize   = @size,
                TotalLines = @lines,
                OpenCount  = OpenCount + 1,
                LastOpened = @now;
            SELECT * FROM file_history WHERE FilePath = @path;
        ";
        cmd.Parameters.AddWithValue("@path", filePath);
        cmd.Parameters.AddWithValue("@name", fileName);
        cmd.Parameters.AddWithValue("@size", fileSize);
        cmd.Parameters.AddWithValue("@lines", totalLines);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@source", source);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapRecord(reader) : new FileRecord { FilePath = filePath, FileName = fileName };
    }

    /// <summary>移除指定路径的记录，并删除对应的上传文件</summary>
    public bool Remove(string filePath)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM file_history WHERE FilePath = @path";
        cmd.Parameters.AddWithValue("@path", filePath);

        var deleted = cmd.ExecuteNonQuery() > 0;
        if (deleted) TryDeleteUploadFile(filePath);
        return deleted;
    }

    /// <summary>清空所有记录，删除所有上传文件</summary>
    public int ClearAll()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // 先读取所有上传文件路径再删除记录
        var uploadPaths = new List<string>();
        using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.CommandText = "SELECT FilePath FROM file_history WHERE Source = 'upload'";
            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
                uploadPaths.Add(reader.GetString(0));
        }

        using var deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM file_history";
        var count = deleteCmd.ExecuteNonQuery();

        foreach (var p in uploadPaths)
            TryDeleteUploadFile(p);

        return count;
    }

    /// <summary>更新上次浏览的滚动位置</summary>
    public void UpdateScrollPosition(string filePath, long line)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE file_history SET LastScrollLine = @line WHERE FilePath = @path";
        cmd.Parameters.AddWithValue("@line", line);
        cmd.Parameters.AddWithValue("@path", filePath);
        cmd.ExecuteNonQuery();
    }

    /// <summary>通过路径查找记录</summary>
    public FileRecord? GetByPath(string filePath)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_history WHERE FilePath = @path";
        cmd.Parameters.AddWithValue("@path", filePath);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapRecord(reader) : null;
    }

    /// <summary>从 SQLite 结果行映射为 FileRecord 对象</summary>
    private static FileRecord MapRecord(SqliteDataReader reader)
    {
        return new FileRecord
        {
            Id             = reader.GetInt64(reader.GetOrdinal("Id")),
            FilePath       = reader.GetString(reader.GetOrdinal("FilePath")),
            FileName       = reader.GetString(reader.GetOrdinal("FileName")),
            FileSize       = reader.GetInt64(reader.GetOrdinal("FileSize")),
            TotalLines     = reader.GetInt64(reader.GetOrdinal("TotalLines")),
            OpenCount      = reader.GetInt32(reader.GetOrdinal("OpenCount")),
            LastOpened     = DateTime.Parse(reader.GetString(reader.GetOrdinal("LastOpened"))),
            FirstOpened    = DateTime.Parse(reader.GetString(reader.GetOrdinal("FirstOpened"))),
            Source         = reader.GetString(reader.GetOrdinal("Source")),
            LastScrollLine = reader.GetInt64(reader.GetOrdinal("LastScrollLine"))
        };
    }

    /// <summary>仅删除位于 uploads 目录下的物理文件</summary>
    private void TryDeleteUploadFile(string filePath)
    {
        if (!filePath.StartsWith(_uploadsDir, StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(filePath); } catch { }
    }
}
