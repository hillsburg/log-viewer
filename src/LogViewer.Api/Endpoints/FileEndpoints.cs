using LogViewer.Api.Services;

namespace LogViewer.Api.Endpoints;

/// <summary>
/// 文件相关 REST API。
/// - GET  /api/file/info    : 获取文件信息（名称、大小、行数），首次访问时构建行偏移索引
/// - GET  /api/file/lines   : 按行号范围读取内容，通过 Seek 实现 O(1) 跳转
/// - GET  /api/file/search  : 全文搜索，返回匹配行号列表
/// - POST /api/file/upload  : 接收浏览器上传的文件，保存到 Data/uploads 并保持原始文件名，
///                             同名自动添加 _1、_2 后缀；最大支持 500MB（Program.cs 中配置）
/// </summary>
public static class FileEndpoints
{
    public static void MapFileEndpoints(this WebApplication app)
    {
        // 获取文件信息并自动加入历史记录
        app.MapGet("/api/file/info", (FileService service, HistoryService history, string path) =>
        {
            try
            {
                var info = service.GetFileInfo(path);
                history.Add(info.FilePath, info.FileName, info.FileSize, info.TotalLines, "local");
                return Results.Ok(info);
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new { Error = "File not found" });
            }
        });

        // 按行号范围读取内容，count 上限 500 行防止单次传输过大
        app.MapGet("/api/file/lines", (FileService service, string path, long start, int count) =>
        {
            if (count > 500) count = 500;
            var lines = service.GetLines(path, start, count);
            return Results.Ok(new { Lines = lines, Start = start });
        });

        // 全文搜索，支持区分大小写
        app.MapGet("/api/file/search", async (FileService service, string path, string keyword, bool caseSensitive) =>
        {
            var lineNumbers = await service.SearchAsync(path, keyword, caseSensitive);
            return Results.Ok(new { LineNumbers = lineNumbers, Total = lineNumbers.Count });
        });

        // 文件上传：保存到 Data/uploads，文件名同名自动递增序号
        app.MapPost("/api/file/upload", async (IWebHostEnvironment env, FileService service, HistoryService history, HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { Error = "No file uploaded" });

            // 仅允许 .log 和 .txt 文件
            var fileName = file.FileName;
            var lowerName = fileName.ToLowerInvariant();
            if (!lowerName.EndsWith(".log") && !lowerName.EndsWith(".txt"))
                return Results.BadRequest(new { Error = "Only .log and .txt files are supported" });

            var uploadsDir = Path.Combine(env.ContentRootPath, "Data", "uploads");
            if (!Directory.Exists(uploadsDir)) Directory.CreateDirectory(uploadsDir);

            // 生成不冲突的文件名：app.log → app_1.log → app_2.log ...
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var savedName = fileName;
            var savedPath = Path.Combine(uploadsDir, savedName);
            var counter = 1;
            while (File.Exists(savedPath))
            {
                savedName = $"{baseName}_{counter}{ext}";
                savedPath = Path.Combine(uploadsDir, savedName);
                counter++;
            }

            using (var stream = new FileStream(savedPath, FileMode.Create, FileAccess.Write))
            {
                await file.CopyToAsync(stream);
            }

            try
            {
                var info = service.GetFileInfo(savedPath);
                history.Add(info.FilePath, info.FileName, info.FileSize, info.TotalLines, "upload");
                return Results.Ok(info);
            }
            catch
            {
                return Results.BadRequest(new { Error = "Failed to read file" });
            }
        });
    }
}
