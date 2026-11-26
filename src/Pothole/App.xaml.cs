using System.IO;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Pothole;

public partial class App : Application
{
    public static string DataDir { get; } = Directory.CreateDirectory(
        Environment.GetEnvironmentVariable("POTHOLE_DATA") is { Length: > 0 } d ? d
        : Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "Pothole-Data")).FullName;

    public static Settings Settings { get; private set; } = null!;
    public static Filter Filter { get; } = new();
    public static Stats Stats { get; } = new();
    public static DnsServer Server { get; private set; } = null!;
    /// <summary>Plain-language status for the window and tray.</summary>
    public static string Status { get; private set; } = "Stopped";
    public static bool ListsLoading { get; private set; }
    public static bool TestMode { get; private set; }
    public static event Action? Changed;

    static Mutex? _single;
    static readonly object LogLock = new();
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem? _trayStartStop, _trayPause;
    MainWindow? _window;
    bool _exiting;

    public static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                var dir = Directory.CreateDirectory(Path.Combine(DataDir, "logs")).FullName;
                File.AppendAllText(Path.Combine(dir, $"pothole-{DateTime.Now:yyyyMMdd}.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the app down */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();
        _single = new Mutex(true, "Pothole.SingleInstance", out bool first);
        if (!first)
        {
            // Another copy is running: ask it to show itself (or exit) and leave.
            var name = args.Contains("--exit") ? "Pothole.Exit" : "Pothole.Show";
            if (EventWaitHandle.TryOpenExisting(name, out var h)) h.Set();
            Shutdown();
            return;
        }
        if (args.Contains("--exit")) { Shutdown(); return; }
        TestMode = args.Contains("--test");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, ex) => { Log("Unhandled: " + ex.Exception); ex.Handled = true; };

        Settings = Settings.Load();
        Stats.Load();
        Server = new DnsServer(Filter, Stats);
        Server.SetUpstreams(Settings.Upstreams);
        Filter.Enabled = Settings.BlockingEnabled;
        // The startup entry is the truth (it can be removed outside Pothole). If this PC relies on Pothole for DNS,
        // it must come back after a restart, so put the entry back.
        Settings.StartWithWindows = StartsWithWindows();
        if (!TestMode && !Settings.StartWithWindows && PcDns.IsOn()) SetStartWithWindows(true);
        Filter.SetCustom(Settings.Allowed, Settings.Blocked);
        Log($"Pothole {typeof(App).Assembly.GetName().Version} started, data in {DataDir}");

        ListenForSignal("Pothole.Show", ShowWindow);
        ListenForSignal("Pothole.Exit", () => ExitApp(ask: false)); // scripted exit (updates) must not wait on a question
        CreateTray();

        if (Settings.StartServerOnLaunch) StartServer();
        _ = ReloadLists(download: false);

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        timer.Tick += (_, _) =>
        {
            Stats.Save();
            // Lists refresh themselves once a day.
            if (!ListsLoading && Settings.BlockLists.Any(b => b.Enabled && (b.Updated == null || b.Updated < DateTime.Now.AddDays(-1))))
                _ = ReloadLists(download: true);
        };
        timer.Start();

        if (!args.Contains("--tray")) ShowWindow();
    }

    void ListenForSignal(string name, Action action)
    {
        var h = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        new Thread(() => { while (true) { h.WaitOne(); Dispatcher.Invoke(action); } }) { IsBackground = true }.Start();
    }

    public static void StartServer()
    {
        var err = Server.Start();
        Status = err ?? "Running";
        Log(err ?? "Server started");
        Changed?.Invoke();
    }

    public static void StopServer()
    {
        Server.Stop();
        Status = "Stopped";
        Log("Server stopped");
        Changed?.Invoke();
    }

    public static async Task ReloadLists(bool download)
    {
        if (ListsLoading) return;
        ListsLoading = true;
        Changed?.Invoke();
        try { await Task.Run(() => Filter.ReloadLists(Settings, download)); }
        finally
        {
            ListsLoading = false;
            SaveSettings();
            Changed?.Invoke();
        }
    }

    public static void SaveSettings()
    {
        try { Settings.Save(); } catch (Exception ex) { Log("Saving settings failed: " + ex.Message); }
    }

    public static void SetBlocking(bool on)
    {
        Settings.BlockingEnabled = Filter.Enabled = on;
        SaveSettings();
        Changed?.Invoke();
    }

    static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("Pothole") != null;
    }

    public static void SetStartWithWindows(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (on) key.SetValue("Pothole", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("Pothole", false);
        Settings.StartWithWindows = on;
        SaveSettings();
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Pothole", null, (_, _) => ShowWindow());
        _trayStartStop = new Forms.ToolStripMenuItem("Stop", null, (_, _) => { if (Server.Running) StopServer(); else StartServer(); });
        _trayPause = new Forms.ToolStripMenuItem("Pause blocking", null, (_, _) => SetBlocking(!Settings.BlockingEnabled));
        menu.Items.Add(_trayStartStop);
        menu.Items.Add(_trayPause);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, ev) => { if (ev.Button == Forms.MouseButtons.Left) ShowWindow(); };
        Changed += UpdateTray;
        UpdateTray();
    }

    void UpdateTray() => Dispatcher.BeginInvoke(() =>
    {
        if (_tray == null) return;
        _trayStartStop!.Text = Server.Running ? "Stop server" : "Start server";
        _trayPause!.Text = Settings.BlockingEnabled ? "Pause blocking" : "Resume blocking";
        var text = $"Pothole: {(Server.Running ? Settings.BlockingEnabled ? "running" : "running, blocking paused" : "stopped")}";
        _tray.Text = text.Length > 63 ? text[..63] : text;
    });

    public void ShowWindow()
    {
        if (_window == null)
        {
            _window = new MainWindow();
            _window.Closing += (_, ev) =>
            {
                if (!_exiting && Settings.CloseToTray) { ev.Cancel = true; _window.Hide(); }
                else if (!_exiting) ExitApp();
            };
        }
        if (TestMode)
        {
            // Background testing: stays off-screen and never takes focus.
            _window.ShowActivated = false;
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Left = -32000;
            _window.Show();
            return;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ExitApp(bool ask = true)
    {
        if (_exiting) return;
        // Without Pothole this PC couldn't look anything up, so offer to put its DNS back first.
        if (ask && !TestMode && PcDns.IsOn() && MessageBox.Show(
                "This PC is using Pothole for DNS. Put its DNS setting back to how it was before Pothole, so the internet keeps working after Pothole closes?",
                "Pothole", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            var err = PcDns.Set(false).GetAwaiter().GetResult();
            if (err != null) MessageBox.Show(err + " Set DNS back to automatic in Windows' network settings.", "Pothole");
        }
        _exiting = true;
        Server.Stop();
        Stats.Save();
        SaveSettings();
        if (_tray != null) _tray.Visible = false;
        _window?.Close();
        Log("Exited");
        Shutdown();
    }
}
