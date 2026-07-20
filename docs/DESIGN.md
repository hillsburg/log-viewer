# LogViewer 设计与实现文档

## 1. 项目概述

LogViewer 是一款基于 Web 的大文件日志查看工具，面向需要高频浏览和分析 `.log` / `.txt` 日志文件的开发者。

**核心能力：**

- 支持任意大小日志文件流畅浏览（百万行级别无卡顿）
- 可配置的关键字高亮（内联标记 + 整行底色）
- 全文搜索与结果导航
- 文件历史记录持久化
- 拖拽 / 上传打开文件（带上传进度条）
- 深色 / 浅色主题切换
- 双击任意行复制内容到剪贴板
- 全局 Loading Overlay：上传进度、索引构建、全文搜索均有明确反馈

**设计原则：**

- **零依赖前端**：纯 HTML + CSS + Vanilla JS，无框架、无构建工具、即开即用
- **启动即浏览**：双击 exe 自动打开浏览器，无需命令行操作
- **性能优先**：后端 O(1) 行跳转 + 前端虚拟滚动，两个层面共同保障大文件体验
- **"Terminal Professional" 视觉语言**：Tech Innovation 风格（Electric Blue + Neon Cyan 强调色），Outfit / JetBrains Mono 字体，避免"AI 审美"通用字体和扁平灰配色

---

## 2. 技术架构

```
┌─────────────────────────────────────────────────────┐
│  浏览器                                              │
│                                                      │
│  ┌─────────┐  ┌──────────────┐  ┌────────────────┐  │
│  │ App 主控 │  │ KeywordPanel │  │ VirtualScroll  │  │
│  │ (app.js) │←→│  (侧边栏 UI) │  │  (虚拟滚动)    │  │
│  │          │  └──────────────┘  └───────┬────────┘  │
│  │          │                             │          │
│  │          │          ┌──────────────────┘          │
│  │          │          ▼                             │
│  │          │  ┌──────────────┐                      │
│  │          │  │  Highlight   │                      │
│  │          │  │  (高亮引擎)  │                      │
│  │          │  └──────────────┘                      │
│  └──────────┘                                        │
│  │  HTTP (Fetch API / XHR 上传进度)                    │
│  │  🌐 Google Fonts (Outfit + JetBrains Mono)         │
│  └───────────────────────────────────────────────────│
└──────────────────────────────────────────────────────┘
```

**协议：** 前后端通过 REST API (`/api/*`) 通信。前端主要使用 Fetch API；文件上传因需要进度反馈使用 XMLHttpRequest。

---

## 3. 目录结构

```
LogViewer/
├── LogViewer.sln                        # 解决方案文件
├── src/
│   ├── LogViewer.Launcher/              # 系统托盘启动器 (WPF)
│   │   ├── LogViewer.Launcher.csproj    # 项目配置
│   │   ├── App.xaml                     # 应用定义
│   │   └── App.xaml.cs                  # 托盘图标、进程管理、自启动
│   ├── LogViewer.Api/                   # 后端 (.NET 8.0 ASP.NET Core)
│   │   ├── Program.cs                   # 应用入口、DI、中间件
│   │   ├── LogViewer.Api.csproj         # 项目配置
│   │   ├── appsettings.json             # Kestrel 日志配置
│   │   ├── Models/                      # 数据模型
│   │   │   ├── Keyword.cs               # 关键字配置项
│   │   │   ├── KeywordConfig.cs         # 关键字根配置（列表 + 全局设置）
│   │   │   └── FileRecord.cs            # 历史记录条目
│   │   ├── Endpoints/                   # Minimal API 路由
│   │   │   ├── KeywordEndpoints.cs      # 关键字 CRUD + 大小写 + 主题
│   │   │   ├── FileEndpoints.cs         # 文件信息/读行/搜索/上传
│   │   │   └── HistoryEndpoints.cs      # 历史记录 CRUD
│   │   ├── Services/                    # 业务逻辑层
│   │   │   ├── FileService.cs           # 行偏移索引 + Seek 读取
│   │   │   ├── KeywordService.cs        # 关键字读写（JSON）
│   │   │   └── HistoryService.cs        # 历史记录 + 上传文件清理
│   │   ├── wwwroot/                     # 前端静态文件（原 LogViewer.Web）
│   │   │   ├── index.html               # 单页面入口
│   │   │   ├── css/
│   │   │   │   └── style.css            # "Terminal Professional" 设计系统
│   │   │   └── js/
│   │   │       ├── app.js               # 应用主控（Toast/Loading/事件绑定）
│   │   │       ├── virtual-scroll.js    # 虚拟滚动 + AbortController
│   │   │       ├── highlight.js         # 高亮引擎（内联 + 整行）
│   │   │       └── keyword-panel.js     # 关键字列表 UI
│   │   └── Data/                        # 持久化数据
│   │       ├── logviewer.db             # 历史记录（SQLite）
│   │       ├── keywords.json            # 关键字配置
│   │       └── uploads/                 # 上传文件目录（.gitignore）
└── docs/
    └── DESIGN.md                        # 本文档
```

