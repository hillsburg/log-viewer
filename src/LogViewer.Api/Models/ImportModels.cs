namespace LogViewer.Api.Models;

/// <summary>
/// 导入用的关键字数据。
/// 包含 CreatedAt 用于导入时按原顺序排序，但不会直接使用该时间戳写入数据库。
/// MatchMode 用于指定匹配模式（contains / wholeWord / regex），默认 contains。
/// </summary>
public record ImportKeyword(string Text, string Color, bool Enabled, bool CaseSensitive, bool HighlightWholeLine, int WholeLineOpacity, string CreatedAt, string? MatchMode = null);

/// <summary>导入结果统计</summary>
public record ImportResult(int Added, int Skipped, int Overwritten);
