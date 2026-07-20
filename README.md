# LogViewer

**[中文文档](README_zh_CN.md)**

A web-based large-file log viewer with virtual scrolling, keyword highlighting, full-text search, and file history — built for developers who frequently browse and analyze `.log` / `.txt` files.

## Features

- **Smooth Large File Browsing** — Virtual scrolling renders only visible rows; handles million-line files at 60 FPS
- **O(1) Random Line Access** — Byte-offset index enables instant seek to any line number
- **Configurable Keyword Highlighting** — Inline text marks + whole-line background coloring, per-keyword case sensitivity and opacity control
- **Full-Text Search** — Search across the entire file with prev/next navigation and current-match outline
- **File History** — Persistent history with "recent" and "most frequent" sorting, filename fuzzy search
- **Drag & Drop Upload** — Drop files into the browser with real-time upload progress
- **Dark / Light Theme** — "Terminal Professional" design language with Electric Blue + Neon Cyan accents
- **Double-Click to Copy** — Double-click any log line to copy its content to clipboard
- **System Tray Launcher** — One-click launch, auto-restart on crash, Windows startup integration

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Backend | ASP.NET Core 8.0 Minimal API |
| Frontend | Vanilla HTML + CSS + JavaScript (zero dependencies) |
| Database | SQLite (WAL mode) |
| Launcher | WPF (Windows system tray) |
| Fonts | Outfit + JetBrains Mono (Google Fonts) |

## Project Structure

```
LogViewer/
├── src/
│   ├── LogViewer.Api/           # Backend (ASP.NET Core)
│   │   ├── Program.cs           # Entry point, DI, middleware
│   │   ├── Endpoints/           # REST API routes
│   │   ├── Models/              # Data models
│   │   ├── Services/            # Business logic
│   │   │   ├── FileService.cs   # Line-offset index + seek read + search
│   │   │   ├── KeywordService.cs
│   │   │   └── HistoryService.cs
│   │   └── wwwroot/             # Frontend static files
│   │       ├── index.html       # Main UI
│   │       ├── admin.html       # Admin console
│   │       ├── css/style.css    # Design system
│   │       └── js/              # App modules
│   └── LogViewer.Launcher/      # System tray launcher (WPF)
└── docs/
    └── DESIGN.md                # Architecture & design document
```

## Getting Started

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- A modern browser (Chrome / Edge / Firefox)
- Windows (required for the system tray launcher)

### Build

```bash
dotnet build
```

### Run (Development Mode)

```bash
dotnet run --project src/LogViewer.Api
```

The browser opens automatically at `http://localhost:5173`.

### Run (Production with Tray Launcher)

```bash
dotnet run --project src/LogViewer.Launcher
```

The launcher starts the backend as a child process and minimizes to the system tray. Double-click the tray icon to open the admin console.

## Usage

### Opening Files

1. **From History** — Click any file in the right-side history panel (sorted by recent or most frequent)
2. **Drag & Drop** — Drag a `.log` or `.txt` file into the browser window; it uploads with a progress bar
3. **Upload Button** — Use the file picker in the toolbar

When a file is opened for the first time, an indexing overlay appears while the backend builds a line-offset index (typically 1–3 seconds for a 1 GB file). Subsequent opens are instant.

### Keyword Highlighting

The left sidebar contains the keyword configuration panel:

1. **Add Keyword** — Click the `+` button, type the keyword text, and press Enter
2. **Configure** — Each keyword supports:
   - Custom highlight color
   - Case-sensitive toggle (`Aa` button, per-keyword)
   - Whole-line highlight mode with adjustable opacity
   - Inline text editing (click the keyword text)
3. **Import / Export** — Bulk import or export keywords as JSON

### Searching

1. Type a keyword in the search box and press Enter or click **Search**
2. Use **↑ Previous** / **Next ↓** to navigate between matches — the current match gets a glowing cyan outline
3. Click **×** (Clear) to remove all search highlights

### Virtual Scrolling

- The log area uses virtual scrolling — only visible rows ± 50 lines are rendered as DOM nodes
- Drag the scrollbar freely; stale requests are auto-cancelled via `AbortController`
- Unloaded rows display a shimmer loading animation
- **Double-click** any line to copy its content to the clipboard

### Admin Console

Access via `http://localhost:5173/admin.html` (or double-click the tray icon):

- Service status: uptime, memory, CPU, indexed file count
- Uploaded file management: browse, multi-select, batch delete
- Auto-refresh every 5–10 seconds

## API Reference

All endpoints are prefixed with `/api` and return JSON.

### File Operations

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/file/info?path=xxx` | File info (triggers index build on first call) |
| `GET` | `/api/file/lines?path=xxx&start=0&count=200` | Read lines by number (max 500) |
| `GET` | `/api/file/search?path=xxx&keyword=yyy&caseSensitive=false` | Full-text search |
| `POST` | `/api/file/upload` | Upload file (multipart, max 500 MB) |

### Keywords

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/keywords` | List all keywords |
| `POST` | `/api/keywords` | Create keyword |
| `PUT` | `/api/keywords/{id}` | Update keyword |
| `DELETE` | `/api/keywords/{id}` | Delete keyword |
| `GET` | `/api/keywords/export` | Export as JSON |
| `POST` | `/api/keywords/import` | Import (replace / merge) |
| `GET` / `PUT` | `/api/keywords/theme` | Get / set theme |

### History

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/history?sort=recent\|frequent&search=xxx` | List history |
| `DELETE` | `/api/history?path=xxx` | Delete single record |
| `DELETE` | `/api/history/clear` | Clear all records |

### Admin

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/dashboard/status` | Service status metrics |
| `GET` | `/api/dashboard/files` | Uploaded file list |
| `DELETE` | `/api/dashboard/files` | Batch delete files |

## Performance

| Scenario | Latency | User Feedback |
|----------|---------|---------------|
| First open (index build, 1 GB) | ~1–3 s | Loading overlay |
| Cached open | < 100 ms | Instant |
| Seek to any line (1 GB) | < 50 ms | Shimmer → data |
| Virtual scroll | 60 FPS | Only visible DOM |
| Full-text search (1 GB) | Several seconds | Search overlay |

## Design System

The UI follows a **"Terminal Professional"** design language:

- **Fonts**: Outfit (UI) + JetBrains Mono (code/logs)
- **Dark theme**: Deep-space backgrounds (`#0d1117` → `#21262d`) with Electric Blue (`#0066ff`) and Neon Cyan accents
- **Light theme**: White surfaces with the same accent color semantics
- **4px grid**: All spacing is multiples of 4
- **Transitions**: 120 ms (fast) / 200 ms (standard) / 300 ms (smooth)

## License

[MIT](LICENSE.txt)