---

## 4. 数据模型

### 4.1 Keyword（关键字）

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | int | 唯一标识，从 1 递增 |
| `Text` | string | 匹配文本（支持点击内联编辑） |
| `Color` | string | 颜色 hex（如 `#ff4444`） |
| `Enabled` | bool | 启用状态 |
| `CaseSensitive` | bool | **逐关键字独立**区分大小写，默认 `false` |
| `HighlightWholeLine` | bool | 是否整行高亮 |
| `WholeLineOpacity` | int | 整行背景透明度（5-80，百分比） |

### 4.2 关键字配置（SQLite）

所有关键字持久化到 `Data/logviewer.db` 的 `keywords` 表：

```sql
CREATE TABLE IF NOT EXISTS keywords (
    Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    Text               TEXT    NOT NULL,
    Color              TEXT    NOT NULL DEFAULT '#ff4444',
    Enabled            INTEGER NOT NULL DEFAULT 1,
    CaseSensitive      INTEGER NOT NULL DEFAULT 0,
    HighlightWholeLine INTEGER NOT NULL DEFAULT 0,
    WholeLineOpacity   INTEGER NOT NULL DEFAULT 30,
    CreatedAt          TEXT    NOT NULL,
    UpdatedAt          TEXT    NOT NULL
);
```

全局配置（主题、大小写开关）存储在 `settings` 表：

```sql
CREATE TABLE IF NOT EXISTS settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);
```

### 4.3 FileRecord（历史记录）

持久化到 `Data/logviewer.db`（SQLite，WAL 模式）：

```sql
CREATE TABLE file_history (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    FilePath        TEXT    NOT NULL UNIQUE,  -- 同一路径 UPSERT
    FileName        TEXT    NOT NULL,
    FileSize        INTEGER NOT NULL DEFAULT 0,
    TotalLines      INTEGER NOT NULL DEFAULT 0,
    OpenCount       INTEGER NOT NULL DEFAULT 1,
    LastOpened      TEXT    NOT NULL,         -- ISO 8601
    FirstOpened     TEXT    NOT NULL,
    Source          TEXT    NOT NULL DEFAULT 'local',  -- 'local' | 'upload'
    LastScrollLine  INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_history_last_opened ON file_history(LastOpened DESC);
CREATE INDEX idx_history_open_count  ON file_history(OpenCount DESC);
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | long | 自增主键 |
| `FilePath` | string | 文件完整路径（UNIQUE，同一路径 UPSERT 更新而非新增） |
| `FileName` | string | 显示用文件名 |
| `FileSize` | long | 文件字节数（打开时记录） |
| `TotalLines` | long | 文件总行数（打开时记录） |
| `OpenCount` | int | 累计打开次数（每次打开同文件时 +1） |
| `LastOpened` | DateTime | 最后打开时间 |
| `FirstOpened` | DateTime | 首次打开时间（新建时写入，更新时不变） |
| `Source` | string | `local`（本地路径）或 `upload`（上传文件） |
| `LastScrollLine` | long | 上次浏览滚动位置（行号），支持"继续上次浏览" |

---

## 5. API 设计

所有路由前缀 `/api`，请求/响应均为 JSON。

### 5.1 关键字管理

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/keywords` | 获取所有关键字 |
| `POST` | `/api/keywords` | 新建关键字 |
| `PUT` | `/api/keywords/{id}` | 更新关键字全部字段 |
| `DELETE` | `/api/keywords/{id}` | 删除关键字 |
| `GET` | `/api/keywords/export` | 导出全部关键字为 JSON 数组 |
| `POST` | `/api/keywords/import` | 导入关键字（replace / merge 模式） |
| `GET` | `/api/keywords/theme` | 获取当前主题 |
| `PUT` | `/api/keywords/theme` | 设置主题 |

**导入 `/api/keywords/import` 请求体：**

