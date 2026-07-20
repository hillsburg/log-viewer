using System.Text;
using LogViewer.Api.Models;

namespace LogViewer.Api.Services;

/// <summary>
/// 日志文件读取服务。
/// 核心优化：首次打开文件时扫描全文建立行偏移索引（LineOffsets），
/// 后续通过 FileStream.Seek 实现 O(1) 随机跳转到任意行直接读取，
/// 避免每次从文件头逐行扫描到目标位置。
///
/// 缓存策略：LRU 淘汰，最多保留 MaxCacheSize 个文件的索引。
/// 访问时更新访问顺序，缓存满时淘汰最久未访问的条目。
/// </summary>
public class FileService
{
    private const int MaxCacheSize = 100;

    /// <summary>LRU 缓存：路径 → 索引，按访问时间排序</summary>
    private readonly Dictionary<string, FileIndex> _indexCache = new();
    private readonly LinkedList<string> _accessOrder = new();
    private readonly object _cacheLock = new();

    /// <summary>
    /// 行偏移索引：LineOffsets[i] 表示第 i 行（0-indexed）在文件中的字节起始位置。
    /// LineOffsets[0] 固定为 0（文件头），数组长度 = 总行数 + 1。
    /// </summary>
    private class FileIndex
    {
        public long[] LineOffsets = Array.Empty<long>();
        public long TotalLines;
        public DateTime LastModified;
    }

    /// <summary>
    /// 获取或构建行偏移索引（带 LRU 缓存淘汰）。
    /// 如果缓存命中且文件最后修改时间未变，直接返回缓存；否则重建。
    /// </summary>
    private FileIndex? GetOrBuildIndex(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) return null;

        lock (_cacheLock)
        {
            // 缓存命中且文件未被修改，直接复用并刷新 LRU 顺序
            if (_indexCache.TryGetValue(filePath, out var cached) &&
                cached.LastModified == info.LastWriteTimeUtc)
            {
                TouchLru(filePath);
                return cached;
            }

            // 缓存满时淘汰最久未访问的条目
            if (_indexCache.Count >= MaxCacheSize)
                EvictLeastRecent();
        }

