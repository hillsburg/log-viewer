/**
 * Toast 提示：在页面底部居中显示消失型消息。
 * 支持消息队列：多次调用会创建独立 toast 元素，互不干扰。
 * duration 单位毫秒，默认 2 秒。
 */
function showToast(message, duration = 2000) {
    const wrap = document.getElementById('toast-wrap');
    const toast = document.createElement('div');
    toast.className = 'toast';
    toast.textContent = message;
    wrap.appendChild(toast);

    // 强制 reflow 后添加 show 类，触发 CSS transition
    requestAnimationFrame(() => toast.classList.add('show'));

    setTimeout(() => {
        toast.classList.remove('show');
        toast.addEventListener('transitionend', () => toast.remove());
    }, duration);
}

/**
 * Loading Overlay API
 *
 * 全局统一耗时操作反馈，覆盖整个应用区域。
 * 支持纯 spinner（索引构建、搜索）和带进度条（上传）两种模式。
 *
 * showLoadingOverlay(text, showProgress = false)
 * updateLoadingProgress(pct)   - pct: 0-100 的百分比
 * updateLoadingText(text)      - 动态更新描述文字
 * hideLoadingOverlay()
 */
function showLoadingOverlay(text, showProgress = false) {
    const overlay = document.getElementById('loading-overlay');
    const textEl = document.getElementById('loading-text');
    const progressWrap = document.getElementById('loading-progress-wrap');
    const bar = document.getElementById('loading-progress-bar');
    const pctEl = document.getElementById('loading-progress-pct');
    textEl.textContent = text;
    progressWrap.style.display = showProgress ? 'block' : 'none';
    if (showProgress) {
        bar.style.setProperty('--progress', '0%');
        pctEl.textContent = '0%';
    }
    overlay.classList.add('active');
}

function updateLoadingProgress(pct) {
    const bar = document.getElementById('loading-progress-bar');
    const pctEl = document.getElementById('loading-progress-pct');
    const clamped = Math.min(100, Math.max(0, pct));
    bar.style.setProperty('--progress', clamped + '%');
    pctEl.textContent = Math.round(clamped) + '%';
}

function updateLoadingText(text) {
    document.getElementById('loading-text').textContent = text;
}

function hideLoadingOverlay() {
    document.getElementById('loading-overlay').classList.remove('active');
}

/**
 * App - 应用主控制模块
 *
 * 职责：
 * - 初始化各子模块（VirtualScroll、KeywordPanel）
 * - 绑定所有 DOM 事件（文件打开、拖拽、搜索、侧边栏折叠、主题切换）
 * - 文件上传流程：浏览器选取/拖拽 → POST /api/file/upload → 更新状态
 * - 历史记录管理（从后端加载和渲染）
 *
 * 当前文件路径状态：
 * - currentFilePath : 当前打开的文件路径（用于搜索等）
 * - lastFilePath    : 最近一次打开的文件路径（预填 prompt 用）
 * - currentTheme    : 当前主题标识（dark / light）
 */