```json
{
  "keywords": [ { "Text": "ERROR", "Color": "#ff4444", ... } ],
  "mode": "replace",
  "conflictAction": "skip"
}
```

| 模式 | 行为 |
|---|---|
| `replace` | 导入全部，与现有 Text 冲突的条目覆盖（先删后插），不冲突的保留 |
| `merge` + `skip` | 保留现有，仅新增不冲突的 |
| `merge` + `overwrite` | 与 replace 相同，冲突时覆盖 |

**响应：** `{ "added": 3, "skipped": 1, "overwritten": 2 }`

### 5.2 文件操作

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/file/info?path=xxx` | 获取文件信息（首次触发索引构建，耗时与文件大小相关） |
| `GET` | `/api/file/lines?path=xxx&start=0&count=200` | 按行号读取内容（count 上限 500） |
| `GET` | `/api/file/search?path=xxx&keyword=yyy&caseSensitive=true` | 全文搜索 |
| `POST` | `/api/file/upload` | 上传文件（multipart，最大 500MB，同名自动递增序号） |

### 5.3 历史记录

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/history?sort=recent\|frequent&search=xxx` | 获取历史记录（支持排序和文件名模糊搜索） |
| `DELETE` | `/api/history?path=xxx` | 删除单条记录（联动删除 uploads 下对应文件） |
| `DELETE` | `/api/history/clear` | 清空全部记录 |

**`sort` 参数：**
- `recent` — 按最后打开时间降序（默认）
- `frequent` — 按累计打开次数降序

**`search` 参数：** 对 `FileName` 字段做 `LIKE '%value%'` 模糊匹配。省略时返回全部记录。

**响应示例：**

```json
[
  {
    "id": 1,
    "filePath": "C:\\logs\\app.log",
    "fileName": "app.log",
    "fileSize": 1048576,
    "totalLines": 12500,
    "openCount": 5,
    "lastOpened": "2026-06-01T10:30:00+08:00",
    "firstOpened": "2026-05-28T14:00:00+08:00",
    "source": "local",
    "lastScrollLine": 1200
  }
]
```

---

## 6. 核心实现详解

### 6.1 大文件 O(1) 行跳转读取

**问题：** 传统 `GetLines(start, count)` 每次从文件头逐行 `ReadLine()` 跳过 `start` 行，大文件拖到末尾时响应极慢。

**方案：行偏移索引**

```
首次打开文件
    │
    ▼
扫描全文，记录每行起始字节偏移
    │
    LineOffsets[0] = 0        ← 文件头
    LineOffsets[1] = 156      ← 第 2 行起始位置
    LineOffsets[2] = 312      ← 第 3 行起始位置
    ...
    LineOffsets[N] = EOF
    │
    ▼
缓存到内存 ConcurrentDictionary<路径, 索引>
    │
    ▼
后续请求 GetLines(500000, 200)
    │
    fs.Seek(LineOffsets[500000])   ← O(1) 跳转
    读取 200 行                     ← 只读目标数据
```

- 索引按文件最后修改时间校验缓存有效性，文件被修改时自动重建
- `DiscardBufferedData()` 清除 StreamReader 旧缓冲区，避免 Seek 后读到旧数据

### 6.2 虚拟滚动

**核心思路：** 只渲染可见区域 ± 50 行缓冲区的 DOM 节点，用 `position: absolute` 绝对定位到正确位置。

```
┌─ log-scroll-container ─────────────────┐
│  ↑                                     │
│  │ spacer (height = totalLines × 20px) │
│  │                                     │
│  │  ┌─ log-lines ────────────────────┐ │
│  │  │  div (top: 4950×20px)          │ │ ← 缓冲起始
│  │  │  div (top: 4951×20px)          │ │
│  │  │  ...                           │ │
│  │  │  div (top: 5000×20px)  ← 可见  │ │ ← 首屏行
│  │  │  ...                           │ │
│  │  │  div (top: 5049×20px)  ← 可见  │ │ ← 末屏行
│  │  │  ...                           │ │
│  │  │  div (top: 5149×20px)          │ │ ← 缓冲结束
│  │  └────────────────────────────────┘ │
│  │                                     │
│  ↓                                     │
└────────────────────────────────────────┘
```

**关键参数：**

| 常量 | 值 | 作用 |
|---|---|---|
| `LINE_HEIGHT` | 20px | 每行固定高度 |
| `BUFFER_LINES` | 50 | 可见区域上下各预留行数 |
| `BATCH_SIZE` | 200 | 每次向后端请求的行数 |

