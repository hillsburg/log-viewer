namespace LogViewer.Api.Models;

/// <summary>
/// 文件打开历史记录条目，持久化到 Data/logviewer.db（SQLite）。
/// FilePath 可能是本地原始路径，也可能是 Data/uploads 下上传文件的路径。
/// </summary>
public class FileRecord
{
    /// <summary>自增主键</summary>
    public long Id { get; set; }

    /// <summary>文件完整路径（UNIQUE 约束，同一路径打开时 UPSERT 更新而非新增）</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>显示用文件名（不含目录路径）</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>文件字节数，打开时记录</summary>
    public long FileSize { get; set; }

    /// <summary>文件总行数，打开时记录</summary>
    public long TotalLines { get; set; }

    /// <summary>累计打开次数，每次打开同文件时 +1</summary>
    public int OpenCount { get; set; } = 1;

    /// <summary>最后打开时间</summary>
    public DateTime LastOpened { get; set; }

    /// <summary>首次打开时间（新建记录时写入，更新时不变）</summary>
    public DateTime FirstOpened { get; set; }

    /// <summary>文件来源类型：local（本地路径）或 upload（上传文件）</summary>
    public string Source { get; set; } = "local";

    /// <summary>上次浏览的滚动位置（行号），支持"继续上次浏览"</summary>
    public long LastScrollLine { get; set; }
}
