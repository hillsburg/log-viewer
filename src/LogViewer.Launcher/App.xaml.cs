using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MediaColor = System.Windows.Media.Color;
using Microsoft.Win32;

namespace LogViewer.Launcher;

/// <summary>
/// LogViewer 系统托盘启动器。
///
/// 职责：
/// - 启动时自动拉起 LogViewer.Api.exe 后端进程（隐藏窗口）；若已在运行则直接附着
/// - 每 2 秒轮询检测后端进程是否存活（Process.GetProcessesByName）
/// - 托盘图标颜色随状态变化：灰色=检测中，绿色=运行中，红色=未运行
/// - 右键菜单：打开浏览器、状态显示、重启/停止服务、开机自启动、退出
/// - 后端异常退出时自动重启（最多 3 次，间隔递增）
/// - 托盘自身异常退出时由 watchdog + RegisterApplicationRestart 自动拉起（手动退出除外）
/// - 开机自启动通过注册表 HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run 实现
///
/// 设计风格：Terminal Professional（与 LogViewer 主界面统一）
/// - 深色菜单背景 #161b22，Electric Blue #0066ff 选中高亮
/// - 托盘图标：深色底 + 蓝色文档符号 + 搜索放大镜
/// </summary>
public partial class App : Application
{
    // ========== 状态字段 ==========
    private NotifyIcon _trayIcon = null!;
    private Process? _backendProcess;
    private DispatcherTimer _statusTimer = null!;
    private int _crashRestartCount;
    private const int MaxRestartAttempts = 3;
    private DateTime _startTime;
    private volatile bool _isStopping;
    private volatile bool _isIntentionalExit;
    private bool? _lastBackendRunning;
    private Icon? _iconGray;
    private Icon? _iconGreen;
    private Icon? _iconRed;
    private bool _hasOpenedBrowserOnStart;
    private static readonly HttpClient HealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    // ========== 配置常量 ==========
    private static readonly string BackendExeName = "LogViewer.Api.exe";
    private static readonly string BackendProcessName = "LogViewer.Api";
    private static readonly string ServiceUrl = "http://localhost:5173";
    private static readonly string AdminUrl = "http://localhost:5173/admin.html";
    private const string WatchdogArg = "--watchdog";
    private const string LaunchedByTrayEnv = "LOGVIEWER_LAUNCHED_BY_TRAY";
    private static readonly string CleanExitMarkerPath = Path.Combine(
        Path.GetTempPath(), "LogViewer.Launcher.clean-exit");