**滚动请求取消（AbortController）：**

用户快速拖动滚动条时，旧位置的请求会被 `AbortController.abort()` 取消，新位置请求立即发出，避免串行等待：

```
拖到位置 A → fetch A 开始
拖到位置 B → fetch A 取消，fetch B 开始
位置 B 数据返回 → 立即渲染
```

**缓存策略：**

- `lineCache: Map<行号, 文本>` 缓存已加载的行内容
- 跳过渲染条件：`startLine === renderedStart && endLine === renderedEnd && isRangeCached(startLine, endLine)`
- 数据到达后重置 `renderedStart/renderedEnd = -1` 强制重渲染

**加载状态反馈：**

未缓存行显示 CSS shimmer 脉冲动画（渐变扫光），而非静态 `...`。

### 6.3 关键字高亮引擎

**两种高亮模式：**

| 模式 | 触发条件 | 效果 |
|---|---|---|
| 内联标记 | 普通关键字 (`highlightWholeLine = false`) | `<mark>` 包裹匹配文本，文字背景着色 |
| 整行底色 | 整行关键字 (`highlightWholeLine = true`) | 整行 `<span>` 背景着色，内联标记在其之上渲染 |

**优先级规则：**

```
一行日志
    │
    ▼
是否有整行关键字命中？
    ├─ 是 → 取最后一个命中的整行关键字颜色和浓度 → 渲染整行底色
    │        （普通关键字仍在此底色上做内联标记）
    │
    └─ 否 → 仅普通关键字做内联标记
```

**大小写敏感性：**

每个关键字独立使用自身的 `caseSensitive` 属性控制，新建关键字默认不区分大小写。用户通过每行关键字旁的 `Aa` 按钮独立切换，无全局开关干扰。

**关键字文本编辑：**

点击关键字文本即进入内联编辑模式，文本标签替换为输入框。`Enter` / 失焦确认保存，`Escape` 取消。编辑期间自动隐藏操作按钮和浓度滑块，避免视觉干扰。

**透明度控制：**

```
用户设定浓度 30%
    │
    ▼
opacity = 30 / 100 = 0.3
    │
    ▼
hexAlpha = round(0.3 * 255) = 77 → '4d'
    │
    ▼
最终背景色 = #ff4444 + '4d' = #ff44444d
```

### 6.4 文件上传与打开

**问题：** 浏览器 `<input type="file">` 存在 `fakepath` 安全限制，且大文件上传和首次打开（索引构建）均耗时较长，需要明确的操作反馈。

**上传流程（XHR + 进度条）：**

```
浏览器读取文件内容 (File API)
    │
    ▼
XHR POST /api/file/upload (multipart/form-data)
    │  xhr.upload.onprogress 实时上报字节进度
    ▼
后端保存到 Data/uploads/app.log
    同名文件自动递增：app_1.log → app_2.log
    │
    ▼
返回文件信息，前端自动打开
```

**打开流程（索引构建 Overlay）：**

从历史记录点击打开文件时，后端首次访问需要全文件扫描构建行偏移索引，可能耗时数秒。期间显示"正在构建 xxx 索引..."Overlay。

**上传文件生命周期管理：**

| 时机 | 行为 |
|---|---|
| 上传 | 保存到 `Data/uploads/`，自动写入 SQLite 历史记录（Source = upload） |
| 打开文件 | SQLite UPSERT：更新 FileSize/TotalLines/LastOpened，OpenCount +1 |
| 用户删除某条历史 | 删除 SQLite 记录 + 对应上传文件（仅 uploads 目录） |
| 清空历史 | 删除所有 SQLite 记录 + 所有上传文件 |
| 应用启动 | 扫描 `Data/uploads/`，对比 SQLite 中已存路径，删除孤立文件 |

**历史面板 UI：**

历史记录从左侧侧边栏移至右侧独立面板，通过工具栏「历史记录」按钮切换显隐（默认收起 0px，展开 280px）。面板包含：
- **搜索过滤**（实时文件名模糊匹配，防抖 300ms）
- **Tab 排序**（最近打开 / 最常用）
- **文件条目**（文件名 + 文件大小 + 删除按钮），点击打开文件
- **空状态提示**（暂无历史记录）

### 6.5 全局 Loading 反馈体系

针对大文件操作耗时长的问题，设计统一的 Loading Overlay 机制，覆盖三个场景：