const App = (() => {
    let currentFilePath = null;
    let lastFilePath = null;
    let currentTheme = 'dark';

    /** 应用入口：初始化子模块 → 加载主题 → 加载关键字 → 加载历史 → 绑定事件 */
    async function init() {
        VirtualScroll.init(
            document.getElementById('log-scroll'),
            document.getElementById('log-spacer'),
            document.getElementById('log-lines')
        );

        await I18n.init();
        await loadTheme();
        const { keywords } = await KeywordPanel.load();

        KeywordPanel.setUpdateCallback((kw) => {
            VirtualScroll.updateKeywords(kw);
        });

        // 语言切换时刷新关键字面板（按钮标题等动态 DOM）
        I18n.onLanguageChange(() => {
            KeywordPanel.load();
            applyTheme(currentTheme);
        });

        await loadHistory();
        bindEvents();
    }

    /** 从后端加载主题设置 */
    async function loadTheme() {
        try {
            const resp = await fetch('/api/keywords/theme');
            const data = await resp.json();
            currentTheme = data.theme || 'dark';
            applyTheme(currentTheme);
        } catch (e) {
            applyTheme('dark');
        }
    }

    /** 应用主题到 <html> 元素并更新主题切换按钮图标 */
    function applyTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        const btn = document.getElementById('btn-theme-toggle');
        if (btn) {
            btn.textContent = theme === 'dark' ? '☀' : '☾';
            btn.title = theme === 'dark' ? t('toolbar.themeDark') : t('toolbar.themeLight');
        }
        I18n.updateLangButton();
    }

    /** 切换主题并持久化到后端 */
    async function toggleTheme() {
        currentTheme = currentTheme === 'dark' ? 'light' : 'dark';
        applyTheme(currentTheme);
        await fetch('/api/keywords/theme', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ theme: currentTheme })
        });
    }

    /** 绑定所有按钮、输入框的交互事件 */
    function bindEvents() {
        // 打开文件：点击按钮触发隐藏的 file input
        document.getElementById('btn-open').addEventListener('click', () => {
            document.getElementById('file-input').click();
        });

        // 文件选择后上传；重置 input.value 以支持重复选同一文件
        document.getElementById('file-input').addEventListener('change', (e) => {
            const files = e.target.files;
            if (files.length > 0) uploadFile(files[0]);
            e.target.value = '';
        });

        // 添加关键字（点击按钮或按回车）
        document.getElementById('btn-add-keyword').addEventListener('click', () => {
            const textInput = document.getElementById('new-keyword-text');
            const colorInput = document.getElementById('new-keyword-color');
            if (textInput.value.trim()) {
                KeywordPanel.add(textInput.value, colorInput.value);
                textInput.value = '';
            }
        });

        // 导出关键字配置
        document.getElementById('btn-export-keywords').addEventListener('click', exportKeywords);

        // 导入关键字配置
        document.getElementById('btn-import-keywords').addEventListener('click', () => {
            document.getElementById('keyword-file-input').click();
        });
        document.getElementById('keyword-file-input').addEventListener('change', handleImportFile);

        // 导入对话框：切换合并模式时显示冲突选项
        document.querySelectorAll('input[name="import-mode"]').forEach(radio => {
            radio.addEventListener('change', () => {
                const conflictDiv = document.getElementById('import-conflict-options');
                conflictDiv.style.display = radio.value === 'merge' && radio.checked ? 'flex' : 'none';
            });
        });
        document.getElementById('new-keyword-text').addEventListener('keydown', (e) => {
            if (e.key === 'Enter') document.getElementById('btn-add-keyword').click();
        });

        // 搜索：点击按钮或按回车
        document.getElementById('btn-search').addEventListener('click', () => doSearch());
        document.getElementById('search-input').addEventListener('keydown', (e) => {
            if (e.key === 'Enter') doSearch();
        });

        // 搜索导航：上一个 / 下一个
        document.getElementById('btn-prev').addEventListener('click', () => navigateSearch('prev'));
        document.getElementById('btn-next').addEventListener('click', () => navigateSearch('next'));

        // 清除搜索高亮
        document.getElementById('btn-clear-search').addEventListener('click', () => clearSearch());

        // 侧边栏折叠 / 展开
        document.getElementById('btn-toggle-sidebar').addEventListener('click', () => {
            document.getElementById('sidebar').style.display = 'none';
            document.getElementById('sidebar-collapsed').style.display = 'flex';
        });
        document.getElementById('btn-expand-sidebar').addEventListener('click', () => {
            document.getElementById('sidebar').style.display = 'flex';
            document.getElementById('sidebar-collapsed').style.display = 'none';
        });

        // 历史面板切换
        document.getElementById('btn-toggle-history').addEventListener('click', () => {
            const panel = document.getElementById('history-panel');
            panel.classList.toggle('open');
            document.getElementById('btn-toggle-history').classList.toggle('active', panel.classList.contains('open'));
            if (panel.classList.contains('open')) loadHistory();
        });

        // 关闭历史面板
        document.getElementById('btn-close-history').addEventListener('click', () => {
            document.getElementById('history-panel').classList.remove('open');
            document.getElementById('btn-toggle-history').classList.remove('active');
        });

        // 语言切换按钮
        document.getElementById('btn-lang-toggle').addEventListener('click', () => {
            const newLang = I18n.getLanguage() === 'zh' ? 'en' : 'zh';
            I18n.setLanguage(newLang);
        });

        // 历史记录清空（二次确认）
        document.getElementById('btn-clear-history').addEventListener('click', () => {
            if (!confirm(t('toast.confirmClearHistory'))) return;
            fetch('/api/history/clear', { method: 'DELETE' }).then(() => loadHistory());
        });

        // 历史搜索（防抖 300ms）
        let historySearchTimer = null;
        document.getElementById('history-search').addEventListener('input', (e) => {
            clearTimeout(historySearchTimer);
            historySearchTimer = setTimeout(() => loadHistory(), 300);
        });

        // 历史排序切换
        document.querySelectorAll('.history-tab').forEach(tab => {
            tab.addEventListener('click', function() {
                document.querySelectorAll('.history-tab').forEach(t => t.classList.remove('active'));
                this.classList.add('active');
                loadHistory();
            });
        });

        // 主题切换
        document.getElementById('btn-theme-toggle').addEventListener('click', toggleTheme);

        initDragDrop();
    }

    /**
     * 初始化拖拽打开文件功能。
     * 使用 dragCounter 防止 dragleave 误触（子元素进出时 dragleave 会触发）。
     */
    function initDragDrop() {
        const viewer = document.getElementById('log-viewer');
        const overlay = document.getElementById('drop-overlay');
        let dragCounter = 0;

        viewer.addEventListener('dragenter', (e) => {
            e.preventDefault();
            dragCounter++;
            overlay.classList.add('active');
            viewer.classList.add('drag-over');
        });

        viewer.addEventListener('dragleave', (e) => {
            e.preventDefault();
            dragCounter--;
            if (dragCounter === 0) {
                overlay.classList.remove('active');
                viewer.classList.remove('drag-over');
            }
        });

        viewer.addEventListener('dragover', (e) => {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'copy';
        });

        // 放下文件：仅接受 .log / .txt，直接走 uploadFile 流程
        viewer.addEventListener('drop', (e) => {
            e.preventDefault();
            dragCounter = 0;
            overlay.classList.remove('active');
            viewer.classList.remove('drag-over');

            const files = e.dataTransfer.files;
            if (files.length > 0) {
                const file = files[0];
                const lowerName = file.name.toLowerCase();
                if (lowerName.endsWith('.log') || lowerName.endsWith('.txt')) {
                    uploadFile(file);
                } else {
                    showToast(t('toast.dropLogTxt'));
                }
            }
        });
    }

    /**
     * 文件上传：将浏览器读取的文件内容 POST 到后端，
     * 后端保存到 Data/uploads 并返回文件信息。
     * 浏览器 <input type="file"> 不暴露真实路径（fakepath 问题），
     * 因此改为上传文件内容而非路径。
     *
     * 使用 XHR 而非 fetch，以获得 upload.onprogress 上传进度事件。
     */
    function uploadFile(file) {
        const formData = new FormData();
        formData.append('file', file);

        showLoadingOverlay(t('loading.uploading', { fileName: file.name }), true);

        const xhr = new XMLHttpRequest();
        xhr.open('POST', '/api/file/upload');

        xhr.upload.onprogress = (e) => {
            if (e.lengthComputable) {
                const pct = (e.loaded / e.total) * 100;
                updateLoadingProgress(pct);
                updateLoadingText(t('loading.uploadProgress', { fileName: file.name, loaded: formatSize(e.loaded), total: formatSize(e.total) }));
            }
        };

        xhr.onload = async () => {
            hideLoadingOverlay();
            if (xhr.status >= 200 && xhr.status < 300) {
                try {
                    const info = JSON.parse(xhr.responseText);
                    handleFileOpened(info);
                } catch {
                    showToast(t('toast.uploadParseError'));
                }
            } else {
                try {
                    const err = JSON.parse(xhr.responseText);
                    showToast(err.error || t('toast.uploadFailed'));
                } catch {
                    showToast(t('toast.uploadHttpFailed', { status: xhr.status }));
                }
            }
        };

        xhr.onerror = () => {
            hideLoadingOverlay();
            showToast(t('toast.uploadNetworkError'));
        };

        xhr.send(formData);
    }

    /**
     * 通过路径打开文件（用于历史记录点击）。
     * 路径指向后端文件系统上的真实路径（本地或上传后的 temp 路径）。
     *
     * 后端首次访问时需要扫描全文构建行偏移索引，可能耗时数秒，
     * 期间显示 loading overlay 提示用户索引构建进度。
     */
    async function openFile(filePath) {
        const fileName = filePath.split(/[\\/]/).pop() || filePath;
        showLoadingOverlay(t('loading.buildingIndex', { fileName }));

        try {
            const resp = await fetch(`/api/file/info?path=${encodeURIComponent(filePath)}`);
            hideLoadingOverlay();
            if (!resp.ok) { showToast(t('toast.fileNotAccessible')); return; }
            const info = await resp.json();
            handleFileOpened(info);
        } catch (err) {
            hideLoadingOverlay();
            showToast(t('toast.openFailed') + ': ' + err.message);
        }
    }

    /**
     * 文件信息加载后的公共处理：更新顶部文件信息、显示日志容器、配置虚拟滚动。
     */
    function handleFileOpened(info) {
        currentFilePath = info.filePath;
        lastFilePath = info.filePath;

        document.getElementById('file-name').textContent = info.fileName;
        document.getElementById('file-info').textContent =
            `${formatSize(info.fileSize)} | ${info.totalLines.toLocaleString()} 行`;
        document.getElementById('log-placeholder').style.display = 'none';
        document.getElementById('log-scroll').style.display = 'block';

        VirtualScroll.setConfig(
            info.filePath, info.totalLines,
            KeywordPanel.getKeywords(), false
        );

        loadHistory();
        showToast(t('toast.fileOpened', { fileName: info.fileName }));
    }

    /** 从后端 SQLite 加载历史记录并渲染到右侧面板 */
    async function loadHistory() {
        const sort = document.querySelector('.history-tab.active')?.dataset?.sort || 'recent';
        const search = document.getElementById('history-search')?.value?.trim() || '';
        const params = new URLSearchParams({ sort });
        if (search) params.set('search', search);

        const resp = await fetch('/api/history?' + params.toString());
        const records = await resp.json();

        const list = document.getElementById('history-list');
        list.innerHTML = '';

        if (records.length === 0) {
            list.innerHTML = '<div class="history-empty">' + t('history.empty') + '</div>';
            return;
        }

        records.forEach(record => {
            const item = document.createElement('div');
            item.className = 'history-item';

            const name = document.createElement('span');
            name.className = 'history-item-name';
            name.textContent = record.fileName;
            name.title = record.filePath;
            name.onclick = () => openFile(record.filePath);

            const meta = document.createElement('span');
            meta.className = 'history-item-meta';
            meta.textContent = formatSize(record.fileSize);

            const remove = document.createElement('button');
            remove.className = 'history-item-remove';
            remove.textContent = '\u00D7';
            remove.title = '移除';
            remove.onclick = async (e) => {
                e.stopPropagation();
                await fetch(`/api/history?path=${encodeURIComponent(record.filePath)}`, { method: 'DELETE' });
                loadHistory();
            };

            item.appendChild(name);
            item.appendChild(meta);
            item.appendChild(remove);
            list.appendChild(item);
        });
    }

    /**
     * 执行全文搜索，结果传给 VirtualScroll 导航。
     * 全文搜索需要扫描整个日志文件，可能耗时较长，
     * 期间显示 loading overlay 并禁用搜索按钮防止重复提交。
     */
    async function doSearch() {
        if (!currentFilePath) { showToast(t('toast.noFileOpened')); return; }
        const keyword = document.getElementById('search-input').value.trim();
        if (!keyword) return;

        const searchBtn = document.getElementById('btn-search');
        searchBtn.classList.add('btn-searching');
        searchBtn.textContent = t('sidebar.search') + '...';
        showLoadingOverlay(t('loading.searching', { keyword }));

        try {
            const resp = await fetch(
                `/api/file/search?path=${encodeURIComponent(currentFilePath)}&keyword=${encodeURIComponent(keyword)}&caseSensitive=false`
            );
            const data = await resp.json();

            VirtualScroll.setSearchResults(data.lineNumbers);

            const resultEl = document.getElementById('search-result');
            const navEl = document.getElementById('search-nav');

            if (data.total === 0) {
                resultEl.textContent = t('toast.noMatch');
                navEl.style.display = 'none';
            } else {
                resultEl.textContent = t('toast.matchFound', { total: data.total });
                navEl.style.display = 'flex';
                updateSearchIndex();
            }
        } catch (err) {
            showToast(t('toast.searchFailed') + ': ' + err.message);
        } finally {
            searchBtn.classList.remove('btn-searching');
            searchBtn.textContent = t('sidebar.search');
            hideLoadingOverlay();
        }
    }

    function navigateSearch(direction) {
        const info = VirtualScroll.navigateSearch(direction);
        if (info) updateSearchIndex();
    }

    /** 清除搜索高亮：移除所有匹配标记和当前导航状态 */
    function clearSearch() {
        VirtualScroll.clearSearch();
        document.getElementById('search-result').textContent = '';
        document.getElementById('search-nav').style.display = 'none';
        document.getElementById('search-input').value = '';
    }

    function updateSearchIndex() {
        const info = VirtualScroll.getSearchInfo();
        document.getElementById('search-index').textContent = `${info.current}/${info.total}`;
    }

    /** 格式化文件大小（B / KB / MB） */
    function formatSize(bytes) {
        if (bytes < 1024) return bytes + ' B';
        if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
        return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
    }

    // ========== 导入/导出关键字 ==========

    /** 暂存待导入数据，对话框确认后才真正发送到后端 */
    let pendingImportData = null;

    /**
     * 导出全部关键字为 JSON 文件下载。
     * 文件名格式：logviewer-keywords-YYYYMMDD.json
     * 包含全部字段（含 Id、CreatedAt、UpdatedAt），导入时按 CreatedAt 排序。
     */
    async function exportKeywords() {
        try {
            const resp = await fetch('/api/keywords/export');
            if (!resp.ok) throw new Error('Export failed');
            const keywords = await resp.json();

            // 创建 Blob 并触发浏览器下载
            const blob = new Blob([JSON.stringify(keywords, null, 2)], { type: 'application/json' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            const now = new Date();
            const dateStr = now.getFullYear().toString() +
                String(now.getMonth() + 1).padStart(2, '0') +
                String(now.getDate()).padStart(2, '0');
            a.href = url;
            a.download = `logviewer-keywords-${dateStr}.json`;
            a.click();
            URL.revokeObjectURL(url);
            showToast(t('toast.exportSuccess', { count: keywords.length }));
        } catch (err) {
            showToast(t('toast.exportFailed') + ': ' + err.message);
        }
    }

    /**
     * 处理导入文件选择：读取 JSON 并弹出导入对话框。
     * 严格校验：必须为 JSON 数组，每个元素包含全部必要字段。
     * 校验失败不弹窗，直接 Toast 提示。
     */
    function handleImportFile(e) {
        const file = e.target.files[0];
        if (!file) return;
        e.target.value = '';

        const reader = new FileReader();
        reader.onload = () => {
            try {
                const data = JSON.parse(reader.result);
                if (!Array.isArray(data)) {
                    showToast(t('toast.importFormatError'));
                    return;
                }
                const required = ['text', 'color', 'enabled', 'caseSensitive', 'highlightWholeLine', 'wholeLineOpacity'];
                for (let i = 0; i < data.length; i++) {
                    for (const field of required) {
                        if (data[i][field] === undefined) {
                            showToast(t('toast.importFieldMissing', { index: i + 1, field }));
                            return;
                        }
                    }
                }

                pendingImportData = data;
                document.getElementById('import-file-info').textContent =
                    `${file.name}  |  ${data.length} ${t('sidebar.keywords')}`;
                document.getElementById('import-modal').classList.add('active');
            } catch (err) {
                showToast(t('toast.importJsonError') + ' - ' + err.message);
            }
        };
        reader.readAsText(file);
    }

    /** 关闭导入对话框，清空暂存数据 */
    window.closeImportModal = function () {
        pendingImportData = null;
        document.getElementById('import-modal').classList.remove('active');
    };

    /**
     * 确认导入：读取用户选择的模式和冲突策略，发送到后端。
     * replace 模式：导入全部，冲突的覆盖，不冲突的保留。
     * merge 模式：冲突时按用户选择跳过或覆盖。
     * 完成后刷新关键字列表并 Toast 反馈结果。
     */
    window.confirmImport = async function () {
        if (!pendingImportData) return;

        const mode = document.querySelector('input[name="import-mode"]:checked').value;
        const conflictAction = document.querySelector('input[name="import-conflict"]:checked')?.value || 'skip';

        try {
            const resp = await fetch('/api/keywords/import', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    keywords: pendingImportData,
                    mode,
                    conflictAction
                })
            });
            if (!resp.ok) throw new Error('Import failed');
            const result = await resp.json();

            closeImportModal();

            let msg = t('toast.importSuccess', { added: result.added });
            if (result.skipped > 0) msg += t('toast.importSkipped', { count: result.skipped });
            if (result.overwritten > 0) msg += t('toast.importOverwritten', { count: result.overwritten });
            showToast(msg);

            await KeywordPanel.load();
        } catch (err) {
            showToast(t('toast.importFailed') + ': ' + err.message);
        }
    };

    return { init };
})();

// DOM 就绪后启动应用
document.addEventListener('DOMContentLoaded', () => App.init());
