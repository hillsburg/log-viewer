# 历史记录系统设计文档

## 1. 概述

将历史记录从 JSON 文件存储迁移至 SQLite 数据库，提升查询性能和数据管理能力。同时将历史记录面板从左侧侧边栏移至右侧独立面板，支持收起/展开。

---

## 2. SQLite 数据库设计

### 2.1 数据库文件位置

```
Data/
├── logviewer.db          ← SQLite 数据库文件
├── keywords.json         （保留，关键字配置）
└── uploads/              （保留，上传文件目录）
```

### 2.2 表结构：`file_history`

```sql
CREATE TABLE file_history (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    FilePath        TEXT    NOT NULL UNIQUE,   -- 文件完整路径（唯一约束，防止重复记录）
    FileName        TEXT    NOT NULL,           -- 显示用文件名（不含目录路径）
    FileSize        INTEGER NOT NULL DEFAULT 0, -- 文件大小（字节），用于历史列表展示
    TotalLines      INTEGER NOT NULL DEFAULT 0, -- 文件总行数，用于历史列表展示
    OpenCount       INTEGER NOT NULL DEFAULT 1, -- 累计打开次数，用于"最常用"排序
    LastOpened      TEXT    NOT NULL,           -- 最后打开时间（ISO 8601 格式，带时区）
    FirstOpened     TEXT    NOT NULL,           -- 首次打开时间（ISO 8601 格式，带时区）
    Source          TEXT    NOT NULL DEFAULT 'local',  -- 文件来源：'local'（本地路径）或 'upload'（上传文件）
    LastScrollLine  INTEGER NOT NULL DEFAULT 0  -- 上次滚动位置（行号），支持"继续上次浏览"
);

-- 索引：加速按最后打开时间排序查询（最常用操作）
CREATE INDEX idx_file_history_last_opened ON file_history(LastOpened DESC);

-- 索引：加速按打开次数排序查询（"最常用"视图）
CREATE INDEX idx_file_history_open_count ON file_history(OpenCount DESC);

-- 索引：加速按来源类型筛选
CREATE INDEX idx_file_history_source ON file_history(Source);
```

### 2.3 字段说明

| 字段 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `Id` | INTEGER | PK, AUTOINCREMENT | 自增主键 |
| `FilePath` | TEXT | NOT NULL, UNIQUE | 文件完整路径，唯一约束防止重复记录。同一路径打开时更新而非新增 |
| `FileName` | TEXT | NOT NULL | 显示用文件名，从路径中提取 |
| `FileSize` | INTEGER | DEFAULT 0 | 文件字节数，打开时记录，用于历史列表展示 |
| `TotalLines` | INTEGER | DEFAULT 0 | 文件总行数，打开时记录，用于历史列表展示 |
| `OpenCount` | INTEGER | DEFAULT 1 | 累计打开次数，每次打开同一文件时 +1 |
| `LastOpened` | TEXT | NOT NULL | 最后打开时间，ISO 8601 格式 |
| `FirstOpened` | TEXT | NOT NULL | 首次打开时间，仅新建时写入 |
| `Source` | TEXT | DEFAULT 'local' | 文件来源类型，`local` = 本地路径，`upload` = 通过上传功能添加 |
| `LastScrollLine` | INTEGER | DEFAULT 0 | 上次浏览的滚动位置（行号），支持"继续上次浏览"功能 |

### 2.4 与现有数据模型对比

| 现有 (FileRecord) | 新增 (SQLite) | 变化说明 |
|---|---|---|
| `FilePath` | `FilePath` | 保持不变，UNIQUE 约束 |
| `FileName` | `FileName` | 保持不变 |
| `LastOpened` | `LastOpened` | 保持不变 |
| — | `Id` | 新增：自增主键 |
| — | `FileSize` | 新增：文件大小 |
| — | `TotalLines` | 新增：总行数 |
| — | `OpenCount` | 新增：累计打开次数 |
| — | `FirstOpened` | 新增：首次打开时间 |
| — | `Source` | 新增：区分本地/上传 |
| — | `LastScrollLine` | 新增：上次滚动位置 |

### 2.5 核心查询

```sql
-- 按最后打开时间降序（默认视图）
SELECT * FROM file_history ORDER BY LastOpened DESC LIMIT 50;

-- 按打开次数降序（"最常用"视图）
SELECT * FROM file_history ORDER BY OpenCount DESC LIMIT 20;

-- 按文件名模糊搜索
SELECT * FROM file_history WHERE FileName LIKE '%keyword%' ORDER BY LastOpened DESC;

-- 按来源筛选
SELECT * FROM file_history WHERE Source = 'upload' ORDER BY LastOpened DESC;

-- 插入或更新（UPSERT）：同一路径打开时更新而非新增
INSERT INTO file_history (FilePath, FileName, FileSize, TotalLines, OpenCount, LastOpened, FirstOpened, Source)
VALUES (@path, @name, @size, @lines, 1, @now, @now, @source)
ON CONFLICT(FilePath) DO UPDATE SET
    FileName   = excluded.FileName,
    FileSize   = excluded.FileSize,
    TotalLines = excluded.TotalLines,
    OpenCount  = OpenCount + 1,
    LastOpened = excluded.LastOpened;

-- 更新滚动位置
UPDATE file_history SET LastScrollLine = @line WHERE FilePath = @path;

-- 删除记录（上传文件同时删除物理文件）
DELETE FROM file_history WHERE FilePath = @path;

-- 清空所有记录
DELETE FROM file_history;
```

---

## 3. UI 设计：右侧可收起历史面板

### 3.1 布局变化

