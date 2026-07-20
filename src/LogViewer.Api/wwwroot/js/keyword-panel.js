/**
 * KeywordPanel - 左侧关键字配置面板 UI 模块
 *
 * 负责渲染关键字列表，并提供以下交互：
 * - 启用/禁用关键字
 * - 切换整行高亮（SVG 横线图标，带 active 状态）
 * - 每关键字独立切换大小写敏感性（Aa 按钮）
 * - 修改关键字颜色（color picker）
 * - 修改关键字文本（点击文本进入内联编辑模式）
 * - 删除关键字
 * - 底色浓度滑块（所有关键字均显示，范围 5%-80%）
 *
 * 所有操作统一通过 updateKeyword() 发送 PUT 请求，
 * 成功后更新本地 state，触发重渲染并通知 VirtualScroll 刷新。
 */
const KeywordPanel = (() => {
    let keywords = [];
    let onUpdate = null;
    let editingId = null;

    /**
     * 构建统一的关键字请求体，overrides 中的字段覆盖 kw 对应值。
     * 避免每个操作手动拼装完整对象，防止遗漏字段导致后端覆盖错误。
     */
    function buildKeywordPayload(kw, overrides = {}) {
        return {
            text: overrides.text ?? kw.text,
            color: overrides.color ?? kw.color,
            enabled: overrides.enabled ?? kw.enabled,
            caseSensitive: overrides.caseSensitive ?? kw.caseSensitive,
            highlightWholeLine: overrides.highlightWholeLine ?? kw.highlightWholeLine,
            wholeLineOpacity: overrides.wholeLineOpacity ?? (kw.wholeLineOpacity ?? 30),
            matchMode: overrides.matchMode ?? (kw.matchMode || 'contains')
        };
    }

    /**
     * 通用更新操作：PUT 请求成功后替换本地 state，重渲染并通知 VirtualScroll。
     */
    async function updateKeyword(kw, overrides) {
        const payload = buildKeywordPayload(kw, overrides);
        const resp = await fetch(`/api/keywords/${kw.id}`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        if (resp.ok) {
            const updated = await resp.json();
            const idx = keywords.findIndex(k => k.id === kw.id);
            if (idx >= 0) keywords[idx] = updated;
            editingId = null;
            render();
            notifyUpdate();
        }
    }

    /** 从后端加载关键字列表，加载完成后通知 VirtualScroll 刷新 */
    async function load() {
        const resp = await fetch('/api/keywords');
        keywords = await resp.json();

        render();
        notifyUpdate();
        return { keywords };
    }

    function setUpdateCallback(cb) { onUpdate = cb; }

    /**
     * 进入内联编辑模式：将关键字文本替换为 input，
     * Enter/失焦确认，Escape 取消。
     */
    function startEdit(kw, textEl, item) {
        if (editingId !== null) return;
        editingId = kw.id;

        const input = document.createElement('input');
        input.type = 'text';
        input.value = kw.text;
        input.className = 'keyword-edit-input';

        textEl.replaceWith(input);
        input.focus();
        input.select();

        function confirm() {
            const newText = input.value.trim();
            if (newText && newText !== kw.text) {
                updateKeyword(kw, { text: newText });
            } else {
                editingId = null;
                render();
            }
        }

        input.addEventListener('blur', confirm);
        input.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') { e.preventDefault(); input.blur(); }
            if (e.key === 'Escape') { editingId = null; render(); }
        });

        item.classList.add('editing');
    }

    /**
     * 重新渲染整个关键字列表 DOM。
     * 每个关键字项结构：
     *   颜色圆点 | 文本标签（点击进入编辑） | 整行高亮图标 | [启用/禁用] [Aa] [颜色] [删除]
     *   └─ 底色浓度滑块（始终显示）
     */
    function render() {
        const list = document.getElementById('keyword-list');
        list.innerHTML = '';

        keywords.forEach(kw => {
            const item = document.createElement('div');
            item.className = 'keyword-item';

            const dot = document.createElement('span');
            dot.className = 'keyword-color-dot';
            dot.style.backgroundColor = kw.color;

            const text = document.createElement('span');
            text.className = 'keyword-text' + (kw.enabled ? '' : ' disabled');
            text.textContent = kw.text;
            text.title = t('keyword.editTooltip') + '\n' + kw.text + (kw.highlightWholeLine ? ' (' + t('keyword.wholeLine') + ')' : '');
            text.addEventListener('click', (e) => {
                e.stopPropagation();
                startEdit(kw, text, item);
            });

            const lineIndicator = document.createElement('span');
            lineIndicator.className = 'keyword-line-indicator' + (kw.highlightWholeLine ? ' active' : '');
            lineIndicator.title = t('keyword.highlightWholeLine');
            lineIndicator.innerHTML = '<svg width="14" height="14" viewBox="0 0 14 14"><rect x="1" y="6" width="12" height="2" rx="1" fill="currentColor"/></svg>';
            lineIndicator.style.color = kw.color;
            lineIndicator.onclick = (e) => {
                e.stopPropagation();
                updateKeyword(kw, { highlightWholeLine: !kw.highlightWholeLine });
            };

            const actions = document.createElement('span');
            actions.className = 'keyword-actions';

            const btnToggle = document.createElement('button');
            btnToggle.className = 'btn-action-toggle';
            btnToggle.textContent = kw.enabled ? '✓' : '✗';
            btnToggle.title = kw.enabled ? t('keyword.disable') : t('keyword.enable');
            btnToggle.onclick = () => updateKeyword(kw, { enabled: !kw.enabled });

            const btnCase = document.createElement('button');
            btnCase.className = 'btn-action-case' + (kw.caseSensitive ? ' active' : '');
            btnCase.textContent = 'Aa';
            btnCase.title = kw.caseSensitive ? t('keyword.caseSensitiveOn') : t('keyword.caseSensitiveOff');
            btnCase.onclick = (e) => {
                e.stopPropagation();
                updateKeyword(kw, { caseSensitive: !kw.caseSensitive });
            };

            // 匹配模式三态循环按钮：contains → wholeWord → regex → contains
            const matchModes = ['contains', 'wholeWord', 'regex'];
            const matchLabels = { contains: 'abc', wholeWord: 'W', regex: '.*' };
            const currentMode = kw.matchMode || 'contains';

            const btnMatch = document.createElement('button');
            btnMatch.className = 'btn-action-match' + (currentMode !== 'contains' ? ' active' : '');
            btnMatch.textContent = matchLabels[currentMode];
            btnMatch.title = t('keyword.' + currentMode);
            btnMatch.onclick = (e) => {
                e.stopPropagation();
                const idx = matchModes.indexOf(currentMode);
                const nextMode = matchModes[(idx + 1) % matchModes.length];
                updateKeyword(kw, { matchMode: nextMode });
            };

            const btnColor = document.createElement('input');
            btnColor.type = 'color';
            btnColor.value = kw.color;
            btnColor.className = 'color-picker';
            btnColor.title = t('keyword.changeColor');
            btnColor.onchange = (e) => updateKeyword(kw, { color: e.target.value });

            const btnDelete = document.createElement('button');
            btnDelete.className = 'btn-delete';
            btnDelete.textContent = '×';
            btnDelete.title = t('keyword.delete');
            btnDelete.onclick = () => deleteKeyword(kw.id);

            actions.appendChild(btnToggle);
            actions.appendChild(btnCase);
            actions.appendChild(btnMatch);
            actions.appendChild(btnColor);
            actions.appendChild(btnDelete);

            item.appendChild(dot);
            item.appendChild(text);
            item.appendChild(lineIndicator);
            item.appendChild(actions);

            if (editingId === kw.id) {
                item.classList.add('editing');
            }

            const opacity = kw.wholeLineOpacity ?? 30;
            const opacityRow = document.createElement('div');
            opacityRow.className = 'keyword-opacity-row';

            const opacityLabel = document.createElement('span');
            opacityLabel.className = 'keyword-opacity-label';
            opacityLabel.textContent = t('keyword.opacityLabel');

            const opacitySlider = document.createElement('input');
            opacitySlider.type = 'range';
            opacitySlider.min = '5';
            opacitySlider.max = '80';
            opacitySlider.value = String(opacity);
            opacitySlider.className = 'keyword-opacity-slider';
            opacitySlider.title = opacity + '%';

            const opacityValue = document.createElement('span');
            opacityValue.className = 'keyword-opacity-value';
            opacityValue.textContent = opacity + '%';

            opacitySlider.oninput = (e) => {
                const val = parseInt(e.target.value);
                opacityValue.textContent = val + '%';
                opacitySlider.title = val + '%';
            };
            opacitySlider.onchange = (e) => {
                const val = parseInt(e.target.value);
                updateKeyword(kw, { wholeLineOpacity: val });
            };

            opacityRow.appendChild(opacityLabel);
            opacityRow.appendChild(opacitySlider);
            opacityRow.appendChild(opacityValue);
            item.appendChild(opacityRow);

            list.appendChild(item);
        });
    }

    /** 新建关键字，默认不区分大小写 */
    async function add(text, color, highlightWholeLine = false) {
        if (!text.trim()) return;
        const resp = await fetch('/api/keywords', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                text: text.trim(), color,
                caseSensitive: false,
                highlightWholeLine,
                wholeLineOpacity: 30
            })
        });
        if (resp.ok) {
            const kw = await resp.json();
            keywords.push(kw);
            render();
            notifyUpdate();
        }
    }

    async function deleteKeyword(id) {
        const resp = await fetch(`/api/keywords/${id}`, { method: 'DELETE' });
        if (resp.ok) {
            keywords = keywords.filter(k => k.id !== id);
            render();
            notifyUpdate();
        }
    }

    function notifyUpdate() {
        // 关键字配置变更时清除高亮引擎的 RegExp 缓存
        Highlight.clearRegexCache();
        if (onUpdate) onUpdate(keywords);
    }

    return {
        load, add, setUpdateCallback,
        getKeywords: () => keywords
    };
})();
