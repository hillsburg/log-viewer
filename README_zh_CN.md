# LogViewer

**[English](README.md)**

基于 Web 的大文件日志查看工具，支持虚拟滚动、关键字高亮、全文搜索和文件历史记录——专为高频浏览和分析 `.log` / `.txt` 日志文件的开发者打造。

## 功能特性

- **大文件流畅浏览** — 虚拟滚动仅渲染可见区域 DOM，百万行文件 60 FPS 无卡顿
- **O(1) 随机行跳转** — 字节偏移索引，Seek 直达任意行号
- **可配置关键字高亮** — 内联文本标记 + 整行背景着色，每个关键字独立的大小写敏感和透明度控制
- **全文搜索** — 全文件搜索 + 上/下导航 + 当前匹配行发光描边
- **文件历史记录** — 持久化历史，支持"最近打开"和"最常用"排序，文件名模糊搜索
- **拖拽上传** — 将文件拖入浏览器即可上传，带实时进度条
- **深色 / 浅色主题** — "Terminal Professional" 设计语言，Electric Blue + Neon Cyan 强调色
- **双击复制** — 双击任意日志行即复制内容到剪贴板
- **系统托盘启动器** — 一键启动、崩溃自动重启、开机自启动

## 技术栈

| 层级 | 技术 |
|------|------|
| 后端 | ASP.NET Core 8.0 Minimal API |
| 前端 | 纯 HTML + CSS + JavaScript（零依赖） |
| 数据库 | SQLite（WAL 模式） |
| 启动器 | WPF（Windows 系统托盘） |
| 字体 | Outfit + JetBrains Mono（Google Fonts） |

## 项目结构

```
LogViewer/
├── src/
│   ├── LogViewer.Api/           # 后端（ASP.NET Core）
│   │   ├── Program.cs           # 入口：DI、中间件
│   │   ├── Endpoints/           # REST API 路由
│   │   ├── Models/              # 数据模型
│   │   ├── Services/            # 业务逻辑
│   │   │   ├── FileService.cs   # 行偏移索引 + Seek 读取 + 搜索
│   │   │   ├── KeywordService.cs
│   │   │   └── HistoryService.cs
│   │   └── wwwroot/             # 前端静态文件
│   │       ├── index.html       # 主界面
│   │       ├── admin.html       # 管理控制台
│   │       ├── css/style.css    # 设计系统
│   │       └── js/              # 应用模块
│   └── LogViewer.Launcher/      # 系统托盘启动器（WPF）
└── docs/
    └── DESIGN.md                # 架构与设计文档
```

## 快速开始

### 环境要求

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 现代浏览器（Chrome / Edge / Firefox）
- Windows（启动器依赖 WPF）

### 构建

```bash
dotnet build
```

### 运行（开发模式）

```bash
dotnet run --project src/LogViewer.Api
```

启动后自动打开浏览器，地址为 `http://localhost:5173`。

### 运行（生产模式 + 托盘启动器）

```bash
dotnet run --project src/LogViewer.Launcher
```

启动器在后台拉起服务进程并最小化到系统托盘。双击托盘图标打开管理控制台。

## 使用方式

### 打开文件

1. **从历史记录** — 点击右侧历史面板中的文件（可按"最近打开"或"最常用"排序）
2. **拖拽文件** — 将 `.log` 或 `.txt` 文件拖入浏览器窗口，带进度条上传
3. **上传按钮** — 使用工具栏中的文件选择器

首次打开文件时，后端需要扫描全文构建行偏移索引（1 GB 文件约 1–3 秒），期间会显示"正在构建索引..."覆盖层。后续打开瞬间完成。

### 关键字高亮

左侧侧边栏包含关键字配置面板：

1. **添加关键字** — 点击 `+` 按钮，输入关键字文本后回车
2. **配置选项** — 每个关键字支持：
   - 自定义高亮颜色
   - 大小写敏感切换（`Aa` 按钮，逐关键字独立控制）
   - 整行高亮模式，可调节背景透明度
   - 内联文本编辑（点击关键字文本即可修改）
