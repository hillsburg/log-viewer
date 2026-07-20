namespace LogViewer.Api.Models;

/// <summary>
/// 关键字配置项。
/// 每个关键字可独立控制：文本、颜色、启用状态、大小写敏感性、是否整行高亮、整行背景透明度。
/// </summary>
public class Keyword
{
    /// <summary>唯一标识，SQLite 自增主键</summary>
    public int Id { get; set; }

    /// <summary>匹配文本</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>关键字颜色（hex 格式，如 #ff4444），用于内联标记文字背景色</summary>
    public string Color { get; set; } = "#ff4444";

    /// <summary>是否启用：禁用后该关键字不参与任何高亮</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 区分大小写：true 时仅匹配完全一致的大小写形式，false 时忽略大小写。
    /// 每个关键字独立使用该属性，不依赖全局 GlobalCaseSensitive 开关。
    /// </summary>
    public bool CaseSensitive { get; set; }

    /// <summary>
    /// 是否整行高亮：true 时整行背景着色，自身不做内联标记；
    /// 同一行有多个整行关键字命中时，取列表中最后一个的颜色。
    /// </summary>
    public bool HighlightWholeLine { get; set; }

    /// <summary>
    /// 整行背景色透明度（5-80），百分比值。
    /// 5 表示几乎透明，80 表示高浓度不透明。默认 30，兼顾可读性和可见性。
    /// </summary>
    public int WholeLineOpacity { get; set; } = 30;

    /// <summary>
    /// 匹配模式：contains（子串匹配）、wholeWord（全字匹配 \b）、regex（正则表达式）。
    /// 每个关键字独立使用该属性，默认 contains。
    /// </summary>
    public string MatchMode { get; set; } = "contains";

    /// <summary>创建时间（ISO 8601），导入时按此字段排序</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>最后修改时间（ISO 8601）</summary>
    public string UpdatedAt { get; set; } = string.Empty;
}
