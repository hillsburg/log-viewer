namespace LogViewer.Api.Models;

/// <summary>
/// 关键字配置响应 DTO（非持久化模型，仅用于 API 返回）。
/// 主题存储在 SQLite settings 表中。
/// </summary>
public class KeywordConfig
{
    public List<Keyword> Keywords { get; set; } = new();
    public string Theme { get; set; } = "dark";
}
