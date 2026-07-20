/**
 * i18n - 国际化模块
 *
 * 功能：
 * - 从 JSON 文件加载翻译资源
 * - 提供 t(key, params) 翻译函数（支持占位符替换）
 * - 自动处理 data-i18n 属性的静态 HTML 翻译
 * - 语言切换时自动更新所有已翻译元素
 * - 持久化语言偏好到后端 settings 表
 * - 首次访问时跟随浏览器语言自动选择
 *
 * 翻译键名采用分组格式：'group.key'
 * 占位符使用 {name} 格式：t('toast.opened', { fileName: 'app.log' })
 *
 * 使用方式：
 *   静态 HTML：<span data-i18n="toolbar.openFile">打开文件</span>
 *   动态 JS  ：showToast(t('toast.opened', { fileName: 'app.log' }))
 */
const I18n = (() => {
    let translations = {};
    let currentLang = 'zh';
    let _onLangChange = null;

    /**
     * 加载翻译资源并应用语言设置。
     * 1. 从后端读取已保存的语言偏好
     * 2. 若无偏好，检测浏览器语言
     * 3. 加载对应翻译 JSON
     * 4. 应用 data-i18n 属性
     */
    async function init() {
        // 尝试从后端读取已保存的语言偏好
        try {
            const resp = await fetch('/api/keywords/settings/Language');
            if (resp.ok) {
                const data = await resp.json();
                if (data.value) {
                    currentLang = data.value;
                } else {
                    detectBrowserLanguage();
                }
            } else {
                detectBrowserLanguage();
            }
        } catch {
            detectBrowserLanguage();
        }

        await loadTranslations(currentLang);
        applyDataAttributes();
    }

    /** 检测浏览器语言，中文环境默认 zh，其他默认 en */
    function detectBrowserLanguage() {
        const lang = (navigator.language || navigator.userLanguage || 'en').toLowerCase();
        currentLang = lang.startsWith('zh') ? 'zh' : 'en';
    }

    /** 加载指定语言的翻译 JSON */
    async function loadTranslations(lang) {
        try {
            const resp = await fetch(`/i18n/${lang}.json`);
            if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
            translations = await resp.json();
        } catch (err) {
            console.error(`Failed to load translations for ${lang}:`, err);
            if (lang !== 'zh') {
                // 降级到中文
                currentLang = 'zh';
                try {
                    const resp = await fetch('/i18n/zh.json');
                    translations = await resp.json();
                } catch { }
            }
        }
    }

    /**
     * 翻译函数：根据 key 返回翻译文本，支持 {name} 占位符替换。
     * @param {string} key - 分组键名，如 'toast.opened'
     * @param {Object} [params] - 占位符参数，如 { fileName: 'app.log' }
     * @returns {string} 翻译后的文本
     */
    function t(key, params) {
        const value = getNestedValue(translations, key);
        if (value === undefined) return key; // 找不到翻译时返回键名本身
        if (!params) return value;
        return Object.entries(params).reduce(
            (str, [k, v]) => str.replace(new RegExp(`\\{${k}\\}`, 'g'), v),
            value
        );
    }

    /** 从嵌套对象中按 'a.b.c' 路径取值 */
    function getNestedValue(obj, path) {
        return path.split('.').reduce((o, k) => o && o[k], obj);
    }

    /**
     * 应用 data-i18n 属性到所有静态 HTML 元素。
     * 遍历所有带 data-i18n 的元素，用 t() 替换其 textContent。
     */
    function applyDataAttributes() {
        document.querySelectorAll('[data-i18n]').forEach(el => {
            const key = el.getAttribute('data-i18n');
            const translated = t(key);
            if (translated !== key) {
                el.textContent = translated;
            }
        });

        // 处理 placeholder 属性
        document.querySelectorAll('[data-i18n-placeholder]').forEach(el => {
            const key = el.getAttribute('data-i18n-placeholder');
            const translated = t(key);
            if (translated !== key) {
                el.placeholder = translated;
            }
        });

        // 处理 title 属性
        document.querySelectorAll('[data-i18n-title]').forEach(el => {
            const key = el.getAttribute('data-i18n-title');
            const translated = t(key);
            if (translated !== key) {
                el.title = translated;
            }
        });
    }

    /**
     * 切换语言。
     * @param {string} lang - 'zh' 或 'en'
     */
    async function setLanguage(lang) {
        if (lang === currentLang) return;
        currentLang = lang;
        await loadTranslations(lang);
        applyDataAttributes();
        updateLangButton();

        // 通知订阅者（app.js 等）语言已切换，刷新动态内容
        if (_onLangChange) _onLangChange(lang);

        // 持久化到后端
        try {
            await fetch('/api/keywords/settings/Language', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ value: lang })
            });
        } catch { }
    }

    /** 获取当前语言 */
    function getLanguage() {
        return currentLang;
    }

    /** 更新语言切换按钮显示文字 */
    function updateLangButton() {
        const btn = document.getElementById('btn-lang-toggle');
        if (btn) {
            btn.textContent = currentLang === 'zh' ? 'EN' : '中';
            btn.title = currentLang === 'zh' ? 'Switch to English' : '切换到中文';
        }
    }

    /** 注册语言切换回调（用于 app.js 刷新动态内容） */
    function onLanguageChange(cb) {
        _onLangChange = cb;
    }

    return { init, t, setLanguage, getLanguage, updateLangButton, onLanguageChange };
})();

/** 全局翻译函数：app.js / keyword-panel.js 等模块直接调用 t() */
const t = I18n.t;
