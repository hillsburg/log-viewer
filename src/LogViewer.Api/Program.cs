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

// ========== 监听地址 & 自动打开浏览器 ==========
var url = "http://localhost:5173";
app.Urls.Add(url);

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
        catch { }
    });
});

app.Run();

// ========== 孤立上传文件清理（基于 SQLite 历史记录） ==========
static void CleanupOrphanedUploads(WebApplication app)
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
            try { File.Delete(file); } catch { }
    }

    if (!Directory.EnumerateFiles(uploadsDir).Any())
        try { Directory.Delete(uploadsDir); } catch { }
}