3. **导入 / 导出** — 批量导入或导出关键字配置为 JSON

### 搜索

1. 在搜索框输入关键字，按回车或点击 **搜索**
2. 使用 **↑ 上一个** / **下一个 ↓** 在匹配结果间导航——当前匹配行显示 Neon Cyan 发光描边
3. 点击 **×**（清除）移除所有搜索高亮

### 虚拟滚动

- 日志区域使用虚拟滚动——仅渲染可见区域 ± 50 行缓冲区
- 随意拖拽滚动条，过期请求通过 `AbortController` 自动取消
- 未加载行显示 shimmer 脉冲加载动画
- **双击** 任意行复制内容到剪贴板

### 管理控制台

访问 `http://localhost:5173/admin.html`（或双击托盘图标）：

- 服务状态：运行时长、内存占用、CPU 使用率、已索引文件数
- 上传文件管理：浏览、多选、批量删除
- 每 5–10 秒自动刷新

## API 接口

所有接口前缀为 `/api`，返回 JSON 格式。

### 文件操作

| 方法 | 路径 | 说明 |
|------|------|------|
| `GET` | `/api/file/info?path=xxx` | 获取文件信息（首次调用触发索引构建） |
| `GET` | `/api/file/lines?path=xxx&start=0&count=200` | 按行号读取（上限 500） |
| `GET` | `/api/file/search?path=xxx&keyword=yyy&caseSensitive=false` | 全文搜索 |
| `POST` | `/api/file/upload` | 上传文件（multipart，最大 500 MB） |

### 关键字

| 方法 | 路径 | 说明 |
|------|------|------|
| `GET` | `/api/keywords` | 获取所有关键字 |
| `POST` | `/api/keywords` | 新建关键字 |
| `PUT` | `/api/keywords/{id}` | 更新关键字 |
| `DELETE` | `/api/keywords/{id}` | 删除关键字 |
| `GET` | `/api/keywords/export` | 导出为 JSON |
| `POST` | `/api/keywords/import` | 导入（替换 / 合并） |
| `GET` / `PUT` | `/api/keywords/theme` | 获取 / 设置主题 |

### 历史记录

| 方法 | 路径 | 说明 |
|------|------|------|
| `GET` | `/api/history?sort=recent\|frequent&search=xxx` | 查询历史 |
| `DELETE` | `/api/history?path=xxx` | 删除单条记录 |
| `DELETE` | `/api/history/clear` | 清空全部记录 |

### 管理控制台

| 方法 | 路径 | 说明 |
|------|------|------|
| `GET` | `/api/dashboard/status` | 服务运行指标 |
| `GET` | `/api/dashboard/files` | 已上传文件列表 |
| `DELETE` | `/api/dashboard/files` | 批量删除文件 |

## 性能参数

| 场景 | 耗时 | 用户反馈 |
|------|------|---------|
| 首次打开（索引构建，1 GB） | ~1–3 秒 | 加载覆盖层 |
| 缓存命中打开 | < 100 ms | 几乎无感知 |
| Seek 到任意行（1 GB） | < 50 ms | shimmer 动画 → 数据到达 |
| 虚拟滚动拖拽 | 60 FPS | 仅渲染可见 DOM |
| 全文搜索（1 GB） | 数秒 | 搜索覆盖层 |

## 设计系统

UI 遵循 **"Terminal Professional"** 设计语言：

- **字体**：Outfit（界面）+ JetBrains Mono（代码/日志）
- **深色主题**：深空背景（`#0d1117` → `#21262d`），Electric Blue（`#0066ff`）和 Neon Cyan 强调色
- **浅色主题**：白色表面，保持相同色彩语义
- **4px 网格**：所有间距为 4 的倍数
- **过渡动画**：120 ms（快速）/ 200 ms（标准）/ 300 ms（缓动）

## 许可证

[MIT](LICENSE.txt)
