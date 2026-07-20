# 虚拟滚动系统设计文档

## 1. 概述

虚拟滚动是 LogViewer 的核心性能机制，使百万行级日志文件的浏览保持 60 FPS 流畅度。其原理是：**只渲染可见区域 ± 缓冲区的 DOM 节点**，通过绝对定位模拟完整滚动高度，配合后端行偏移索引实现任意位置 O(1) 跳转读取。

---

## 2. 架构总览

```
┌─────────────────────────────────────────────────────┐
│  浏览器                                              │
│                                                      │
│  ┌──────────────────────────────────────────────┐    │
│  │ VirtualScroll (virtual-scroll.js)            │    │
│  │                                              │    │
│  │  scroll 事件                                  │    │
│  │       │                                      │    │
│  │       ▼                                      │    │
│  │  requestAnimationFrame(render)               │    │
│  │       │                                      │    │
│  │       ├─ 缓存命中 → 直接 DOM 渲染             │    │
│  │       │                                      │    │
│  │       └─ 缓存未命中 → ensureLinesLoaded()     │    │
│  │              │                               │    │
│  │              ▼                               │    │
│  │         AbortController.abort(旧请求)         │    │
│  │         fetch /api/file/lines?start&count    │    │
│  │              │                               │    │
│  │              ▼                               │    │
│  │         写入 lineCache → render() 重渲染       │    │
│  └──────────────────────────────────────────────┘    │
│                                                      │
│  ┌──────────────────────────────────────────────┐    │
│  │ Highlight (highlight.js)                     │    │
│  │  每行渲染时调用 highlightLine()               │    │
│  └──────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────┘
                        │
                        │ HTTP GET
                        ▼
┌─────────────────────────────────────────────────────┐
│  ASP.NET Core 8.0                                    │
│                                                      │
│  ┌────────────────────┐  ┌────────────────────────┐  │
│  │ FileEndpoints      │  │ FileService             │  │
│  │                    │  │                         │  │
│  │ GET /api/file/info │→│ GetFileInfo()            │  │
│  │   → 返回总行数     │  │   → 构建行偏移索引      │  │
│  │                    │  │   → 缓存到内存          │  │
│  │ GET /api/file/lines│→│ GetLines(start, count)  │  │
│  │   → 按行号读内容   │  │   → fs.Seek(O(1))      │  │
│  │                    │  │   → 读取 count 行       │  │
│  └────────────────────┘  └────────────────────────┘  │
└─────────────────────────────────────────────────────┘
```

---

## 3. 前端核心模块：`virtual-scroll.js`

### 3.1 常量配置

| 常量 | 值 | 说明 |
|---|---|---|
| `LINE_HEIGHT` | 20px | 每行固定高度，用于计算 scrollTop ↔ 行号 |
| `BUFFER_LINES` | 50 | 可见区域上下各预留的缓冲行数 |
| `BATCH_SIZE` | 200 | 每次向后端请求的行数 |

### 3.2 核心状态

| 变量 | 类型 | 说明 |
|---|---|---|
| `container` | HTMLElement | 滚动容器（`.log-scroll-container`） |
| `spacer` | HTMLElement | 撑开总高度的占位元素 |
| `linesContainer` | HTMLElement | 实际渲染行的容器 |
| `totalLines` | number | 文件总行数 |
| `currentFilePath` | string | 当前打开的文件路径 |
| `lineCache` | `Map<number, string>` | 行号 → 文本内容的缓存 |
| `keywords` | Array | 当前关键字配置 |
| `renderedStart` / `renderedEnd` | number | 上次渲染的行号范围（跳过无变化渲染） |
| `activeController` | AbortController | 用于取消旧的 fetch 请求 |
| `searchMatches` | number[] | 搜索命中的行号列表 |
| `currentSearchIndex` | number | 当前搜索导航位置 |

### 3.3 公开 API