| 场景 | 触发时机 | 反馈内容 | 结束时机 |
|---|---|---|---|
| **文件上传** | 选择/拖入文件 | spinner + 进度条 + 已上传/总字节数 | XHR 完成 |
| **索引构建** | 打开文件（首次或缓存失效） | spinner + "正在构建 xxx 索引..." | `/api/file/info` 返回 |
| **全文搜索** | 点击搜索按钮 | spinner + "正在搜索 \"xxx\"..." + 按钮禁用态 | `/api/file/search` 返回 |

**全局 API（`app.js` 顶层函数）：**

```javascript
showLoadingOverlay(text, showProgress = false)   // 显示 overlay
updateLoadingProgress(pct)                        // 更新进度 0-100
updateLoadingText(text)                           // 动态更新文字
hideLoadingOverlay()                              // 隐藏 overlay
```

**行级加载反馈：**

虚拟滚动中未加载到缓存的行显示 shimmer 脉冲动画（40% 宽度的 accent 色亮条反复滑过），明确传达"正在加载"，区别于静态占位符。

**搜索按钮禁用态：**

搜索期间 `.btn-searching` 类让按钮淡出 + `pointer-events: none`，`finally` 块保证无论成功失败均恢复状态。

### 6.6 设计系统："Terminal Professional"

**设计方向：** Tech Innovation 风格 — 面向开发者的专业日志工具，视觉语言参考 Linear / Raycast / GitHub Dark。

**设计原则（来源于 `frontend-design` skill 的反模式总结）：**
- **不用通用字体**：避免 Consolas/Arial/Inter/Roboto 等系统默认字体
- **不用千篇一律的配色**：避免紫色到白色的渐变、扁平灰等"AI 审美"
- **追求辨识度**：大胆的 Electric Blue + Neon Cyan 强调色，发光边框、渐变装饰线

#### 6.6.1 字体策略

| 用途 | 字体 | 降级方案 |
|---|---|---|
| UI 排版（标题、按钮、标签） | **Outfit** (Google Fonts) | -apple-system, Segoe UI, sans-serif |
| 日志/代码显示 | **JetBrains Mono** (Google Fonts) | Cascadia Code, Fira Code, Consolas, monospace |

Outfit 是现代几何风格无衬线字体，替代系统默认字体赋予应用辨识感。JetBrains Mono 是专为代码阅读设计的等宽字体，含连字支持，字符区分度极高。

#### 6.6.2 色彩体系

**深色主题（默认）：**

```
背景层叠：
  --bg-primary:   #0d1117    深空黑（主内容区）
  --bg-secondary: #161b22    面板表面（侧边栏）
  --bg-tertiary:  #21262d    工具栏表面
  --bg-hover:     #30363d    悬停态
  --bg-active:    rgba(56, 139, 253, 0.15)  激活态

强调色：
  --accent:       #0066ff    Electric Blue（主操作按钮、链接、指示条）
  --accent-hover: #3385ff    悬停态
  --accent-glow:  rgba(0, 102, 255, 0.25)  发光阴影

语义色：
  --danger:  #f85149    错误/删除
  --success: #3fb950    成功
  --warning: #d29922    警告
```

**浅色主题：** 白底 + Electric Blue 强调，保持相同的色彩语义。

#### 6.6.3 间距与圆角

- **4px 基础网格**：所有间距为 4 的倍数（4/8/12/16/24/32）
- **圆角分级**：`--radius-sm: 4px` / `--radius-md: 6px` / `--radius-lg: 8px`
- **过渡时间**：`120ms`(快速) / `200ms`(标准) / `300ms`(缓动)

#### 6.6.4 视觉层次

| 元素 | 视觉处理 |
|---|---|
| 工具栏 | `bg-tertiary` + 底部渐变 accent 分割线 |
| 侧边栏 | `bg-secondary` + 右侧纵向渐变光条 |
| 主按钮 | 实色 accent 填充 + 发光边框 (`box-shadow: 0 0 12px accent-glow`) |
| 列表项 hover | 背景变化 + 左侧 2px accent 指示条 |
| 输入框 focus | accent 边框 + 3px accent-glow 外发光 |
| 拖拽覆盖层 | accent 虚线边框 + 毛玻璃背景 + 居中卡片发光阴影 |

#### 6.6.5 主题切换

CSS 变量 + `data-theme` 属性实现，JS 通过 `document.documentElement.setAttribute('data-theme', theme)` 切换，持久化到 `keywords.json`。

---

## 7. 前端模块交互

