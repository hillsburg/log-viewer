using Microsoft.Data.Sqlite;

namespace LogViewer.Api.Services;

/// <summary>
/// 统一数据库初始化器，在应用启动时一次性创建所有表。
/// KeywordService 和 HistoryService 共用同一个 logviewer.db，
/// 各自通过连接字符串创建独立连接，无需共享连接实例。
/// </summary>
public class DatabaseInitializer
{
    private readonly string _connectionString;

    public DatabaseInitializer(IWebHostEnvironment env)
    {
        var dataDir = Path.Combine(env.ContentRootPath, "Data");
        if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);

        var dbPath = Path.Combine(dataDir, "logviewer.db");
        _connectionString = $"Data Source={dbPath}";
    }

    public string ConnectionString => _connectionString;

    /// <summary>创建所有表和索引（幂等，重复执行无副作用）</summary>
    public void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // WAL 模式：提升并发读写性能
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL";
        pragma.ExecuteNonQuery();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            -- 关键字表
            CREATE TABLE IF NOT EXISTS keywords (
                Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                Text               TEXT    NOT NULL,
                Color              TEXT    NOT NULL DEFAULT '#ff4444',
                Enabled            INTEGER NOT NULL DEFAULT 1,
                CaseSensitive      INTEGER NOT NULL DEFAULT 0,
                HighlightWholeLine INTEGER NOT NULL DEFAULT 0,
                WholeLineOpacity   INTEGER NOT NULL DEFAULT 30,
                MatchMode          TEXT    NOT NULL DEFAULT 'contains',
                CreatedAt          TEXT    NOT NULL,
                UpdatedAt          TEXT    NOT NULL
            );

            -- 全局配置键值表
            CREATE TABLE IF NOT EXISTS settings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            -- 历史记录表
            CREATE TABLE IF NOT EXISTS file_history (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                FilePath        TEXT    NOT NULL UNIQUE,
                FileName        TEXT    NOT NULL,
                FileSize        INTEGER NOT NULL DEFAULT 0,
                TotalLines      INTEGER NOT NULL DEFAULT 0,
                OpenCount       INTEGER NOT NULL DEFAULT 1,
                LastOpened      TEXT    NOT NULL,
                FirstOpened     TEXT    NOT NULL,
                Source          TEXT    NOT NULL DEFAULT 'local',
                LastScrollLine  INTEGER NOT NULL DEFAULT 0
            );

            -- 历史记录索引
            CREATE INDEX IF NOT EXISTS idx_history_last_opened
                ON file_history(LastOpened DESC);
            CREATE INDEX IF NOT EXISTS idx_history_open_count
                ON file_history(OpenCount DESC);

            -- 预置默认主题（仅首次插入）
            INSERT OR IGNORE INTO settings (Key, Value) VALUES ('Theme', 'dark');
        ";
        cmd.ExecuteNonQuery();
    }
}