```javascript
// 初始化：绑定滚动容器
init(scrollEl, spacerEl, linesEl)

// 打开新文件：重置所有状态，触发首次渲染
setConfig(filePath, totalLines, keywords)

// 关键字变更：清缓存，重新渲染
updateKeywords(keywords)

// 设置搜索结果，导航到第一个命中行
setSearchResults(lineNumbers)

// 搜索导航（上一个/下一个）
navigateSearch(direction) → { current, total }

// 滚动到指定行号（居中显示）
scrollToLine(lineNum)

// 清空所有状态
clear()
```

---

## 4. 渲染流程

### 4.1 `render()` 函数

```
scroll 事件触发
    │
    ▼
requestAnimationFrame(render)   ← 每帧最多执行一次
    │
    ▼
计算可见范围：
    startLine = floor(scrollTop / LINE_HEIGHT) - BUFFER_LINES
    endLine   = ceil((scrollTop + viewHeight) / LINE_HEIGHT) + BUFFER_LINES
    │
    ▼
跳过判断：
    startLine === renderedStart && endLine === renderedEnd
    && isRangeCached(startLine, endLine)
    → true → return（无需更新）
    │
    ▼
ensureLinesLoaded(startLine, endLine)   ← 触发异步加载
    │
    ▼
创建 DocumentFragment，遍历 [startLine, endLine)：
    ├─ 行号元素 (.log-line-number)
    ├─ 内容元素 (.log-line-content)
    │   ├─ 缓存命中 → Highlight.highlightLine() 渲染高亮
    │   └─ 缓存未命中 → 显示 shimmer 加载动画 (.log-line-loading)
    └─ 双击事件 → 复制到剪贴板
    │
    ▼
清空 linesContainer，appendChild(fragment)  ← 一次性 DOM 更新
```

### 4.2 `ensureLinesLoaded()` 函数

```
检查 [start, end) 范围内缺失的行号
    │
    ▼
无缺失 或 activeController 存在（正在加载）→ return
    │
    ▼
计算批次范围：batchStart = min(缺失行号), batchEnd = min(total, batchStart + 200)
    │
    ▼
abort() 取消旧请求（如有）
    │
    ▼
fetch('/api/file/lines?path=...&start=batchStart&count=batchEnd-batchStart')
    { signal: controller.signal }
    │
    ▼
.then: 写入 lineCache，重置 renderedStart/renderedEnd = -1，调用 render()
    │
    ▼
.catch: AbortError 静默忽略，其他错误输出到 console
```

---

## 5. 后端 API 接口

### 5.1 `GET /api/file/info`

**用途：** 获取文件基本信息，首次访问时触发行偏移索引构建。

**请求参数：**
- `path` (string) - 文件完整路径

**响应：**
```json
{
    "filePath": "C:\\logs\\app.log",
    "fileName": "app.log",
    "fileSize": 1048576,
    "totalLines": 50000
}
```

**后端逻辑：**
1. 检查文件是否存在
2. 调用 `FileService.GetOrBuildIndex()`：
   - 缓存命中且文件未修改 → 直接返回
   - 否则扫描全文，记录每行字节偏移到 `long[] LineOffsets`
3. 返回文件名、大小、总行数

---

### 5.2 `GET /api/file/lines`

**用途：** 按行号范围读取日志内容，是虚拟滚动的核心数据接口。

**请求参数：**
- `path` (string) - 文件完整路径
- `start` (long) - 起始行号（0-indexed）
- `count` (int) - 读取行数（上限 500）

**响应：**
```json
{
    "lines": [
        "2024-01-01 12:00:00 INFO  Application started",
        "2024-01-01 12:00:01 DEBUG Loading config...",
        ...
    ],
    "start": 49900
}
```

**后端逻辑：**
1. `FileService.GetLines(start, count)`
2. `fs.Position = LineOffsets[start]` — O(1) 直接跳转
3. `reader.DiscardBufferedData()` — 清除 StreamReader 内部缓冲
4. 逐行 `ReadLine()` 读取 `count` 行
5. 返回字符串列表

