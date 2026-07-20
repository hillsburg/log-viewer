using System.Diagnostics;

namespace LogViewer.Api.Services;

/// <summary>
/// 管理控制台服务状态监控：
/// 1. 服务运行时长（基于进程启动时间）
/// 2. 内存占用（进程工作集）
/// 3. CPU 使用率（两次采样计算）
/// 4. 上传文件列表（Data/uploads 目录）
/// </summary>
public class DashboardService
{
    private readonly FileService _fileService;
    private readonly HistoryService _historyService;
    private readonly string _uploadsDir;
    private readonly DateTime _startTime;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuCheck;
    private double _lastCpuPercent;
    private readonly object _cpuLock = new();

    public DashboardService(FileService fileService, HistoryService historyService, IWebHostEnvironment env)
    {
        _fileService = fileService;
        _historyService = historyService;
        _uploadsDir = Path.Combine(env.ContentRootPath, "Data", "uploads");

        var proc = Process.GetCurrentProcess();
        _startTime = proc.StartTime.ToUniversalTime();
        _lastCpuTime = proc.TotalProcessorTime;
        _lastCpuCheck = DateTime.UtcNow;
        _lastCpuPercent = 0;
    }

    /// <summary>获取当前服务运行状态（5 秒内数据视为有效，否则重新采样）</summary>
    public ServiceStatusDto GetStatus()
    {
        var proc = Process.GetCurrentProcess();
        var uptime = DateTime.UtcNow - _startTime;

        // CPU% 计算：两次采样间隔的 TotalProcessorTime 差值 / 时间差 / CPU 核心数
        lock (_cpuLock)
        {
            var currentCpuTime = proc.TotalProcessorTime;
            var currentTime = DateTime.UtcNow;
            var interval = (currentTime - _lastCpuCheck).TotalMilliseconds;

            if (interval >= 1000) // 至少间隔 1 秒才更新，避免频繁调用抖动
            {
                var cpuUsedMs = (currentCpuTime - _lastCpuTime).TotalMilliseconds;
                _lastCpuPercent = cpuUsedMs / interval / Environment.ProcessorCount * 100.0;
                _lastCpuTime = currentCpuTime;
                _lastCpuCheck = currentTime;
            }
        }

        return new ServiceStatusDto
        {
            UptimeSeconds = (long)uptime.TotalSeconds,
            MemoryMB = proc.WorkingSet64 / (1024.0 * 1024.0),
            CpuPercent = Math.Round(_lastCpuPercent, 1),
            IndexedFileCount = _fileService.GetIndexedFileCount(),
            Running = true,
            ProcessId = proc.Id,
            ThreadCount = proc.Threads.Count
        };
    }

    /// <summary>列出 Data/uploads 目录下所有文件</summary>
    public List<UploadedFileDto> GetUploadedFiles()
    {
        if (!Directory.Exists(_uploadsDir)) return new List<UploadedFileDto>();

        return Directory.GetFiles(_uploadsDir)
            .Select(path =>
            {
                var fi = new FileInfo(path);
                return new UploadedFileDto
                {
                    FileName = fi.Name,
                    Path = fi.FullName,
                    SizeBytes = fi.Length,
                    LastModified = fi.LastWriteTimeUtc,
                    IsIndexed = _fileService.HasIndex(fi.FullName)
                };
            })
            .OrderByDescending(f => f.LastModified)
            .ToList();
    }

    /// <summary>永久删除指定路径的文件，并从索引与历史记录中同步清除</summary>
    public int DeleteFiles(IEnumerable<string> paths)
    {
        int deleted = 0;
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                // 安全检查：只允许删除 uploads 目录下的文件
                if (!path.StartsWith(_uploadsDir, StringComparison.OrdinalIgnoreCase)) continue;

                File.Delete(path);
                _fileService.RemoveIndex(path);
                _historyService.Remove(path);  // 联动删除历史记录
                deleted++;
            }
            catch { }
        }
        return deleted;
    }
}

public class ServiceStatusDto
{
    public long UptimeSeconds { get; set; }
    public double MemoryMB { get; set; }
    public double CpuPercent { get; set; }
    public int IndexedFileCount { get; set; }
    public bool Running { get; set; }
    public int ProcessId { get; set; }
    public int ThreadCount { get; set; }
}

public class UploadedFileDto
{
    public string FileName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsIndexed { get; set; }
}
