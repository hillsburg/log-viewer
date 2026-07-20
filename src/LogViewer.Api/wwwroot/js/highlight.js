/**
 * Highlight - 日志行高亮引擎
 *
 * 职责：根据关键字配置对每行日志文本进行高亮渲染。
 *
 * 高亮规则优先级：
 * 1. 整行高亮关键字（highlightWholeLine = true）
 *    - 同一行命中多个整行关键字时，取列表中最后一个的颜色
 *    - 整行关键字自身不做内联标记，只渲染行底色
 * 2. 普通关键字
 *    - 在行底色之上做内联 <mark> 标记，颜色取关键字的 color
 *    - 每个关键字独立使用自身的 caseSensitive 和 matchMode 属性
 *
 * 匹配模式（matchMode）：
 * - contains   : 子串匹配（默认），用 String.includes / RegExp
 * - wholeWord  : 全字匹配，用 \b 关键字 \b 正则
 * - regex      : 用户自定义正则表达式
 *
 * 底层工具函数：
 * - escapeRegex  : 转义正则特殊字符
 * - escapeHtml   : 转义 HTML 实体，防止 XSS
 * - getContrastColor : 根据背景色亮度自动选择黑/白文字色
 * - buildKeywordRegex : 根据 matchMode 构建正则表达式
 */
const Highlight = (() => {
    // RegExp 缓存：key = "text|caseSensitive|matchMode"，避免重复构建
    const _regexCache = new Map();

    function escapeRegex(str) {
        return str.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }

    function escapeHtml(text) {
        return text
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    /**
     * 根据关键字的 matchMode 和 caseSensitive 构建正则表达式。
     * 通过缓存避免对相同关键字配置重复构建 RegExp。
     * regex 模式下直接使用用户输入的文本作为正则（已通过后端校验）。
     * wholeWord 模式用 \b 包裹转义后的文本。
     * contains 模式直接转义文本作为子串匹配。
     */
    function buildKeywordRegex(kw) {
        const cacheKey = `${kw.text}|${kw.caseSensitive ? 1 : 0}|${kw.matchMode || 'contains'}`;
        if (_regexCache.has(cacheKey)) return _regexCache.get(cacheKey);

        const flags = kw.caseSensitive ? 'g' : 'gi';
        let pattern;

        if (kw.matchMode === 'regex') {
            pattern = kw.text;
        } else if (kw.matchMode === 'wholeWord') {
            pattern = '\\b' + escapeRegex(kw.text) + '\\b';
        } else {
            pattern = escapeRegex(kw.text);
        }

        try {
            const regex = new RegExp(`(${pattern})`, flags);
            _regexCache.set(cacheKey, regex);
            return regex;
        } catch {
            _regexCache.set(cacheKey, null);
            return null;
        }
    }

    /** 清空 RegExp 缓存（关键字列表变更时调用） */
    function clearRegexCache() {
        _regexCache.clear();
    }

    /**
     * 判断一行文本是否命中某个关键字（用于整行高亮判定）。
     * 包含模式使用字符串方法直接匹配，避免正则开销。
     * 正则模式和全字模式使用带缓存的正则。
     */
    function matchKeyword(text, kw) {
        if (kw.matchMode === 'regex') {
            try {
                const flags = kw.caseSensitive ? '' : 'i';
                return new RegExp(kw.text, flags).test(text);
            } catch {
                return false;
            }
        }

        if (kw.matchMode === 'wholeWord') {
            const regex = buildKeywordRegex(kw);
            if (!regex) return false;
            const testFlags = kw.caseSensitive ? '' : 'i';
            try {
                return new RegExp(regex.source, testFlags).test(text);
            } catch {
                return false;
            }
        }

        // contains（默认）：用字符串方法直接匹配，比正则快
        return kw.caseSensitive
            ? text.includes(kw.text)
            : text.toLowerCase().includes(kw.text.toLowerCase());
    }

    /**
     * 对已 escape 的 HTML 内容逐关键字应用内联 <mark> 高亮。
     * 按关键字长度降序排列，避免短关键字先匹配导致长关键字被截取。
     */
    function applyInlineHighlight(lineHtml, lineRaw, keywords) {
        if (keywords.length === 0) return lineHtml;
        const sorted = [...keywords].sort((a, b) => b.text.length - a.text.length);
        let result = lineHtml;
        for (const kw of sorted) {
            const regex = buildKeywordRegex(kw);
            if (!regex) continue;

            const bgColor = kw.color;
            const textColor = getContrastColor(bgColor);
            result = result.replace(regex, (match) =>
                `<mark style="background:${bgColor};color:${textColor}">${match}</mark>`
            );
        }
        return result;
    }

    /**
     * 对单行文本进行完整高亮处理。
     *
     * @param {string} line - 原始日志行文本（未转义）
     * @param {Array} keywords - 关键字配置列表
     * @returns {string} 高亮后的 HTML 字符串
     */
    function highlightLine(line, keywords) {
        const enabled = keywords.filter(k => k.enabled);
        if (enabled.length === 0) return escapeHtml(line);

        const wholeLineKeywords = enabled.filter(k => k.highlightWholeLine);
        const normalKeywords = enabled.filter(k => !k.highlightWholeLine);

        // 整行高亮：找到列表中最后一个命中的整行关键字，用它的颜色和浓度
        const matchedWholeKws = wholeLineKeywords.filter(kw => matchKeyword(line, kw));
        const matchedWholeKw = matchedWholeKws.length > 0 ? matchedWholeKws[matchedWholeKws.length - 1] : null;

        if (matchedWholeKw) {
            // 将浓度百分比（5-80）转换为 8 位 hex alpha（如 30% → '4d'）
            const opacity = (matchedWholeKw.wholeLineOpacity ?? 30) / 100;
            const opacityHex = Math.round(opacity * 255).toString(16).padStart(2, '0');
            const lineBg = matchedWholeKw.color + opacityHex;

            let content = escapeHtml(line);
            // 普通关键字在整行底色之上做内联标记
            if (normalKeywords.length > 0) {
                content = applyInlineHighlight(content, line, normalKeywords);
            }
            // width:100% 确保整行背景色铺满可视宽度
            return `<span style="background:${lineBg};display:inline-block;width:100%">${content}</span>`;
        }

        // 仅普通关键字高亮
        if (normalKeywords.length > 0) {
            return applyInlineHighlight(escapeHtml(line), line, normalKeywords);
        }

        return escapeHtml(line);
    }

    /**
     * 根据 hex 颜色的感知亮度（ITU-R BT.601）返回黑色或白色文字，
     * 保证在各种背景色下文字都有足够对比度。
     */
    function getContrastColor(hexColor) {
        const hex = hexColor.replace('#', '');
        const r = parseInt(hex.substring(0, 2), 16);
        const g = parseInt(hex.substring(2, 4), 16);
        const b = parseInt(hex.substring(4, 6), 16);
        const luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
        return luminance > 0.5 ? '#000000' : '#ffffff';
    }

    return { highlightLine, escapeHtml, clearRegexCache };
})();
