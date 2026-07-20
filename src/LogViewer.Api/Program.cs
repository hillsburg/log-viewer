using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LogViewer.Api.Endpoints;
using LogViewer.Api.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

// ========== 依赖注入 ==========
// 数据库统一初始化器，必须最先注册，其他 Service 依赖它获取连接字符串
var dbInit = new DatabaseInitializer(builder.Environment);
dbInit.Initialize();
builder.Services.AddSingleton(dbInit);

builder.Services.AddSingleton<KeywordService>();
builder.Services.AddSingleton<FileService>();
builder.Services.AddSingleton<HistoryService>();
builder.Services.AddSingleton<DashboardService>();

// 文件上传大小限制：500MB（解决大 log 文件上传失败）
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 500L * 1024 * 1024;
});
builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = 500L * 1024 * 1024;
});

// CORS：允许任意来源访问（单机部署，无跨域限制需求）
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

// 启动时清理 uploads 目录中不在 SQLite 历史记录里的孤立上传文件
CleanupOrphanedUploads(app);

app.UseCors();

// ========== 注册 API 路由 ==========
app.MapKeywordEndpoints();
app.MapFileEndpoints();
app.MapHistoryEndpoints();
app.MapDashboardEndpoints();

// ========== 静态文件服务 ==========
// 前端文件已放置在 wwwroot/ 目录下，ASP.NET Core 默认从该目录提供静态文件。
// UseDefaultFiles 必须在 UseStaticFiles 之前，访问 / 时自动返回 index.html。
app.UseDefaultFiles();
app.UseStaticFiles();

// ========== 监听地址 ==========
// 优先级：ASPNETCORE_URLS / LOGVIEWER_URL（托盘传入）> appsettings LogViewer:Port > 默认 5173（占用则自动顺延）
var url = ResolveListenUrl(builder.Configuration);
app.Urls.Clear();
app.Urls.Add(url);

WriteEndpointFile(app.Environment, url);
app.Lifetime.ApplicationStopping.Register(() => ClearEndpointFile(app.Environment));

// 由托盘启动器拉起时不自动弹浏览器（避免崩溃重启反复开页）；直接运行 Api.exe 时仍自动打开
var launchedByTray = string.Equals(
    Environment.GetEnvironmentVariable("LOGVIEWER_LAUNCHED_BY_TRAY"),
    "1",
    StringComparison.Ordinal);

if (!launchedByTray)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Open browser failed: {ex.Message}");
            }
        });
    });
}

app.Run();

// ========== URL / 端口解析 ==========
static string ResolveListenUrl(IConfiguration config)
{
    var fromEnv = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
        ?? Environment.GetEnvironmentVariable("LOGVIEWER_URL");
    if (!string.IsNullOrWhiteSpace(fromEnv))
        return fromEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];

    var host = config.GetValue("LogViewer:Host", "127.0.0.1") ?? "127.0.0.1";
    var preferred = config.GetValue("LogViewer:Port", 5173);
    if (preferred is <= 0 or >= 65536)
        preferred = 5173;

    var port = FindAvailablePort(preferred, 100);
    return $"http://{host}:{port}";
}

static int FindAvailablePort(int preferred, int range)
{
    for (var port = preferred; port < preferred + range && port <= 65535; port++)
    {
        if (IsPortAvailable(port))
            return port;
    }

    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var ephemeral = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return ephemeral;
}

static bool IsPortAvailable(int port)
{
    try
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
        return true;
    }
    catch
    {
        return false;
    }
}

static void WriteEndpointFile(IHostEnvironment env, string url)
{
    try
    {
        var dir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "endpoint.json");
        var json = JsonSerializer.Serialize(new
        {
            url,
            pid = Environment.ProcessId,
            writtenAt = DateTime.UtcNow
        });
        File.WriteAllText(path, json);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"WriteEndpointFile failed: {ex.Message}");
    }
}

static void ClearEndpointFile(IHostEnvironment env)
{
    try
    {
        var path = Path.Combine(env.ContentRootPath, "Data", "endpoint.json");
        if (File.Exists(path))
            File.Delete(path);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ClearEndpointFile failed: {ex.Message}");
    }
}

// ========== 孤立上传文件清理（基于 SQLite 历史记录） ==========
static void CleanupOrphanedUploads(WebApplication app)
{
    try
    {
        var historyService = app.Services.GetRequiredService<HistoryService>();
        var uploadsDir = Path.Combine(app.Environment.ContentRootPath, "Data", "uploads");
        if (!Directory.Exists(uploadsDir)) return;

        var allRecords = historyService.GetAll();
        var referencedPaths = new HashSet<string>(
            allRecords.Where(r => r.Source == "upload").Select(r => r.FilePath),
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(uploadsDir))
        {
            if (!referencedPaths.Contains(file))
            {
                try { File.Delete(file); }
                catch (Exception ex) { Console.Error.WriteLine($"Delete orphan upload failed: {file} — {ex.Message}"); }
            }
        }

        if (!Directory.EnumerateFiles(uploadsDir).Any())
        {
            try { Directory.Delete(uploadsDir); }
            catch (Exception ex) { Console.Error.WriteLine($"Delete empty uploads dir failed: {ex.Message}"); }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"CleanupOrphanedUploads failed: {ex.Message}");
    }
}
