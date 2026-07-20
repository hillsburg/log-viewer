using LogViewer.Api.Services;

namespace LogViewer.Api.Endpoints;

/// <summary>
/// 文件历史记录 REST API，路由前缀 /api/history。
/// - GET    /?sort=recent|frequent&search=xxx  : 获取历史记录
/// - DELETE /?path=xxx                          : 删除单条记录
/// - DELETE /clear                              : 清空全部
/// </summary>
public static class HistoryEndpoints
{
    public static void MapHistoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/history");

        group.MapGet("/", (HistoryService service, string? sort, string? search) =>
            Results.Ok(service.GetAll(sort ?? "recent", search)));

        group.MapDelete("/", (HistoryService service, string path) =>
        {
            var removed = service.Remove(path);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        group.MapDelete("/clear", (HistoryService service) =>
        {
            service.ClearAll();
            return Results.NoContent();
        });
    }
}