        // 构建索引：用 Encoding.GetByteCount 精确追踪每行字节偏移。
        // 不能使用 StreamReader.BaseStream.Position：StreamReader 内部缓冲会让
        // Position 跳到缓冲区末尾，导致偏移指向错误位置。
        var encoding = new UTF8Encoding(false);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);

        // 检测 BOM（字节顺序标记）：StreamReader 用 no-BOM 编码不会自动跳过 BOM，
        // 但 BOM 会被包含在第一行内容中，所以必须手动检测并校正起始偏移。
        // 支持：UTF-8 BOM (3字节)、UTF-16 LE/BE (2字节)、UTF-32 LE/BE (4字节)
        long startOffset = DetectBomLength(fs);

        // 检测换行符风格（\r\n 或 \n），用于精确计算每行字节数
        int newlineBytes = 2;
        var head = new byte[Math.Min(4096, fs.Length)];
        var headRead = fs.Read(head, 0, head.Length);
        for (int b = (int)startOffset; b < headRead; b++)
        {
            if (head[b] == '\n')
            {
                newlineBytes = (b > 0 && head[b - 1] == '\r') ? 2 : 1;
                break;
            }
        }
        fs.Position = startOffset; // 跳过 BOM，从真正的内容开始

        using var reader = new StreamReader(fs, encoding);
        var offsets = new List<long> { startOffset };
        long bytePos = startOffset;

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (line == null) break;
            bytePos += encoding.GetByteCount(line) + newlineBytes;
            offsets.Add(bytePos);
        }

        var index = new FileIndex
        {
            LineOffsets = offsets.ToArray(),
            TotalLines = offsets.Count - 1,
            LastModified = info.LastWriteTimeUtc
        };

        lock (_cacheLock)
        {
            // 再次检查容量（并发场景下可能多个文件同时构建索引）
            if (_indexCache.Count >= MaxCacheSize)
                EvictLeastRecent();
            _indexCache[filePath] = index;
            _accessOrder.AddFirst(filePath);
        }

        return index;
    }

    /// <summary>检测文件 BOM 并返回其字节长度（0 表示无 BOM）</summary>
    private static long DetectBomLength(FileStream fs)
    {
        if (fs.Length < 2) return 0;
        var bom = new byte[Math.Min(4, fs.Length)];
        fs.Read(bom, 0, bom.Length);
        fs.Position = 0;

        if (bom.Length >= 4)
        {
            if (bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0x00 && bom[3] == 0x00) return 4;
            if (bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF) return 4;
        }
        if (bom.Length >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return 3;
        if (bom[0] == 0xFF && bom[1] == 0xFE) return 2;
        if (bom[0] == 0xFE && bom[1] == 0xFF) return 2;
        return 0;
    }

    /// <summary>将 FileStream 定位到 BOM 之后的第一个内容字节</summary>
    private static void SkipBom(FileStream fs)
    {
        fs.Position = DetectBomLength(fs);
    }

    /// <summary>将指定路径移到访问链表头部（最近访问）</summary>
    private void TouchLru(string filePath)
    {
        if (_indexCache.ContainsKey(filePath))
        {
            _accessOrder.Remove(filePath);
            _accessOrder.AddFirst(filePath);
        }
    }

    /// <summary>淘汰访问链表尾部（最久未访问）的索引条目</summary>
    private void EvictLeastRecent()
    {
        if (_accessOrder.Count == 0) return;
        var oldest = _accessOrder.Last!;
        _accessOrder.RemoveLast();
        _indexCache.Remove(oldest.Value);
    }

    /// <summary>获取文件基本信息（名称、大小、总行数）</summary>
    public LogFileInfo GetFileInfo(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found", filePath);

        var info = new FileInfo(filePath);
        var index = GetOrBuildIndex(filePath);

        return new LogFileInfo
        {
            FilePath = filePath,
            FileName = info.Name,
            FileSize = info.Length,
            TotalLines = index?.TotalLines ?? 0
        };
    }

    /// <summary>
    /// 读取指定范围的行，通过索引 Seek 实现 O(1) 跳转。
    /// start: 起始行号（0-indexed），count: 读取行数。
    /// </summary>
    public List<string> GetLines(string filePath, long start, int count)
    {
        var lines = new List<string>();
        var index = GetOrBuildIndex(filePath);
        if (index == null || start >= index.TotalLines) return lines;

        var end = Math.Min(start + count, index.TotalLines);

        // 使用与索引构建相同的编码和 BOM 跳过逻辑
        var encoding = new UTF8Encoding(false);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
        SkipBom(fs);
        using var reader = new StreamReader(fs, encoding);

        // O(1) 跳转到目标行的字节偏移位置
        fs.Position = index.LineOffsets[start];
        // 清除 StreamReader 内部缓冲区中残留的旧位置数据
        reader.DiscardBufferedData();

        for (long i = start; i < end; i++)
        {
            var line = reader.ReadLine();
            if (line == null) break;
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>全文搜索，返回包含关键字的行号列表</summary>
    public async Task<List<long>> SearchAsync(string filePath, string keyword, bool caseSensitive)
    {
        var matchLines = new List<long>();
        if (!File.Exists(filePath) || string.IsNullOrEmpty(keyword))
            return matchLines;

        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // 使用与索引构建相同的编码和 BOM 跳过逻辑
        var encoding = new UTF8Encoding(false);
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
        SkipBom(fileStream);
        using var reader = new StreamReader(fileStream, encoding);
        long lineNumber = 0;

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;
            if (line.Contains(keyword, comparison))
                matchLines.Add(lineNumber);
            lineNumber++;
        }

        return matchLines;
    }

    /// <summary>返回当前缓存中已建立索引的文件数量</summary>
    public int GetIndexedFileCount()
    {
        lock (_cacheLock) { return _indexCache.Count; }
    }

    /// <summary>检查指定文件路径是否已建立索引缓存</summary>
    public bool HasIndex(string filePath)
    {
        lock (_cacheLock) { return _indexCache.ContainsKey(filePath); }
    }

    /// <summary>清空指定路径的索引缓存（删除文件后调用以释放内存）</summary>
    public void RemoveIndex(string filePath)
    {
        lock (_cacheLock)
        {
            _indexCache.Remove(filePath);
            _accessOrder.Remove(filePath);
        }
    }
}

/// <summary>文件基本信息 DTO，供 API 返回</summary>
public class LogFileInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long TotalLines { get; set; }
}