**现有布局：**
```
┌─────────────────────────────────────────────┐
│ 工具栏                                       │
├────────┬────────────────────────────────────┤
│ 左侧   │                                    │
│ 侧边栏 │          日志内容区                  │
│ ┌────┐ │                                    │
│ │历史│ │                                    │
│ │记录│ │                                    │
│ ├────┤ │                                    │
│ │关键│ │                                    │
│ │字  │ │                                    │
│ ├────┤ │                                    │
│ │搜索│ │                                    │
│ └────┘ │                                    │
└────────┴────────────────────────────────────┘
```

**新布局：**
```
┌──────────────────────────────────────────────────┐
│ 工具栏                              [历史记录 ▶] │
├────────┬──────────────────────────┬──────────────┤
│ 左侧   │                          │   右侧       │
│ 侧边栏 │       日志内容区          │   历史面板   │
│ ┌────┐ │                          │  ┌─────────┐ │
│ │关键│ │                          │  │ 搜索框   │ │
│ │字  │ │                          │  ├─────────┤ │
│ ├────┤ │                          │  │ 最近打开 │ │
│ │搜索│ │                          │  │ · a.log  │ │
│ └────┘ │                          │  │ · b.log  │ │
│        │                          │  │ · c.log  │ │
│        │                          │  ├─────────┤ │
│        │                          │  │ 最常用   │ │
│        │                          │  │ · x.log  │ │
│        │                          │  └─────────┘ │
└────────┴──────────────────────────┴──────────────┘
```

### 3.2 交互行为

| 操作 | 行为 |
|---|---|
| 点击工具栏「历史记录」按钮 | 右侧面板展开/收起切换 |
| 点击历史条目 | 打开该文件（同现有行为） |
| 历史条目 hover | 显示「删除」按钮 + 文件详情 tooltip（大小、行数、打开次数） |
| 搜索框输入 | 实时过滤文件名 |
| 视图切换（最近打开 / 最常用） | Tab 切换排序方式 |
| 收起状态 | 面板宽度归零，日志区自动扩展占满 |

### 3.3 面板宽度

- 默认宽度：280px
- 收起后：0px（日志区自动扩展）
- 可拖拽调整宽度（可选，后续迭代）

---

## 4. 迁移方案

### 4.1 启动时自动迁移

应用首次启动检测到 `Data/history.json` 存在且 `Data/logviewer.db` 不存在时，自动执行迁移：

```csharp
// 伪代码
if (File.Exists(jsonPath) && !File.Exists(dbPath))
{
    var records = LoadFromJson(jsonPath);
    foreach (var record in records)
    {
        InsertIntoSqlite(record);
    }
    File.Delete(jsonPath); // 迁移完成后删除旧文件
}
```

### 4.2 向后兼容

- 迁移期间保留 `history.json`，迁移成功后删除
- 迁移失败时静默降级，下次启动重试
- 旧版 `FileRecord` 模型中的字段完整映射到新表

---

## 5. API 变更

### 5.1 新增/修改的端点

| 方法 | 路径 | 说明 | 变更 |
|---|---|---|---|
| `GET` | `/api/history` | 获取历史记录 | 新增查询参数 `sort=recent\|frequent`、`search=keyword` |
| `GET` | `/api/history/{id}` | 获取单条记录详情 | 新增 |
| `DELETE` | `/api/history/{id}` | 删除单条记录 | 改为按 ID 删除（原为按路径） |
| `DELETE` | `/api/history/clear` | 清空所有记录 | 保持不变 |
| `GET` | `/api/history/stats` | 统计信息（总记录数、最近打开等） | 新增 |

### 5.2 响应格式变化

```json
// GET /api/history?sort=recent
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

## 6. 影响范围

### 需要修改的文件

| 文件 | 修改内容 |
|---|---|
| `LogViewer.Api.csproj` | 添加 `Microsoft.Data.Sqlite` NuGet 包 |
| `Models/FileRecord.cs` | 扩展字段（FileSize, TotalLines, OpenCount 等） |
| `Services/HistoryService.cs` | 重写为 SQLite 实现（CRUD + 迁移逻辑） |
| `Endpoints/HistoryEndpoints.cs` | 更新端点（新增排序/搜索参数） |
| `Program.cs` | 注册 SQLite 服务，启动时执行迁移 |
| `wwwroot/index.html` | 移除左侧历史记录区，添加右侧面板结构 |
| `wwwroot/css/style.css` | 新增右侧面板样式 |
| `wwwroot/js/app.js` | 更新 `loadHistory()` 支持新 API，新增面板切换逻辑 |

### 不需要修改的文件

| 文件 | 原因 |
|---|---|
| `Services/FileService.cs` | 与历史记录无关 |
| `Services/KeywordService.cs` | 与历史记录无关 |
| `Endpoints/FileEndpoints.cs` | 与历史记录无关 |
| `Endpoints/KeywordEndpoints.cs` | 与历史记录无关 |
| `wwwroot/admin.html` | 管理面板独立，不受影响 |

---

## 7. 技术选型

### SQLite 库选择

使用 `Microsoft.Data.Sqlite`（微软官方维护，轻量级，无需额外依赖）。

```xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.0" />
```

### 为什么选择 SQLite 而非继续 JSON

| 维度 | JSON 文件 | SQLite |
|---|---|---|
| 并发读写 | 需手动加锁 | 内置 WAL 模式支持 |
| 查询能力 | 全量加载后内存过滤 | SQL 索引加速 |
| 数据完整性 | 无约束 | UNIQUE、NOT NULL、外键 |
| 迁移扩展 | 需手动处理 | 支持 schema migration |
| 文件大小 | 随记录线性增长 | 紧凑存储 + 索引 |

### 为什么选择 Microsoft.Data.Sqlite 而非 EF Core

- 项目规模小，不需要 ORM 的复杂抽象
- 直接写 SQL 更直观，性能更可控
- 减少依赖包数量