```
┌─ 左侧侧边栏 ─┐                ┌─ 右侧面板 ───────┐
│ KeywordPanel  │                │ History Panel    │
│ (关键字配置)   │ ┌────────────┐ │ (可收起)         │
│              │ │  App 主控   │ │ ┌─────────────┐ │
├──────────────┤ │  (app.js)  │ │ │ 搜索过滤     │ │
│ Search       │ │            │ │ │ Tab 排序     │ │
│ (全文搜索)    │ │ + Loading  │ │ │ 最近打开     │ │
└──────────────┘ │  Overlay   │ │ │ 最常用       │ │
                 └─────┬──────┘ │ │ 文件列表     │ │
                       │        │ └─────────────┘ │
              ┌────────┼────────┐└────────────────┘
              ▼        ▼        ▼
    ┌──────────┐ ┌──────────┐ ┌──────────┐
    │ Virtual  │ │ Highlight│ │ History  │
    │ Scroll   │ │ (高亮)    │ │ API      │
    └──────────┘ └──────────┘ └──────────┘
```

**模块加载顺序**（`index.html` 中 `<script>` 标签顺序，后加载的依赖先加载的）：

```
highlight.js → virtual-scroll.js → keyword-panel.js → app.js
```

**全局函数（`app.js` 顶层作用域）：**

| 函数 | 说明 |
|---|---|
| `showToast(msg, duration)` | 底部 Toast 通知 |
| `showLoadingOverlay(text, showProgress)` | 显示 Loading 遮罩 |
| `updateLoadingProgress(pct)` | 更新进度条（0-100） |
| `updateLoadingText(text)` | 动态更新 Loading 文字 |
| `hideLoadingOverlay()` | 隐藏 Loading 遮罩 |

---

## 8. 后端服务设计

### 8.1 依赖注入

三个 Service 均注册为 `AddSingleton`，进程内共享：

```csharp
builder.Services.AddSingleton<KeywordService>();
builder.Services.AddSingleton<FileService>();
builder.Services.AddSingleton<HistoryService>();
```

### 8.2 线程安全

`KeywordService` 写操作通过 `SemaphoreSlim(1, 1)` 保护；`FileService` 无共享可变状态（每次请求创建独立的 `FileStream` + `StreamReader`），天然线程安全；`HistoryService` 使用 SQLite 自带 WAL 模式，每次操作创建独立连接，无需额外加锁。

### 8.3 静态文件服务

前端文件已移入 `LogViewer.Api/wwwroot/`，ASP.NET Core 默认从该目录提供静态文件：

```csharp
// UseDefaultFiles 必须在 UseStaticFiles 之前
// 访问 http://localhost:5173/ 时自动返回 index.html
app.UseDefaultFiles();
app.UseStaticFiles();
```

### 8.4 启动流程

```
应用启动
    │
    ▼
CleanupOrphanedUploads()    ← 清理不在历史中的孤立上传文件
    │
    ▼
注册中间件（CORS、DefaultFiles、StaticFiles）
    │
    ▼
注册 API 路由
    │
    ▼
监听 http://localhost:5173
    │
    ▼
ApplicationStarted
    │
    ▼
Process.Start("http://localhost:5173")  ← 自动打开默认浏览器
```

### 8.5 系统托盘启动器（LogViewer.Launcher）

**架构：** 独立 WPF 应用，与 `LogViewer.Api.exe` 部署在同一目录下。

```
LogViewer.Launcher.exe
    │
    ├─ OnStartup
    │   ├─ CreateTrayIcon()          ← 创建 NotifyIcon + 深色右键菜单
    │   ├─ StartBackend()            ← 启动 LogViewer.Api.exe 子进程
    │   └─ StartStatusMonitor()      ← 每 2 秒轮询进程状态
    │
    ├─ 后端异常退出
    │   ├─ _crashRestartCount++ 
    │   ├─ ≤ 3 次 → 等待 N 秒后自动重启
    │   └─ > 3 次 → 停止重启，气泡通知用户
    │
    └─ OnExit
        └─ StopBackend()             ← Kill 子进程树
```

**托盘图标状态：**

| 状态 | 图标颜色 | 菜单文字 |
|---|---|---|
| 检测中 | 灰色 `#8b949e` | "  检测中..." |
| 运行中 | 绿色 `#3fb950` | "  运行中    5m 32s" |
| 未运行 | 红色 `#f85149` | "  未运行" |

**右键菜单：**

