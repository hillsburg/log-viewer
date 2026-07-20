/**
 * VirtualScroll - 虚拟滚动模块
 *
 * 核心设计：
 * - 只渲染可见区域 ± BUFFER_LINES 缓冲区的 DOM，支持百万级行日志流畅浏览
 * - 行数据缓存到 Map 中，避免重复请求后端
 * - 后端通过行偏移索引实现 O(1) 跳转读取，任意位置拖动秒出
 * - 使用 AbortController 取消旧的 fetch 请求，快速拖动时不阻塞新请求
 */
const VirtualScroll = (() => {
    const LINE_HEIGHT = 20;       // 每行固定像素高度
    const BUFFER_LINES = 50;      // 可见区域上下各预留的缓冲行数
    const BATCH_SIZE = 200;       // 每次向后端请求的行数

    let container = null;         // 滚动容器 (.log-scroll-container)
    let spacer = null;            // 撑开滚动高度的占位元素 (.log-scroll-spacer)
    let linesContainer = null;    // 实际渲染行的容器 (.log-lines)
    let totalLines = 0;           // 文件总行数
    let currentFilePath = null;   // 当前打开的文件路径
    let lineCache = new Map();    // 行号 -> 文本内容的缓存
    let keywords = [];            // 当前关键字列表
    let renderedStart = -1;       // 上次渲染的起始行号（用于跳过无变化渲染）
    let renderedEnd = -1;         // 上次渲染的结束行号
    let searchMatches = [];       // 搜索命中的行号数组
    let currentSearchIndex = -1;  // 当前搜索导航到的索引
    let activeController = null;  // AbortController，用于取消正在进行的 fetch

    /**
     * 初始化：绑定滚动容器和滚动事件
     */
    function init(scrollEl, spacerEl, linesEl) {
        container = scrollEl;
        spacer = spacerEl;
        linesContainer = linesEl;

        container.addEventListener('scroll', onScroll);
    }

    /**
     * 打开新文件时调用：重置所有状态，设置 spacer 高度，触发首次渲染
     */
    function setConfig(filePath, lines, kw) {
        if (activeController) activeController.abort();
        activeController = null;

        currentFilePath = filePath;
        totalLines = lines;
        keywords = kw;
        lineCache.clear();
        renderedStart = -1;
        renderedEnd = -1;
        searchMatches = [];
        currentSearchIndex = -1;

        spacer.style.height = (totalLines * LINE_HEIGHT) + 'px';
        container.scrollTop = 0;
        render();
    }

    /**
     * 关键字变更时调用：清空缓存和渲染标记，立即重新渲染
     */
    function updateKeywords(kw) {
        keywords = kw;
        renderedStart = -1;
        renderedEnd = -1;
        render();
    }

    /**
     * 滚动事件处理：使用 requestAnimationFrame 保证每帧最多一次渲染
     */
    function onScroll() {
        requestAnimationFrame(render);
    }

    /**
     * 检查指定范围 [start, end) 的行是否全部已缓存
     * 用于 render() 中判断是否需要跳过渲染（范围未变且数据已就绪）
     */
    function isRangeCached(start, end) {
        for (let i = start; i < end; i++) {
            if (!lineCache.has(i)) return false;
        }
        return true;
    }

    /**
     * 核心渲染函数
     *
     * 跳过渲染条件：可见行号范围未变化，且范围内数据全部已缓存。
     * 未缓存的行显示加载占位动画（.log-line-loading），数据到达后自动重渲染。
     */
    function render() {
        const scrollTop = container.scrollTop;
        const viewHeight = container.clientHeight;
        const startLine = Math.max(0, Math.floor(scrollTop / LINE_HEIGHT) - BUFFER_LINES);
        const endLine = Math.min(totalLines, Math.ceil((scrollTop + viewHeight) / LINE_HEIGHT) + BUFFER_LINES);

        if (startLine === renderedStart && endLine === renderedEnd && isRangeCached(startLine, endLine)) return;

        renderedStart = startLine;
        renderedEnd = endLine;

        ensureLinesLoaded(startLine, endLine);

        const fragment = document.createDocumentFragment();
        for (let i = startLine; i < endLine; i++) {
            const div = document.createElement('div');
            div.className = 'log-line';
            div.style.position = 'absolute';
            div.style.top = (i * LINE_HEIGHT) + 'px';
            div.style.left = '0';
            div.style.right = '0';

            if (searchMatches.includes(i)) {
                div.classList.add('highlighted');
            }
            if (currentSearchIndex >= 0 && searchMatches[currentSearchIndex] === i) {
                div.classList.add('search-current');
            }

            const lineNum = document.createElement('span');
            lineNum.className = 'log-line-number';
            lineNum.textContent = (i + 1).toString();

            const content = document.createElement('span');
            content.className = 'log-line-content';
            const cached = lineCache.get(i);

            if (cached !== undefined) {
                content.innerHTML = Highlight.highlightLine(cached, keywords);
            } else {
                content.className += ' log-line-loading';
                content.textContent = '';
            }

            div.appendChild(lineNum);
            div.appendChild(content);

            div.addEventListener('dblclick', () => {
                const text = cached || '';
                navigator.clipboard.writeText(text).then(() => showToast('已复制到剪贴板'));
            });

            fragment.appendChild(div);
        }

        linesContainer.innerHTML = '';
        linesContainer.appendChild(fragment);
    }

    /**
     * 确保指定范围的行数据已加载
     *
     * 关键优化：使用 AbortController 取消旧的 fetch。
     * 用户快速拖动滚动条时，旧位置的请求被立即中止，
     * 新位置的请求立刻发出，不会被 isLoading 标志阻塞。
     */
    function ensureLinesLoaded(start, end) {
        const missing = [];
        for (let i = start; i < end; i++) {
            if (!lineCache.has(i)) missing.push(i);
        }

        if (missing.length === 0) return;

        if (!currentFilePath) return;

        // 取消上一次未完成的请求，避免旧数据阻塞新位置的加载
        if (activeController) activeController.abort();

        const batchStart = Math.max(0, Math.min(...missing));
        const batchEnd = Math.min(totalLines, batchStart + BATCH_SIZE);

        const controller = new AbortController();
        activeController = controller;

        fetch(`/api/file/lines?path=${encodeURIComponent(currentFilePath)}&start=${batchStart}&count=${batchEnd - batchStart}`, {
            signal: controller.signal
        })
            .then(resp => resp.json())
            .then(data => {
                // 竞态守卫：如果已有更新的 fetch 取代了当前 controller，
                // 丢弃过期响应，防止旧数据污染缓存导致行内容错位
                if (activeController !== controller) return;

                data.lines.forEach((line, idx) => {
                    lineCache.set(batchStart + idx, line);
                });
                // 重置渲染范围标记，强制下次 render() 重新执行
                renderedStart = -1;
                renderedEnd = -1;
                render();
            })
            .catch(err => {
                if (err.name !== 'AbortError') {
                    console.error('Failed to load lines:', err);
                }
            });
    }

    /**
     * 搜索结果设置：存储命中行号，导航到第一个命中行
     */
    function setSearchResults(lineNumbers) {
        searchMatches = lineNumbers;
        currentSearchIndex = lineNumbers.length > 0 ? 0 : -1;
        renderedStart = -1;
        renderedEnd = -1;
        render();

        if (currentSearchIndex >= 0) {
            scrollToLine(searchMatches[0]);
        }
    }

    /**
     * 搜索结果导航：前一个/后一个命中行
     * @returns {{ current: number, total: number }} 当前位置和总数
     */
    function navigateSearch(direction) {
        if (searchMatches.length === 0) return;

        if (direction === 'next') {
            currentSearchIndex = (currentSearchIndex + 1) % searchMatches.length;
        } else {
            currentSearchIndex = (currentSearchIndex - 1 + searchMatches.length) % searchMatches.length;
        }

        renderedStart = -1;
        renderedEnd = -1;
        render();
        scrollToLine(searchMatches[currentSearchIndex]);

        return { current: currentSearchIndex + 1, total: searchMatches.length };
    }

    /**
     * 滚动到指定行号，使该行居中显示
     */
    function scrollToLine(lineNum) {
        const targetScroll = lineNum * LINE_HEIGHT - container.clientHeight / 2;
        container.scrollTop = Math.max(0, targetScroll);
    }

    /**
     * 清空搜索高亮（不清空文件状态）
     */
    function clearSearch() {
        searchMatches = [];
        currentSearchIndex = -1;
        renderedStart = -1;
        renderedEnd = -1;
        render();
    }

    /**
     * 清空所有状态，关闭文件时调用
     */
    function clear() {
        if (activeController) activeController.abort();
        activeController = null;

        currentFilePath = null;
        totalLines = 0;
        lineCache.clear();
        renderedStart = -1;
        renderedEnd = -1;
        searchMatches = [];
        currentSearchIndex = -1;
        linesContainer.innerHTML = '';
        spacer.style.height = '0';
    }

    return {
        init,
        setConfig,
        updateKeywords,
        setSearchResults,
        navigateSearch,
        clearSearch,
        scrollToLine,
        clear,
        getSearchInfo: () => ({ current: currentSearchIndex + 1, total: searchMatches.length })
    };
})();
