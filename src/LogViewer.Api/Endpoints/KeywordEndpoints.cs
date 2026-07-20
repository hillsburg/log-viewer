using LogViewer.Api.Models;
using LogViewer.Api.Services;

namespace LogViewer.Api.Endpoints;

/// <summary>
/// 关键字配置 REST API，路由前缀 /api/keywords。
/// - CRUD    : GET/ POST/ PUT/ DELETE /api/keywords[/{id}]
/// - 导出    : GET  /api/keywords/export
/// - 导入    : POST /api/keywords/import
/// - 主题    : GET/ PUT /api/keywords/theme
/// </summary>
public static class KeywordEndpoints
{
    public static void MapKeywordEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/keywords");

        group.MapGet("/", (KeywordService service) => Results.Ok(service.GetAll()));

        group.MapPost("/", (KeywordService service, KeywordCreateRequest request) =>
        {
            var keyword = service.Add(
                request.Text, request.Color, request.CaseSensitive,
                request.HighlightWholeLine, request.WholeLineOpacity, request.MatchMode);
            return Results.Created($"/api/keywords/{keyword.Id}", keyword);
        });

        group.MapPut("/{id:int}", (KeywordService service, int id, KeywordUpdateRequest request) =>
        {
            var keyword = service.Update(
                id, request.Text, request.Color, request.Enabled,
                request.CaseSensitive, request.HighlightWholeLine, request.WholeLineOpacity, request.MatchMode);
            return keyword is not null ? Results.Ok(keyword) : Results.NotFound();
        });

        group.MapDelete("/{id:int}", (KeywordService service, int id) =>
        {
            var deleted = service.Delete(id);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        // ========== 导出 / 导入 ==========

        // 导出：返回全部关键字 JSON 数组，按 CreatedAt 排序
        group.MapGet("/export", (KeywordService service) =>
        {
            var keywords = service.GetAll(); // 已按 CreatedAt ASC 排序
            return Results.Ok(keywords);
        });

        // 导入：接收 JSON 数组，按 mode 和 conflictAction 处理
        group.MapPost("/import", (KeywordService service, ImportRequest request) =>
        {
            if (request.Keywords == null || request.Keywords.Count == 0)
                return Results.BadRequest(new { error = "No keywords provided" });

            var result = service.Import(request.Keywords, request.Mode, request.ConflictAction);
            return Results.Ok(result);
        });

        group.MapGet("/theme", (KeywordService service) => Results.Ok(new { Theme = service.GetTheme() }));
        group.MapPut("/theme", (KeywordService service, SetThemeRequest request) =>
        {
            service.SetTheme(request.Theme);
            return Results.Ok();
        });

        // ========== 通用设置（语言等） ==========
        group.MapGet("/settings/{key}", (KeywordService service, string key) =>
        {
            var value = service.GetSetting(key);
            return Results.Ok(new { key, value });
        });

        group.MapPut("/settings/{key}", async (KeywordService service, string key, SetSettingRequest request) =>
        {
            await service.SetSettingAsync(key, request.Value);
            return Results.Ok();
        });
    }
}

public record SetSettingRequest(string Value);

public record KeywordCreateRequest(string Text, string Color, bool CaseSensitive, bool HighlightWholeLine, int WholeLineOpacity = 30, string MatchMode = "contains");
public record KeywordUpdateRequest(string Text, string Color, bool Enabled, bool CaseSensitive, bool HighlightWholeLine, int WholeLineOpacity = 30, string MatchMode = "contains");
public record SetThemeRequest(string Theme);

/// <summary>导入请求</summary>
/// <param name="Keywords">要导入的关键字列表</param>
/// <param name="Mode">replace（替换全部）或 merge（合并）</param>
/// <param name="ConflictAction">merge 模式下冲突时：skip（跳过）或 overwrite（覆盖）</param>
public record ImportRequest(List<ImportKeyword> Keywords, string Mode, string ConflictAction);