| 项目 | 行为 | 备注 |
|---|---|---|
| **管理面板**（粗体） | 打开 `http://localhost:5173/admin.html` | 主入口：服务状态 + 文件管理 |
| 打开日志查看器 | 打开 `http://localhost:5173/` | 日志浏览 / 关键字 / 搜索 |
| 重新启动服务 | 停止 + 重启后端 | |
| 停止服务 | 终止后端进程树 | |
| 开机自启动 | 写入注册表 Run 键 | 勾选切换 |
| 退出 | 停止后端 + 退出启动器 | |

**双击托盘图标：** 打开「管理面板」（"托盘 = 控制台" 语义）

**视觉风格：** 与主界面统一的 Terminal Professional 风格
- 菜单背景 `#161b22`，选中项 `#21262d` + Electric Blue 边框
- 分割线 `#30363d`
- 托盘图标：程序化生成（WPF Drawing API），深色底 + 蓝色文档 + 搜索放大镜

### 8.6 管理控制台（admin.html）

**入口：** 双击托盘图标 或 右键菜单「管理面板」。

**URL：** `http://localhost:5173/admin.html`（无需身份验证，仅本机访问）

**技术栈：** 单文件 HTML，复用主界面 Terminal Professional 设计语言（Outfit + JetBrains Mono + Electric Blue）

#### 8.6.1 顶部状态栏

固定于顶部的毛玻璃状态条，包含：

- **状态徽章**（带脉冲绿点动画 + "服务运行中"） / 红色徽章（"服务离线"）
- 标题「LogViewer 管理控制台」
- 「刷新」按钮（手动刷新指标）+ 「打开日志查看器」按钮（跳转 index.html）

#### 8.6.2 指标卡片网格（4 列）

每 5 秒自动刷新：

| 卡片 | 内容 | 数据来源 |
|---|---|---|
| 运行时长 | `12h 5m 30s` 格式 | 进程启动时间 |
| 内存占用 | `123.4 MB` | WorkingSet64 |
| CPU 使用率 | `1.5%` | 两次采样间隔的 TotalProcessorTime 差值 / 时间差 / CPU 核心数 |
| 已索引文件 | `3 个` | FileService._indexCache.Count |

数据变化时触发 Neon Cyan 脉冲动画（`valuePulse`）。

#### 8.6.3 文件管理面板

列出 `Data/uploads/` 目录下所有文件，每 10 秒自动刷新：

- **表头**：复选框全选 / 文件名 / 大小 / 修改时间 / 索引状态
- **索引状态**：已索引文件显示绿色徽章（`· 已索引`）
- **多选支持**：
  - Shift+click 范围选择
  - 表头复选框半选（indeterminate）
- **批量删除按钮**：仅当有选中项时启用，禁用态显示 "批量删除 (0)"
- **二次确认弹窗**：自定义模态框（非原生 alert），列出待删文件清单

#### 8.6.4 删除流程

```
用户选中多个文件
    ↓
点击「批量删除 (N)」
    ↓
弹出确认模态框（列出待删文件）
    ↓
点击「确认删除」
    ↓
DELETE /api/dashboard/files  { paths: [...] }
    ↓
后端遍历每个路径：
    - 安全检查：路径必须在 Data/uploads/ 下
    - File.Delete 永久删除
    - FileService.RemoveIndex 清理索引缓存
    ↓
Toast 通知「成功删除 N 个文件」
    ↓
自动刷新文件列表
```

#### 8.6.5 API 端点

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/dashboard/status` | 服务运行状态（uptimeSeconds, memoryMB, cpuPercent, indexedFileCount, processId, threadCount） |
| GET | `/api/dashboard/files` | 已上传文件列表（fileName, path, sizeBytes, lastModified, isIndexed） |
| DELETE | `/api/dashboard/files` | 批量删除，请求体 `{ paths: [string] }`，返回 `{ deleted, requested }` |

#### 8.6.6 设计细节

- **径向渐变网格背景**：两个径向渐变叠加，营造深空纵深
- **卡片 hover**：`translateY(-2px)` + Electric Blue 顶部 2px 渐变装饰线淡入
- **自定义复选框**：18×18px，勾选时绘制旋转 45° 勾号 + `#0066ff` 填充
- **骨架加载**：文件列表首次加载显示 3 条 shimmer 占位
- **Toast 通知**：右下角滑入，3 秒后淡出，带左侧颜色条（success / danger / info）
- **响应式**：1024px / 640px 两个断点，卡片网格自动折叠

---

## 9. 性能参数与交互反馈

