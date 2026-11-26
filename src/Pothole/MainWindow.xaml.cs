using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Pothole;

public sealed record ChartSlot(string Label, string Tip);

public partial class MainWindow : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();
        var s = App.Settings;
        ListsView.ItemsSource = s.BlockLists;
        UpstreamBox.Text = string.Join(", ", s.Upstreams);
        StartWithWindowsBox.IsChecked = s.StartWithWindows;
        AutoStartBox.IsChecked = s.StartServerOnLaunch;
        CloseToTrayBox.IsChecked = s.CloseToTray;
        AboutText.Text = $"Pothole {typeof(App).Assembly.GetName().Version?.ToString(3)}   ·   Data: {App.DataDir}";
        RefreshCustomLists();
        foreach (RadioButton r in SpanButtons.Children) r.IsChecked = (string)r.Tag == s.ChartSpan.ToString();

        App.Changed += OnChanged;
        _timer.Tick += (_, _) => RefreshLive();
        IsVisibleChanged += (_, _) => { if (IsVisible) { OnChanged(); _timer.Start(); } else _timer.Stop(); };
        OnChanged();
    }

    void OnChanged() => Dispatcher.BeginInvoke(() =>
    {
        bool running = App.Server.Running, paused = !App.Settings.BlockingEnabled;
        StartStopButton.Content = running ? "Stop" : "Start";
        StartStopButton.Background = running ? (Brush)FindResource("Bad") : (Brush)FindResource("Good");
        StatusText.Text = !running ? "Stopped" : paused ? "Running · blocking paused" : "Running";
        StatusPill.Background = (Brush)FindResource(!running ? "Muted" : paused ? "Warn" : "Good");
        PauseBox.IsChecked = paused;
        bool usePc = PcDns.IsOn();
        if (UsePcBox.IsEnabled) UsePcBox.IsChecked = usePc;

        var lan = LanAddresses();
        var ip = lan.FirstOrDefault() ?? "this PC's address";
        StatusDetail.Text = running
            ? $"Devices are protected when their DNS server is set to {string.Join(" or ", lan.DefaultIfEmpty("this PC's address"))}."
            : App.Status != "Stopped" ? App.Status
            : usePc ? "This PC can't look up sites while Pothole is stopped. Press Start, or untick \"Use on this PC\"."
            : "Press Start to begin blocking ads and trackers.";
        StatusDetail.Foreground = (Brush)FindResource(running || App.Status == "Stopped" && !usePc ? "Muted" : "Bad");
        HowTo.Text =
            $"1. Press Start. Pothole now answers DNS requests (the lookups that turn site names into addresses) on this PC.\n" +
            $"2. Point a device at it: in the device's Wi-Fi or network settings, set the DNS server to {ip}.\n" +
            $"3. To cover every device at home at once, log in to your router and set its DNS server to {ip}.\n" +
            $"4. For this PC itself, tick \"Use on this PC\" at the top (Windows asks for permission).\n" +
            $"Keep this PC on and Pothole running, or those devices can't look up sites. If something breaks, press Stop and set DNS back to automatic.";

        ListsStatus.Text = App.ListsLoading ? "Downloading and loading lists…" : $"{App.Filter.ListCount:N0} unique sites blocked by your lists";
        UpdateListsButton.IsEnabled = !App.ListsLoading;
        ListsView.Items.Refresh();
        RefreshLive();
    });

    /// <summary>IPv4 addresses other devices can reach: those on adapters with a default gateway.</summary>
    static List<string> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString()).Distinct().ToList();

    void RefreshLive()
    {
        if (!IsVisible) return;
        var d = App.Stats.Snapshot;
        TotalText.Text = d.Total.ToString("N0");
        BlockedText.Text = d.Blocked.ToString("N0");
        PercentText.Text = d.Total == 0 ? "0%" : $"{100.0 * d.Blocked / d.Total:0.#}%";
        ListText.Text = App.Filter.ListCount.ToString("N0");
        SinceText.Text = $"Counting since {d.Since:g}";

        var span = App.Settings.ChartSpan;
        _series = App.Stats.Series(span);
        DrawChart();
        ChartTitle.Text = span switch
        {
            ChartSpan.Minute => "Last 60 minutes", ChartSpan.Hour => "Last 24 hours", ChartSpan.Day => "Last 30 days",
            ChartSpan.Month => "Last 12 months", _ => "Last 5 years",
        };
        // Labels on every few points so they don't crowd; the tooltip names each point in full.
        Chart.ItemsSource = _series.Select((x, i) => new ChartSlot(
            span switch
            {
                ChartSpan.Minute => x.Start.Minute % 10 == 0 ? x.Start.ToString("HH:mm") : "",
                ChartSpan.Hour => x.Start.Hour % 3 == 0 ? x.Start.ToString("HH") : "",
                ChartSpan.Day => (_series.Length - 1 - i) % 5 == 0 ? x.Start.ToString("MMM d") : "",
                ChartSpan.Month => x.Start.ToString("MMM"),
                _ => x.Start.ToString("yyyy"),
            },
            span switch
            {
                ChartSpan.Minute => x.Start.ToString("HH:mm"),
                ChartSpan.Hour => $"{x.Start:HH}:00 to {x.Start.AddHours(1):HH}:00",
                ChartSpan.Day => x.Start.ToString("dddd, MMMM d"),
                ChartSpan.Month => x.Start.ToString("MMMM yyyy"),
                _ => x.Start.ToString("yyyy"),
            } + $"\n{x.Queries:N0} requests, {x.Blocked:N0} blocked")).ToList();
        TopSites.ItemsSource = Stats.Top(d.Sites, 10);
        TopBlocked.ItemsSource = Stats.Top(d.BlockedSites, 10);
        TopDevices.ItemsSource = Stats.Top(d.Devices, 10);

        if (LogList.SelectedItem == null) RefreshLog();
    }

    (DateTime Start, long Queries, long Blocked)[] _series = [];

    void Span_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.ChartSpan = Enum.Parse<ChartSpan>((string)((FrameworkElement)sender).Tag);
        App.SaveSettings();
        RefreshLive();
    }

    void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    /// <summary>Trend lines for requests and blocked per minute/hour/day/month/year, each point centred over its hour label.</summary>
    void DrawChart()
    {
        ChartCanvas.Children.Clear();
        double w = ChartCanvas.ActualWidth, top = 8, h = ChartCanvas.ActualHeight - top;
        if (w <= 0 || h <= 0 || _series.Length == 0) return;
        double max = Math.Max(1, _series.Max(x => x.Queries)), step = w / _series.Length;
        PointCollection Line(Func<(DateTime Start, long Queries, long Blocked), long> value) =>
            new(_series.Select((x, i) => new Point((i + 0.5) * step, top + h - value(x) / max * h)));
        var requests = Line(x => x.Queries);
        var blocked = Line(x => x.Blocked);
        var area = requests.Clone();
        area.Add(new Point(requests[^1].X, top + h));
        area.Add(new Point(requests[0].X, top + h));
        var accent = (SolidColorBrush)FindResource("Accent");
        ChartCanvas.Children.Add(new Polygon { Points = area, Fill = new SolidColorBrush(accent.Color) { Opacity = 0.12 } });
        ChartCanvas.Children.Add(new Polyline { Points = requests, Stroke = accent, StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round });
        ChartCanvas.Children.Add(new Polyline { Points = blocked, Stroke = (Brush)FindResource("Bad"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
        foreach (var (pts, brush) in new[] { (requests, (Brush)accent), (blocked, (Brush)FindResource("Bad")) })
        {
            // a dot on the current hour so "now" stands out
            var dot = new Ellipse { Width = 7, Height = 7, Fill = brush };
            Canvas.SetLeft(dot, pts[^1].X - 3.5);
            Canvas.SetTop(dot, pts[^1].Y - 3.5);
            ChartCanvas.Children.Add(dot);
        }
    }

    void RefreshLog()
    {
        var q = LogSearch.Text.Trim();
        bool onlyBlocked = OnlyBlocked.IsChecked == true;
        LogList.ItemsSource = App.Stats.Recent()
            .Where(e => (!onlyBlocked || e.Result == QueryResult.Blocked) &&
                        (q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Client.Contains(q)))
            .Take(500).ToList();
    }

    void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server.Running) App.StopServer(); else App.StartServer();
    }

    void PauseBox_Click(object sender, RoutedEventArgs e) => App.SetBlocking(PauseBox.IsChecked != true);

    async void UsePcBox_Click(object sender, RoutedEventArgs e)
    {
        bool on = UsePcBox.IsChecked == true;
        if (on && !App.Server.Running) App.StartServer();
        if (on)
        {
            // After a restart this PC would have no DNS at all unless Pothole comes back with it.
            App.SetStartWithWindows(true);
            StartWithWindowsBox.IsChecked = true;
        }
        UsePcBox.IsEnabled = false;
        var err = await PcDns.Set(on);
        UsePcBox.IsEnabled = true;
        if (err != null) MessageBox.Show(this, err, "Pothole");
        OnChanged();
    }

    void ResetStats_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Clear all counters and the activity list?", "Pothole", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            App.Stats.Reset();
            RefreshLive();
        }
    }

    // ---- Activity ----
    void LogFilter_Changed(object sender, RoutedEventArgs e) { LogList.SelectedItem = null; RefreshLog(); }

    void LogList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { LogList.SelectedItem = null; RefreshLog(); }
    }

    void LogBlock_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is QueryEntry q) _ = AddCustom(App.Settings.Blocked, App.Settings.Allowed, q.Name, "block");
    }

    void LogAllow_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is QueryEntry q) _ = AddCustom(App.Settings.Allowed, App.Settings.Blocked, q.Name, "allow");
    }

    // ---- Allow & block ----
    /// <summary>Adds a site to one list (removing it from the other) and applies it at once.</summary>
    bool AddCustom(List<string> to, List<string> other, string text, string verb)
    {
        var d = Filter.Clean(text);
        if (d == null)
        {
            MessageBox.Show(this, $"\"{text}\" doesn't look like a site name. Type something like example.com.", "Pothole");
            return false;
        }
        other.Remove(d);
        if (!to.Contains(d)) to.Add(d);
        to.Sort();
        ApplyCustom();
        App.Log($"User chose to {verb} {d}");
        return true;
    }

    void ApplyCustom()
    {
        App.Filter.SetCustom(App.Settings.Allowed, App.Settings.Blocked);
        App.SaveSettings();
        RefreshCustomLists();
    }

    void RefreshCustomLists()
    {
        AllowedList.ItemsSource = App.Settings.Allowed.ToList();
        BlockedList.ItemsSource = App.Settings.Blocked.ToList();
    }

    void AddAllowed_Click(object sender, RoutedEventArgs e)
    {
        if (AllowBox.Text.Trim().Length == 0) return;
        if (AddCustom(App.Settings.Allowed, App.Settings.Blocked, AllowBox.Text, "allow")) AllowBox.Clear();
    }

    void AddBlocked_Click(object sender, RoutedEventArgs e)
    {
        if (BlockBox.Text.Trim().Length == 0) return;
        if (AddCustom(App.Settings.Blocked, App.Settings.Allowed, BlockBox.Text, "block")) BlockBox.Clear();
    }

    void AllowBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddAllowed_Click(sender, e); }
    void BlockBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddBlocked_Click(sender, e); }

    void RemoveAllowed_Click(object sender, RoutedEventArgs e)
    {
        foreach (string d in AllowedList.SelectedItems) App.Settings.Allowed.Remove(d);
        ApplyCustom();
    }

    void RemoveBlocked_Click(object sender, RoutedEventArgs e)
    {
        foreach (string d in BlockedList.SelectedItems) App.Settings.Blocked.Remove(d);
        ApplyCustom();
    }

    // ---- Block lists ----
    void ListEnabled_Click(object sender, RoutedEventArgs e) => _ = App.ReloadLists(download: false);

    void UpdateLists_Click(object sender, RoutedEventArgs e) => _ = App.ReloadLists(download: true);

    void AddList_Click(object sender, RoutedEventArgs e)
    {
        var url = NewListUrl.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(this, "Paste the full web address of the list, starting with https://", "Pothole");
            return;
        }
        if (App.Settings.BlockLists.Any(b => b.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) return;
        App.Settings.BlockLists.Add(new BlockListSource { Name = NewListName.Text.Trim() is { Length: > 0 } n ? n : u.Host, Url = url, Enabled = true });
        NewListName.Clear();
        NewListUrl.Clear();
        _ = App.ReloadLists(download: false);
    }

    void RemoveList_Click(object sender, RoutedEventArgs e)
    {
        if (ListsView.SelectedItem is not BlockListSource b) return;
        App.Settings.BlockLists.Remove(b);
        _ = App.ReloadLists(download: false);
    }

    // ---- Settings ----
    void Preset_Click(object sender, RoutedEventArgs e)
    {
        UpstreamBox.Text = (string)((Button)sender).Tag;
        SaveUpstream_Click(sender, e);
    }

    void SaveUpstream_Click(object sender, RoutedEventArgs e)
    {
        var list = UpstreamBox.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).ToList();
        var bad = list.Where(x => !IPEndPoint.TryParse(x, out _)).ToList();
        if (list.Count == 0 || bad.Count > 0)
        {
            UpstreamStatus.Text = bad.Count > 0 ? $"Not a valid address: {string.Join(", ", bad)}. Use numbers like 1.1.1.1." : "Enter at least one address.";
            UpstreamStatus.Foreground = (Brush)FindResource("Bad");
            return;
        }
        App.Settings.Upstreams = list;
        App.Server.SetUpstreams(list);
        App.SaveSettings();
        UpstreamStatus.Text = "Saved. New requests use " + string.Join(", ", list) + ".";
        UpstreamStatus.Foreground = (Brush)FindResource("Good");
    }

    void Option_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.StartServerOnLaunch = AutoStartBox.IsChecked == true;
        s.CloseToTray = CloseToTrayBox.IsChecked == true;
        if (sender == StartWithWindowsBox) App.SetStartWithWindows(StartWithWindowsBox.IsChecked == true);
        App.SaveSettings();
    }

    void OpenData_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", App.DataDir);
}
