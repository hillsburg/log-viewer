using LogViewer.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogViewer.Api.Endpoints;

/// <summary>
/// 管理控制台 API，路由前缀 /api/dashboard。
/// - GET  /status  : 服务运行状态（运行时长、内存、CPU、索引文件数）
/// - GET  /files   : 已上传文件列表
/// - DELETE /files  : 批量删除文件（请求体 { paths: [...] }）
/// </summary>
public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/dashboard");

        group.MapGet("/status", (DashboardService svc) => Results.Ok(svc.GetStatus()));

        group.MapGet("/files", (DashboardService svc) => Results.Ok(svc.GetUploadedFiles()));

        group.MapDelete("/files", (DashboardService svc, [FromBody] DeleteFilesRequest request) =>
        {
            if (request.Paths == null || request.Paths.Count == 0)
                return Results.BadRequest(new { error = "No paths provided" });

            var deleted = svc.DeleteFiles(request.Paths);
            return Results.Ok(new { deleted, requested = request.Paths.Count });
        });
    }
}

public record DeleteFilesRequest(List<string> Paths);