**性能特性：**
- 跳转到文件任意位置：O(1)（直接 Seek）
- 读取 N 行：O(N)
- 不受文件总大小影响

---

## 6. 行偏移索引（后端核心优化）

### 6.1 问题

传统实现每次请求行时都从文件头逐行 `ReadLine()` 跳过 `start` 行，对大文件（百万行）极慢。

### 6.2 方案

```csharp
// FileService.cs - GetOrBuildIndex()

// 首次打开时扫描全文，记录每行起始字节偏移
var offsets = new List<long> { 0 };  // 第 0 行从字节 0 开始
while (!reader.EndOfStream)
{
    reader.ReadLine();
    offsets.Add(fs.Position);  // ReadLine 后 Position 指向下一行起始
}
// offsets[i] = 第 i 行在文件中的字节起始位置
```

**缓存策略：**
- 存储在 `ConcurrentDictionary<string, FileIndex>`
- 按文件最后修改时间校验有效性
- 文件被修改时自动重建

**Seek 读取：**
```csharp
fs.Position = index.LineOffsets[start];  // O(1) 跳转
reader.DiscardBufferedData();            // 清除 StreamReader 缓冲
for (long i = start; i < end; i++)
    lines.Add(reader.ReadLine());
```

---

## 7. 加载状态反馈

### 7.1 行级加载动画

未缓存的行显示 CSS shimmer 动画（40% 宽度的 accent 色亮条从左到右滑过），区别于静态 `...` 占位：

```css
.log-line-loading::after {
    /* 从左到右的 accent 色扫描线 */
    background: linear-gradient(90deg, transparent, var(--accent), transparent);
    animation: shimmer 1s linear infinite;
}
```

### 7.2 全局 Loading Overlay

大文件首次打开时（索引构建），显示全屏 spinner + "正在构建 xxx 索引..."。数据到达后自动隐藏。

---

## 8. 请求取消机制（AbortController）

用户快速拖动滚动条时，可能每帧都触发新的 `ensureLinesLoaded` 调用。如果不取消旧请求，会导致：

1. 多个 fetch 并行，浪费带宽
2. 旧请求的响应可能晚于新请求到达，导致缓存写入错误位置的数据

**解决方案：**
- 每次发起新 fetch 前，`activeController.abort()` 取消旧请求
- 新请求携带自己的 `AbortController.signal`
- fetch 的 `.catch` 中静默忽略 `AbortError`

```
时间线：
  请求 A 开始 ──────────────────────┐
       请求 B 开始（abort A） ──────┐│
            请求 B 完成 ────────────┘│
                                     └─ A 被取消，不处理
```

---

## 9. 完整数据流：从打开文件到看到日志

```
1. 用户打开文件
   → GET /api/file/info?path=xxx
   → 后端构建行偏移索引（首次 O(N)）
   → 返回 { filePath, totalLines, ... }

2. 前端调用 setConfig(filePath, totalLines, keywords)
   → 设置 spacer 高度 = totalLines × 20px
   → scrollTop = 0
   → 调用 render()

3. render() 计算可见范围 [0, 100]
   → 调用 ensureLinesLoaded(0, 100)
   → 发起 GET /api/file/lines?start=0&count=200

4. 后端 Seek 到 LineOffsets[0]，读取 200 行
   → 返回 { lines: [...], start: 0 }

5. 前端写入 lineCache，调用 render()
   → 从缓存读取，调用 Highlight.highlightLine() 渲染
   → DOM 更新，用户看到日志内容

6. 用户滚动
   → scroll 事件 → requestAnimationFrame(render)
   → 计算新可见范围
   → 缓存命中 → 直接渲染
   → 缓存未命中 → ensureLinesLoaded → fetch → render
```