| 场景 | 耗时 | 用户反馈 |
|---|---|---|
| 1GB 日志首次打开（构建索引） | 约 1-3 秒 | "正在构建 xxx 索引..." Loading Overlay |
| 1GB 日志再次打开（缓存命中） | < 100ms | 几乎无感知延迟 |
| 1GB 日志拖拽到任意位置 | < 50ms（Seek 跳转） | shimmer 行加载动画 → 数据到达后自动替换 |
| 虚拟滚动拖拽滚动条 | 60 FPS | 仅渲染可见区域 DOM |
| 内联高亮渲染（单行） | < 1ms | 实时渲染，无感知延迟 |
| 全文搜索（1GB 文件） | 数秒（O(N) 全扫描） | "正在搜索 \"xxx\"..." Overlay + 按钮禁用态 |
| 上传 500MB 文件 | 取决于网络带宽 | 实时进度条 + 已上传/总字节数 |

---

## 10. 构建与运行

**环境要求：**
- .NET 8.0 SDK
- 现代浏览器（Chrome / Edge / Firefox）
- Windows（Launcher 需要 WPF + WinForms）

**构建：**

```bash
dotnet build
```

**运行后端（开发模式）：**

```bash
dotnet run --project src/LogViewer.Api
```

启动后自动打开 `http://localhost:5173`。

**运行启动器（生产模式）：**

```bash
dotnet run --project src/LogViewer.Launcher
```

启动器会自动拉起后端进程，最小化到系统托盘。双击托盘图标打开浏览器。

---

## 11. 文件说明速查表

**启动器（`LogViewer.Launcher`）：**

| 文件 | 用途 |
|---|---|
| `LogViewer.Launcher.csproj` | WPF 项目配置（UseWPF + UseWindowsForms） |
| `App.xaml` | 应用定义（ShutdownMode = OnExplicitShutdown） |
| `App.xaml.cs` | 托盘图标、右键菜单、进程管理、自启动、动态图标生成 |

**后端（`LogViewer.Api`）：**

| 文件 | 用途 |
|---|---|
| `Program.cs` | 入口：DI、中间件、静态文件服务、清理孤立上传、Kestrel 500MB 上传限制 |
| `Models/Keyword.cs` | 关键字数据模型（7 个字段，含 WholeLineOpacity） |
| `Models/KeywordConfig.cs` | 关键字根配置（列表 + 全局大小写 + 主题） |
| `Models/FileRecord.cs` | 历史记录条目（路径 + 文件名 + 最后打开时间） |
| `Services/FileService.cs` | 行偏移索引构建 + Seek O(1) 读取 + 全文搜索 + 索引计数 |
| `Services/KeywordService.cs` | 关键字 JSON 持久化（SemaphoreSlim 加锁） |
| `Services/HistoryService.cs` | 历史记录 CRUD（SQLite + WAL 模式）+ 上传文件联动清理 |
| `Services/DashboardService.cs` | 服务状态监控（运行时长/内存/CPU）+ 上传文件列表 + 批量删除（含安全检查） |
| `Endpoints/KeywordEndpoints.cs` | 关键字 REST API（CRUD + 大小写 + 主题） |
| `Endpoints/FileEndpoints.cs` | 文件信息/读行/搜索/上传 API |
| `Endpoints/HistoryEndpoints.cs` | 历史记录 REST API（sort/search 查询参数） |
| `Endpoints/DashboardEndpoints.cs` | 管理控制台 REST API（status / files） |
| `Data/logviewer.db` | 历史记录持久化文件（SQLite） |
| `Data/keywords.json` | 关键字持久化文件 |
| `Data/uploads/` | 上传文件目录（已 .gitignore） |

**前端（`wwwroot/`）：**

| 文件 | 用途 |
|---|---|
| `index.html` | 日志查看器主界面（左侧关键字+搜索 + 右侧可收起历史面板） |
| `admin.html` | **管理控制台**（服务状态 + 已上传文件批量管理），Terminal Professional Dashboard 风格 |
| `css/style.css` | 主界面样式：Terminal Professional（Tech Innovation 配色 + Outfit/JetBrains Mono） |
| `js/app.js` | 应用主控：Toast/Loading API、事件绑定、XHR 上传进度、搜索 overlay |
| `js/virtual-scroll.js` | 虚拟滚动引擎 + AbortController 请求取消 |
| `js/highlight.js` | 高亮引擎（内联标记 + 整行底色两种模式） |
| `js/keyword-panel.js` | 关键字列表 DOM 渲染、内联编辑、CRUD 操作 |