    /// <summary>后端 exe 路径：启动器所在目录的 server/ 子目录</summary>
    private string BackendExePath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "server", BackendExeName);

    // ========== 生命周期 ==========

    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Watchdog 模式：监视托盘进程，非干净退出时重新拉起
        if (e.Args.Length >= 2 &&
            string.Equals(e.Args[0], WatchdogArg, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(e.Args[1], out int parentPid))
        {
            RunWatchdog(parentPid);
            Shutdown();
            return;
        }

        // 单实例保护：防止同时运行多个托盘启动器
        _mutex = new Mutex(true, "LogViewer.Launcher.SingleInstance", out bool isNew);
        if (!isNew)
        {
            System.Windows.MessageBox.Show("LogViewer 启动器已在运行中。", "LogViewer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        InstallExceptionHandlers();
        SystemEvents.SessionEnding += OnSessionEnding;
        TryDeleteCleanExitMarker();
        RegisterForCrashRestart();
        StartWatchdog();
        CreateTrayIcon();
        StartBackend();
        StartStatusMonitor();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { SystemEvents.SessionEnding -= OnSessionEnding; } catch { /* ignore */ }
        _statusTimer?.Stop();

        // 仅手动退出 / 注销会话时停止后端；异常退出保留 Api，便于托盘重启后附着
        if (_isIntentionalExit)
            StopBackend();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _iconGray?.Dispose();
        _iconGreen?.Dispose();
        _iconRed?.Dispose();

        try { _mutex?.ReleaseMutex(); } catch { /* abandoned/owned */ }
        _mutex?.Dispose();
        _mutex = null;

        base.OnExit(e);
    }

    // ========== 托盘图标 & 右键菜单 ==========

    /// <summary>
    /// 创建系统托盘图标和右键菜单。
    /// 使用 DarkMenuRenderer 实现 Terminal Professional 深色风格。
    /// </summary>
    private void CreateTrayIcon()
    {
        var contextMenu = new ContextMenuStrip();
        contextMenu.Renderer = new DarkMenuRenderer();
        contextMenu.BackColor = ColorTranslator.FromHtml("#161b22");
        contextMenu.ForeColor = ColorTranslator.FromHtml("#e6edf3");
        contextMenu.Font = new Font("Segoe UI", 9f);
        contextMenu.MinimumSize = new System.Drawing.Size(220, 0);

        // 状态指示（只读，不可点击）
        var statusItem = new ToolStripMenuItem("  检测中...")
        {
            Enabled = false,
            ForeColor = ColorTranslator.FromHtml("#8b949e")
        };
        statusItem.Name = "status";

        // 管理面板（主入口，服务状态 / 文件管理）
        var adminItem = new ToolStripMenuItem("  管理面板");
        adminItem.Font = new Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
        adminItem.Click += (_, _) => OpenAdminPanel();

        // 打开日志查看器（日志浏览 / 关键字配置 / 搜索）
        var viewerItem = new ToolStripMenuItem("  打开日志查看器");
        viewerItem.Click += (_, _) => OpenLogViewer();

        // 重启服务
        var restartItem = new ToolStripMenuItem("  重新启动服务");
        restartItem.Click += (_, _) => RestartBackend();

        // 停止服务
        var stopItem = new ToolStripMenuItem("  停止服务");
        stopItem.Click += (_, _) => StopBackend();

        // 开机自启动（勾选项）
        var autoStartItem = new ToolStripMenuItem("  开机自启动")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled()
        };
        autoStartItem.Click += (_, _) => SetAutoStart(autoStartItem.Checked);

        // 退出（干净退出：写标记 + 注销崩溃重启，watchdog 不会再拉起）
        var exitItem = new ToolStripMenuItem("  退出");
        exitItem.Click += (_, _) => RequestIntentionalExit();

        contextMenu.Items.AddRange(new ToolStripItem[]
        {
            adminItem,
            viewerItem,
            new ToolStripSeparator(),
            statusItem,
            new ToolStripSeparator(),
            restartItem,
            stopItem,
            new ToolStripSeparator(),
            autoStartItem,
            new ToolStripSeparator(),
            exitItem
        });

        _iconGray = CreateIcon(MediaColor.FromRgb(0x8b, 0x94, 0x9e));
        _iconGreen = CreateIcon(MediaColor.FromRgb(0x3f, 0xb9, 0x50));
        _iconRed = CreateIcon(MediaColor.FromRgb(0xf8, 0x51, 0x49));

        _trayIcon = new NotifyIcon
        {
            Icon = _iconGray,
            ContextMenuStrip = contextMenu,
            Text = "LogViewer - 检测中...",
            Visible = true
        };

        // 双击托盘图标：打开管理控制台（"托盘 = 控制台" 语义）
        _trayIcon.DoubleClick += (_, _) => OpenAdminPanel();
    }

    // ========== 状态监控 ==========

    /// <summary>启动 2 秒间隔的状态轮询定时器</summary>
    private void StartStatusMonitor()
    {
        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();
    }

    /// <summary>
    /// 检测后端进程存活状态，更新托盘图标颜色和菜单文字。
    /// 运行中 → 绿色图标 + 运行时长
    /// 未运行 → 红色图标
    /// 图标使用缓存实例，避免每 2 秒新建导致 GDI 句柄耗尽后托盘崩溃退出。
    /// </summary>
    private void UpdateStatus()
    {
        var isRunning = IsBackendRunning();
        var statusItem = _trayIcon.ContextMenuStrip?.Items
            .OfType<ToolStripMenuItem>()
            .FirstOrDefault(i => i.Name == "status");

        if (statusItem == null) return;

        if (isRunning)
        {
            var uptime = DateTime.Now - _startTime;
            var uptimeText = uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
                : $"{(int)uptime.TotalMinutes}m {uptime.Seconds}s";

            statusItem.Text = $"  运行中    {uptimeText}";
            statusItem.ForeColor = ColorTranslator.FromHtml("#3fb950");
            _trayIcon.Text = $"LogViewer - 运行中 ({uptimeText})";

            if (_lastBackendRunning != true)
                _trayIcon.Icon = _iconGreen;
        }
        else
        {
            statusItem.Text = "  未运行";
            statusItem.ForeColor = ColorTranslator.FromHtml("#f85149");
            _trayIcon.Text = "LogViewer - 未运行";

            if (_lastBackendRunning != false)
                _trayIcon.Icon = _iconRed;
        }

        _lastBackendRunning = isRunning;
    }

    /// <summary>通过进程名检测后端是否正在运行</summary>
    private bool IsBackendRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(BackendProcessName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var p in processes)
                    p.Dispose();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"IsBackendRunning failed: {ex.Message}");
            return false;
        }
    }

    // ========== 后端进程管理 ==========

    /// <summary>首次启动后端：已有实例则附着，否则拉起新进程</summary>
    private void StartBackend()
    {
        if (!File.Exists(BackendExePath))
        {
            ShowNotification("LogViewer", $"找不到后端程序：{BackendExeName}", ToolTipIcon.Error);
            return;
        }

        _isStopping = false;
        _crashRestartCount = 0;

        if (TryAttachToExistingBackend())
            return;

        LaunchBackendProcess(openBrowserOnReady: true);
    }

    /// <summary>
    /// 附着到已在运行的 LogViewer.Api（托盘重启场景）。
    /// 若存在多个实例，保留第一个并终止其余，避免端口/状态混乱。
    /// </summary>
    private bool TryAttachToExistingBackend()
    {
        try
        {
            var processes = Process.GetProcessesByName(BackendProcessName);
            Process? existing = null;
            foreach (var p in processes)
            {
                if (existing == null && !p.HasExited)
                {
                    existing = p;
                    continue;
                }

                try
                {
                    if (!p.HasExited)
                    {
                        try { p.Kill(entireProcessTree: true); }
                        catch (Exception ex) { Debug.WriteLine($"Kill extra Api failed: {ex.Message}"); }
                    }
                }
                finally
                {
                    p.Dispose();
                }
            }

            if (existing == null)
                return false;

            DetachBackendProcess();
            _backendProcess = existing;
            _backendProcess.EnableRaisingEvents = true;
            _backendProcess.Exited += OnBackendExited;
            _startTime = DateTime.Now;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TryAttachToExistingBackend failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 启动 LogViewer.Api.exe 子进程。
    /// CreateNoWindow = true 隐藏控制台窗口，
    /// EnableRaisingEvents = true 以便监听 Exited 事件。
    /// </summary>
    private void LaunchBackendProcess(bool openBrowserOnReady = false)
    {
        if (_isStopping) return;

        try
        {
            // 启动前若已有 Api（例如附着失败后的竞态），改为附着而非再起一个
            if (TryAttachToExistingBackend())
                return;

            DetachBackendProcess();

            _backendProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = BackendExePath,
                    WorkingDirectory = Path.GetDirectoryName(BackendExePath)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                },
                EnableRaisingEvents = true
            };

            // 告知 Api 由托盘拉起：不要自行弹浏览器（避免崩溃重启反复弹窗）
            _backendProcess.StartInfo.Environment[LaunchedByTrayEnv] = "1";

            _backendProcess.Exited += OnBackendExited;
            _backendProcess.Start();
            _startTime = DateTime.Now;

            var pid = _backendProcess.Id;
            _ = Task.Run(() => WaitForBackendHealthyAsync(pid, openBrowserOnReady));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LaunchBackendProcess failed: {ex}");
            ShowNotification("LogViewer 启动失败", ex.Message, ToolTipIcon.Error);
        }
    }

    /// <summary>轮询 HTTP，确认后端真正可服务后再通知 / 打开浏览器</summary>
    private async Task WaitForBackendHealthyAsync(int pid, bool openBrowserOnReady)
    {
        const int maxAttempts = 20;
        for (var i = 0; i < maxAttempts; i++)
        {
            if (_isStopping) return;

            try
            {
                if (_backendProcess == null || _backendProcess.HasExited || _backendProcess.Id != pid)
                    return;
            }
            catch
            {
                return;
            }

            if (await IsBackendHealthyAsync())
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (_isStopping) return;
                    if (openBrowserOnReady && !_hasOpenedBrowserOnStart)
                    {
                        _hasOpenedBrowserOnStart = true;
                        OpenLogViewer();
                    }
                });
                return;
            }

            await Task.Delay(500);
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isStopping) return;
            ShowNotification("LogViewer",
                "后端进程已启动，但服务未在预期时间内就绪，请检查端口 5173 是否被占用。",
                ToolTipIcon.Warning);
        });
    }

    private static async Task<bool> IsBackendHealthyAsync()
    {
        try
        {
            using var response = await HealthClient.GetAsync(ServiceUrl);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void DetachBackendProcess()
    {
        if (_backendProcess == null) return;
        try { _backendProcess.Exited -= OnBackendExited; } catch { /* ignore */ }
        try { _backendProcess.Dispose(); } catch (Exception ex) { Debug.WriteLine($"Detach dispose: {ex.Message}"); }
        _backendProcess = null;
    }

    /// <summary>
    /// 后端进程退出回调。
    /// 如果不是用户主动停止（_isStopping = false），则尝试自动重启。
    /// 间隔递增：第 1 次 1 秒，第 2 次 2 秒，第 3 次 3 秒。
    /// 超过 3 次停止重启，弹气泡通知用户。
    /// </summary>
    private void OnBackendExited(object? sender, EventArgs e)
    {
        if (_isStopping) return;

        _crashRestartCount++;

        if (_crashRestartCount <= MaxRestartAttempts)
        {
            var attempt = _crashRestartCount;
            Dispatcher.BeginInvoke(() =>
            {
                ShowNotification("LogViewer",
                    $"后端进程异常退出，正在第 {attempt}/{MaxRestartAttempts} 次重启...",
                    ToolTipIcon.Warning);
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000 * attempt);
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (_isStopping) return;
                        LaunchBackendProcess(openBrowserOnReady: false);
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Backend restart schedule failed: {ex.Message}");
                }
            });
        }
        else
        {
            Dispatcher.BeginInvoke(() =>
            {
                ShowNotification("LogViewer",
                    $"后端连续崩溃 {MaxRestartAttempts} 次，已停止自动重启。请检查日志后手动重启。",
                    ToolTipIcon.Error);
            });
        }
    }

    /// <summary>
    /// 停止后端：先杀跟踪句柄，再按进程名清场所有 LogViewer.Api，
    /// 避免孤儿进程在「退出/停止」后仍占用端口。
    /// </summary>
    private void StopBackend()
    {
        _isStopping = true;

        if (_backendProcess != null)
        {
            try
            {
                if (!_backendProcess.HasExited)
                {
                    _backendProcess.Kill(entireProcessTree: true);
                    if (!_backendProcess.WaitForExit(3000))
                        Debug.WriteLine("Tracked backend did not exit within 3s after Kill.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StopBackend tracked kill failed: {ex.Message}");
            }
        }

        KillAllBackendProcesses();
        DetachBackendProcess();
    }

    /// <summary>终止所有 LogViewer.Api 进程（含孤儿实例）</summary>
    private static void KillAllBackendProcesses()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(BackendProcessName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetProcessesByName failed: {ex.Message}");
            return;
        }

        foreach (var p in processes)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    if (!p.WaitForExit(3000))
                        Debug.WriteLine($"Api pid={p.Id} did not exit within 3s.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Kill Api pid={p.Id} failed: {ex.Message}");
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    /// <summary>重启服务：停止全部实例 → 重置计数器 → 重新启动</summary>
    private void RestartBackend()
    {
        StopBackend();
        // 给已排队的自动重启回调一点时间看到 _isStopping，避免竞态再拉起
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            _ = Dispatcher.BeginInvoke(() =>
            {
                _isStopping = false;
                _crashRestartCount = 0;
                LaunchBackendProcess(openBrowserOnReady: false);
            });
        });
    }

    /// <summary>用系统默认浏览器打开指定 URL</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenUrl failed: {ex.Message}");
        }
    }

    /// <summary>打开管理控制台（服务状态 / 已上传文件管理）</summary>
    private static void OpenAdminPanel() => OpenUrl(AdminUrl);

    /// <summary>打开日志查看器主界面</summary>
    private static void OpenLogViewer() => OpenUrl(ServiceUrl);

    // ========== 开机自启动（注册表） ==========

    /// <summary>检查注册表 Run 键中是否已设置 LogViewer 自启动</summary>
    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("LogViewer") != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>设置或取消开机自启动（写入/删除注册表 Run 键）</summary>
    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? "";
                key.SetValue("LogViewer", $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue("LogViewer", false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetAutoStart failed: {ex.Message}");
        }
    }

    // ========== 动态托盘图标生成 ==========

    /// <summary>
    /// 用 WPF Drawing API 程序生成 32x32 托盘图标。
    /// 图标内容：深色圆角底 + 文档符号（蓝色描边）+ 搜索放大镜（Electric Blue）。
    /// 颜色参数控制文档符号的描边色，用于表达不同状态。
    /// </summary>
    private static System.Drawing.Icon CreateIcon(MediaColor color)
    {
        var drawingGroup = new DrawingGroup();

        // 深色背景
        drawingGroup.Children.Add(new GeometryDrawing(
            new SolidColorBrush(MediaColor.FromRgb(0x0d, 0x11, 0x17)),
            null,
            new RectangleGeometry(new Rect(0, 0, 32, 32))));

        // 文档轮廓（带折角）
        var docPath = Geometry.Parse("M8 4 L22 4 L24 6 L24 28 L8 28 Z");
        drawingGroup.Children.Add(new GeometryDrawing(
            new SolidColorBrush(color), null, docPath));

        // 文档内的三条横线（模拟文本行）
        var line1 = new LineGeometry(new System.Windows.Point(11, 10), new System.Windows.Point(21, 10));
        var line2 = new LineGeometry(new System.Windows.Point(11, 14), new System.Windows.Point(21, 14));
        var line3 = new LineGeometry(new System.Windows.Point(11, 18), new System.Windows.Point(18, 18));
        var linesPen = new System.Windows.Media.Pen(new SolidColorBrush(MediaColor.FromRgb(0x0d, 0x11, 0x17)), 1.2);

        drawingGroup.Children.Add(new GeometryDrawing(null, linesPen, line1));
        drawingGroup.Children.Add(new GeometryDrawing(null, linesPen, line2));
        drawingGroup.Children.Add(new GeometryDrawing(null, linesPen, line3));

        // 搜索放大镜（Electric Blue #0066ff）
        var searchCircle = new EllipseGeometry(new System.Windows.Point(22, 22), 4, 4);
        drawingGroup.Children.Add(new GeometryDrawing(
            null,
            new System.Windows.Media.Pen(new SolidColorBrush(MediaColor.FromRgb(0x00, 0x66, 0xff)), 1.8),
            searchCircle));

        var searchLine = Geometry.Parse("M25 25 L29 29");
        drawingGroup.Children.Add(new GeometryDrawing(
            null,
            new System.Windows.Media.Pen(new SolidColorBrush(MediaColor.FromRgb(0x00, 0x66, 0xff)), 1.8),
            searchLine));

        // 渲染为 32x32 位图，转换为 .NET Icon
        var drawingVisual = new DrawingVisual();
        using (var ctx = drawingVisual.RenderOpen())
        {
            ctx.DrawDrawing(drawingGroup);
        }

        var renderTargetBitmap = new RenderTargetBitmap(
            32, 32, 96, 96, PixelFormats.Pbgra32);
        renderTargetBitmap.Render(drawingVisual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderTargetBitmap));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;

        using var bitmap = new System.Drawing.Bitmap(ms);
        var hIcon = bitmap.GetHicon();
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(hIcon);
            return (System.Drawing.Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// 在已有的托盘图标上显示气泡通知。
    /// 如果托盘图标已 Dispose，则静默跳过。
    /// </summary>
    private void ShowNotification(string title, string text, ToolTipIcon icon)
    {
        try
        {
            _trayIcon?.ShowBalloonTip(3000, title, text, icon);
        }
        catch { }
    }

    // ========== 托盘保活：异常拦截 / 崩溃重启 / Watchdog ==========

    private void RequestIntentionalExit()
    {
        _isIntentionalExit = true;
        _isStopping = true;
        try { File.WriteAllText(CleanExitMarkerPath, DateTime.UtcNow.Ticks.ToString()); }
        catch (Exception ex) { Debug.WriteLine($"Write clean-exit marker failed: {ex.Message}"); }
        UnregisterApplicationRestart();
        Shutdown();
    }

    /// <summary>注销/关机时视为干净退出，避免 watchdog 在空会话里再拉起托盘</summary>
    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        if (_isIntentionalExit) return;
        _isIntentionalExit = true;
        _isStopping = true;
        try { File.WriteAllText(CleanExitMarkerPath, DateTime.UtcNow.Ticks.ToString()); }
        catch (Exception ex) { Debug.WriteLine($"SessionEnding marker failed: {ex.Message}"); }
        UnregisterApplicationRestart();
        StopBackend();
    }

    private void InstallExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            try
            {
                ShowNotification("LogViewer", $"发生错误但已继续运行：{args.Exception.Message}", ToolTipIcon.Warning);
            }
            catch { }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // 无法阻止终结时的退出；依赖 watchdog / RegisterApplicationRestart 拉起
            try
            {
                var ex = args.ExceptionObject as Exception;
                Debug.WriteLine($"UnhandledException: {ex}");
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
        };
    }

    private static void TryDeleteCleanExitMarker()
    {
        try
        {
            if (File.Exists(CleanExitMarkerPath))
                File.Delete(CleanExitMarkerPath);
        }
        catch { }
    }

    private static void RegisterForCrashRestart()
    {
        try
        {
            // flags=0：崩溃/无响应时由 WER 重启；手动退出前会 Unregister
            RegisterApplicationRestart(null, 0);
        }
        catch { }
    }

    private static void StartWatchdog()
    {
        try
        {
            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"{WatchdogArg} {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch { }
    }

    /// <summary>
    /// 监视托盘主进程。主进程退出后：
    /// - 若存在干净退出标记 → 不重启（用户点了「退出」）
    /// - 若单实例 Mutex 已被占用 → 说明 WER/其它路径已拉起，跳过
    /// - 否则重新启动托盘
    /// </summary>
    private static void RunWatchdog(int parentPid)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            parent.WaitForExit();
        }
        catch
        {
            // 父进程已不存在
        }

        Thread.Sleep(800);

        if (File.Exists(CleanExitMarkerPath))
        {
            TryDeleteCleanExitMarker();
            return;
        }

        // 避免与 RegisterApplicationRestart 双拉起
        try
        {
            using var probe = new Mutex(false, "LogViewer.Launcher.SingleInstance", out bool createdNew);
            if (!createdNew)
                return;
        }
        catch
        {
            return;
        }

        try
        {
            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterApplicationRestart(string? commandLineArgs, int flags);

    [DllImport("kernel32.dll")]
    private static extern uint UnregisterApplicationRestart();

    // ========== 深色菜单渲染器 ==========
    /// <summary>
    /// 自定义 ToolStrip 渲染器，实现完整的 Terminal Professional 深色风格菜单。
    /// 重写所有会产生浅色区域的默认渲染方法，确保菜单整体一致深色。
    /// 背景 #161b22，选中项 #21262d + Electric Blue 边框，分割线 #30363d。
    /// </summary>
    private class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly System.Drawing.Color MenuBg = ColorTranslator.FromHtml("#161b22");
        private static readonly System.Drawing.Color HoverBg = ColorTranslator.FromHtml("#21262d");
        private static readonly System.Drawing.Color Border = ColorTranslator.FromHtml("#30363d");
        private static readonly System.Drawing.Color Accent = ColorTranslator.FromHtml("#0066ff");
        private static readonly System.Drawing.Color CheckGreen = ColorTranslator.FromHtml("#3fb950");
        private static readonly System.Drawing.Color TextPrimary = ColorTranslator.FromHtml("#e6edf3");
        private static readonly System.Drawing.Color TextSecondary = ColorTranslator.FromHtml("#8b949e");

        /// <summary>菜单整体背景填充</summary>
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(MenuBg);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        /// <summary>
        /// 关键修复：左侧复选框/图标占位符区域（Image Margin）。
        /// 默认是白色渐变背景，改为与菜单主背景同色，消除突兀的白条。
        /// </summary>
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(MenuBg);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        /// <summary>菜单外边框，用细微深色分割边替代默认蓝色</summary>
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new System.Drawing.Pen(Border, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        /// <summary>单个菜单项背景（普通态 vs 悬浮态）</summary>
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(System.Drawing.Point.Empty, e.Item.Size);
            if (e.Item.Selected)
            {
                using var brush = new SolidBrush(HoverBg);
                e.Graphics.FillRectangle(brush, rect);
                using var pen = new System.Drawing.Pen(Accent, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, rect.Width - 1, rect.Height - 1);
            }
            else
            {
                using var brush = new SolidBrush(MenuBg);
                e.Graphics.FillRectangle(brush, rect);
            }
        }

        /// <summary>勾选标记：深色背景方块 + Electric Blue 边框 + 绿色勾号</summary>
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var rect = new Rectangle(e.ImageRectangle.Left - 2, e.ImageRectangle.Top - 2,
                e.ImageRectangle.Width + 4, e.ImageRectangle.Height + 4);

            using var bgBrush = new SolidBrush(HoverBg);
            e.Graphics.FillRectangle(bgBrush, rect);

            using var borderPen = new System.Drawing.Pen(Accent, 1);
            e.Graphics.DrawRectangle(borderPen, rect);

            using var checkPen = new System.Drawing.Pen(CheckGreen, 2);
            e.Graphics.DrawLines(checkPen, new[]
            {
                new System.Drawing.Point(rect.Left + 3, rect.Top + rect.Height / 2),
                new System.Drawing.Point(rect.Left + rect.Width / 2 - 1, rect.Bottom - 4),
                new System.Drawing.Point(rect.Right - 3, rect.Top + 4)
            });
        }

        /// <summary>分割线：居中的深色细线</summary>
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new System.Drawing.Pen(Border, 1);
            e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
        }

        /// <summary>菜单项文字颜色，确保所有文字使用 Terminal Professional 文字色</summary>
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var color = e.Item.Selected ? TextPrimary : TextPrimary;
            if (!e.Item.Enabled)
                color = TextSecondary;

            e.TextColor = color;
            base.OnRenderItemText(e);
        }

        /// <summary>下拉箭头颜色（如有子菜单）</summary>
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = TextSecondary;
            base.OnRenderArrow(e);
        }
    }
}
