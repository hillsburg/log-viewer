using Microsoft.Data.Sqlite;
using LogViewer.Api.Models;

namespace LogViewer.Api.Services;

/// <summary>
/// 关键字配置服务，使用 SQLite 持久化。
/// 无 SemaphoreSlim，依赖 WAL 模式保证并发安全。
/// </summary>
public class KeywordService
{
    private readonly string _connectionString;

    public KeywordService(DatabaseInitializer db)
    {
        _connectionString = db.ConnectionString;
    }

    /// <summary>获取所有关键字，按创建时间升序</summary>
    public List<Keyword> GetAll()
    {
        var list = new List<Keyword>();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM keywords ORDER BY CreatedAt ASC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(Map(reader));
        return list;
    }

    public Keyword? GetById(int id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM keywords WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>添加关键字，Id 自增，CreatedAt/UpdatedAt 自动填充</summary>
    /// <param name="matchMode">匹配模式：contains / wholeWord / regex</param>
    public Keyword Add(string text, string color, bool caseSensitive, bool highlightWholeLine, int wholeLineOpacity = 30, string matchMode = "contains")
    {
        ValidateMatchMode(matchMode, text);
        var now = DateTime.Now.ToString("O");
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO keywords (Text, Color, Enabled, CaseSensitive, HighlightWholeLine, WholeLineOpacity, MatchMode, CreatedAt, UpdatedAt)
            VALUES (@text, @color, 1, @cs, @hl, @op, @mm, @now, @now);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@text", text);
        cmd.Parameters.AddWithValue("@color", color);
        cmd.Parameters.AddWithValue("@cs", caseSensitive ? 1 : 0);
        cmd.Parameters.AddWithValue("@hl", highlightWholeLine ? 1 : 0);
        cmd.Parameters.AddWithValue("@op", wholeLineOpacity);
        cmd.Parameters.AddWithValue("@mm", matchMode);
        cmd.Parameters.AddWithValue("@now", now);
        var id = (long)cmd.ExecuteScalar()!;

        return new Keyword
        {
            Id = (int)id,
            Text = text,
            Color = color,
            Enabled = true,
            CaseSensitive = caseSensitive,
            HighlightWholeLine = highlightWholeLine,
            WholeLineOpacity = wholeLineOpacity,
            MatchMode = matchMode,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>更新关键字全部字段，UpdatedAt 自动刷新</summary>
    public Keyword? Update(int id, string text, string color, bool enabled, bool caseSensitive, bool highlightWholeLine, int wholeLineOpacity = 30, string matchMode = "contains")
    {
        ValidateMatchMode(matchMode, text);
        var now = DateTime.Now.ToString("O");
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE keywords SET
                Text = @text, Color = @color, Enabled = @en,
                CaseSensitive = @cs, HighlightWholeLine = @hl,
                WholeLineOpacity = @op, MatchMode = @mm, UpdatedAt = @now
            WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@text", text);
        cmd.Parameters.AddWithValue("@color", color);
        cmd.Parameters.AddWithValue("@en", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@cs", caseSensitive ? 1 : 0);
        cmd.Parameters.AddWithValue("@hl", highlightWholeLine ? 1 : 0);
        cmd.Parameters.AddWithValue("@op", wholeLineOpacity);
        cmd.Parameters.AddWithValue("@mm", matchMode);
        cmd.Parameters.AddWithValue("@now", now);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0) return null;

        return new Keyword
        {
            Id = id, Text = text, Color = color, Enabled = enabled,
            CaseSensitive = caseSensitive, HighlightWholeLine = highlightWholeLine,
            WholeLineOpacity = wholeLineOpacity, MatchMode = matchMode,
            CreatedAt = now, UpdatedAt = now
        };
    }

    public bool Delete(int id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM keywords WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ========== 全局配置（settings 表） ==========

    public string GetTheme()
    {
        return GetSetting("Theme") ?? "dark";
    }

    public void SetTheme(string theme)
    {
        SetSetting("Theme", theme);
    }

    public string? GetSetting(string key)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM settings WHERE Key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar()?.ToString();
    }

    public async Task SetSettingAsync(string key, string value)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO settings (Key, Value) VALUES (@key, @value)";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    private void SetSetting(string key, string value)
    {
        SetSettingAsync(key, value).GetAwaiter().GetResult();
    }

    /// <summary>SQLite 行 → Keyword 对象</summary>
    private static Keyword Map(SqliteDataReader reader)
    {
        return new Keyword
        {
            Id                 = reader.GetInt32(reader.GetOrdinal("Id")),
            Text               = reader.GetString(reader.GetOrdinal("Text")),
            Color              = reader.GetString(reader.GetOrdinal("Color")),
            Enabled            = reader.GetInt32(reader.GetOrdinal("Enabled")) == 1,
            CaseSensitive      = reader.GetInt32(reader.GetOrdinal("CaseSensitive")) == 1,
            HighlightWholeLine = reader.GetInt32(reader.GetOrdinal("HighlightWholeLine")) == 1,
            WholeLineOpacity   = reader.GetInt32(reader.GetOrdinal("WholeLineOpacity")),
            MatchMode          = reader.GetString(reader.GetOrdinal("MatchMode")),
            CreatedAt          = reader.GetString(reader.GetOrdinal("CreatedAt")),
            UpdatedAt          = reader.GetString(reader.GetOrdinal("UpdatedAt"))
        };
    }

    /// <summary>
    /// 校验匹配模式和关键字文本的合法性。
    /// - matchMode 必须是 contains / wholeWord / regex 之一
    /// - regex 模式下校验正则合法性，长度限制 200 字符
    /// </summary>
    private static void ValidateMatchMode(string matchMode, string text)
    {
        if (matchMode != "contains" && matchMode != "wholeWord" && matchMode != "regex")
            throw new ArgumentException($"Invalid matchMode: {matchMode}");

        if (matchMode == "regex")
        {
            if (string.IsNullOrEmpty(text))
                throw new ArgumentException("正则表达式不能为空");
            if (text.Length > 200)
                throw new ArgumentException("正则表达式长度不能超过 200 字符");
            try
            {
                _ = new System.Text.RegularExpressions.Regex(text);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"非法正则表达式: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 导入关键字配置。
    ///
    /// 导入时按导入数据的 CreatedAt 升序排序后依次处理，保留原始顺序。
    /// CreatedAt / UpdatedAt 使用当前时间（不使用导出的时间戳）。
    ///
    /// replace 模式：导入全部，与现有 Text 冲突的条目覆盖（先删后插），
    ///   不冲突的现有关键字保留不变。
    /// merge 模式：逐个检查 Text 冲突，按 conflictAction 决定跳过或覆盖。
    /// </summary>
    /// <param name="keywords">要导入的关键字列表（含 CreatedAt 用于排序）</param>
    /// <param name="mode">"replace" 或 "merge"</param>
    /// <param name="conflictAction">merge 模式下冲突时："skip" 或 "overwrite"</param>
    /// <returns>导入统计（新增 / 跳过 / 覆盖数量）</returns>
    public ImportResult Import(List<ImportKeyword> keywords, string mode, string conflictAction)
    {
        // 按导出时的 CreatedAt 排序，保留用户原始排列顺序
        var sorted = keywords.OrderBy(k => k.CreatedAt).ToList();

        var now = DateTime.Now.ToString("O");
        var added = 0;
        var skipped = 0;
        var overwritten = 0;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        foreach (var kw in sorted)
        {
            // 按 Text 查找是否已存在同名关键字
            var existingId = FindByText(conn, kw.Text);

            if (existingId.HasValue)
            {
                // 冲突：根据模式和用户选择处理
                if (mode == "replace" || conflictAction == "overwrite")
                {
                    // 覆盖：删除旧条目，插入新条目（保留新 Id 和时间戳）
                    DeleteById(conn, existingId.Value);
                    InsertKeyword(conn, kw, now);
                    overwritten++;
                }
                else
                {
                    // 跳过：保留现有条目不变
                    skipped++;
                }
            }
            else
            {
                // 无冲突：直接插入
                InsertKeyword(conn, kw, now);
                added++;
            }
        }

        return new ImportResult(added, skipped, overwritten);
    }

    /// <summary>按 Text 精确查找关键字 Id，用于导入时检测冲突</summary>
    private static int? FindByText(SqliteConnection conn, string text)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM keywords WHERE Text = @text LIMIT 1";
        cmd.Parameters.AddWithValue("@text", text);
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToInt32(result) : null;
    }

    /// <summary>按 Id 删除单条关键字（导入覆盖时使用）</summary>
    private static void DeleteById(SqliteConnection conn, int id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM keywords WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>插入一条新关键字，CreatedAt/UpdatedAt 均设为 now</summary>
    /// <summary>插入一条新关键字，CreatedAt/UpdatedAt 均设为 now，Id 由 SQLite 自增分配</summary>
    private static void InsertKeyword(SqliteConnection conn, ImportKeyword kw, string now)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO keywords (Text, Color, Enabled, CaseSensitive, HighlightWholeLine, WholeLineOpacity, MatchMode, CreatedAt, UpdatedAt)
            VALUES (@text, @color, @en, @cs, @hl, @op, @mm, @now, @now)";
        cmd.Parameters.AddWithValue("@text", kw.Text);
        cmd.Parameters.AddWithValue("@color", kw.Color);
        cmd.Parameters.AddWithValue("@en", kw.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@cs", kw.CaseSensitive ? 1 : 0);
        cmd.Parameters.AddWithValue("@hl", kw.HighlightWholeLine ? 1 : 0);
        cmd.Parameters.AddWithValue("@op", kw.WholeLineOpacity);
        cmd.Parameters.AddWithValue("@mm", kw.MatchMode ?? "contains");
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>按 Id 更新关键字全部字段，UpdatedAt 刷新为 now（保留 CreatedAt 原值）</summary>
    private static void UpdateKeywordById(SqliteConnection conn, int id, ImportKeyword kw, string now)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE keywords SET
                Text = @text, Color = @color, Enabled = @en,
                CaseSensitive = @cs, HighlightWholeLine = @hl,
                WholeLineOpacity = @op, MatchMode = @mm, UpdatedAt = @now
            WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@text", kw.Text);
        cmd.Parameters.AddWithValue("@color", kw.Color);
        cmd.Parameters.AddWithValue("@en", kw.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@cs", kw.CaseSensitive ? 1 : 0);
        cmd.Parameters.AddWithValue("@hl", kw.HighlightWholeLine ? 1 : 0);
        cmd.Parameters.AddWithValue("@op", kw.WholeLineOpacity);
        cmd.Parameters.AddWithValue("@mm", kw.MatchMode ?? "contains");
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }
}
