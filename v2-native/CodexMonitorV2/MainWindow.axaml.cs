using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LiquidGlassAvaloniaUI;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexMonitorV2;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<TaskRow> Tasks { get; } = new();
    private readonly DispatcherTimer _dataTimer;
    private bool _dataBusy;
    private Bitmap? _sceneBitmap;
    private bool? _sceneIsDay;
    private bool _showTokens;
    private int _themeIndex;
    private DateTime _lastQuotaRefresh = DateTime.MinValue;

    private readonly (Color Tint, Color Surface)[] _themes =
    {
        (Color.Parse("#0EFFFFFF"), Color.Parse("#08FFFFFF")),
        (Color.Parse("#16DCE5EA"), Color.Parse("#0CFFFFFF")),
        (Color.Parse("#14DDD6F0"), Color.Parse("#0CFFFFFF")),
        (Color.Parse("#14CFE8E3"), Color.Parse("#0CFFFFFF")),
        (Color.Parse("#14EDD5DE"), Color.Parse("#0CFFFFFF"))
    };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _dataTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _dataTimer.Tick += async (_, _) =>
        {
            ApplySceneBackground();
            await RefreshDataAsync();
        };
        Opened += async (_, _) =>
        {
            ApplyNativeWindowMaterial();
            ApplySceneBackground();
            await RefreshDataAsync();
            _dataTimer.Start();
        };
        SizeChanged += (_, _) => ApplyNativeWindowMaterial();
        Closed += (_, _) =>
        {
            _dataTimer.Stop();
            _sceneBitmap?.Dispose();
        };

        this.FindControl<Control>("TitleBar")!.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("MinimizeButton")!.Click += (_, _) => WindowState = WindowState.Minimized;
        this.FindControl<Button>("PinButton")!.Click += (_, _) => Topmost = !Topmost;
        this.FindControl<Button>("TokenButton")!.Click += (_, _) =>
        {
            _showTokens = !_showTokens;
            RefreshTaskDetails();
        };
        this.FindControl<Button>("ThemeButton")!.Click += (_, _) => ApplyNextTheme();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void ApplyNativeWindowMaterial()
    {
        nint hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd == 0) return;
        int rounded = 2;
        DwmSetWindowAttribute(hwnd, 33, ref rounded, sizeof(int));
        int noSystemBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 34, ref noSystemBorder, sizeof(int));

        double scale = RenderScaling;
        int width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale));
        int height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        int radius = Math.Max(1, (int)Math.Round(28 * scale));
        nint region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region != 0 && SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
    }

    private void ApplySceneBackground()
    {
        bool isDay = DateTime.Now.Hour is >= 6 and < 18;
        if (_sceneIsDay == isDay) return;

        Uri uri = new($"avares://CodexMonitorV2/Assets/{(isDay ? "sun" : "moon")}.png");
        Bitmap next = new(AssetLoader.Open(uri));
        this.FindControl<Image>("BackdropImage")!.Source = next;
        Bitmap? old = _sceneBitmap;
        _sceneBitmap = next;
        _sceneIsDay = isDay;
        old?.Dispose();
    }

    private async Task RefreshDataAsync()
    {
        if (_dataBusy) return;
        _dataBusy = true;
        try
        {
            IReadOnlyList<MonitorData.TaskSnapshot> snapshots = await MonitorData.ReadActiveTasksAsync();
            Tasks.Clear();
            foreach (MonitorData.TaskSnapshot item in snapshots)
                Tasks.Add(new TaskRow(item.Title, item.Detail, item.Tokens) { ShowTokens = _showTokens });

            this.FindControl<TextBlock>("StatusText")!.Text = snapshots.Count == 0
                ? "暂无正在调用的任务"
                : $"正在调用 {snapshots.Count} 个任务";
            this.FindControl<TextBlock>("UpdatedText")!.Text = $"更新：{DateTime.Now:HH:mm:ss}";

            if (DateTime.Now - _lastQuotaRefresh >= TimeSpan.FromSeconds(30))
            {
                MonitorData.QuotaSnapshot? quota = await MonitorData.ReadQuotaAsync();
                if (quota is not null) UpdateQuota(quota);
                _lastQuotaRefresh = DateTime.Now;
            }
        }
        catch (Exception error)
        {
            this.FindControl<TextBlock>("StatusText")!.Text = "读取失败";
            this.FindControl<TextBlock>("UpdatedText")!.Text = error.Message;
        }
        finally
        {
            _dataBusy = false;
        }
    }

    private void UpdateQuota(MonitorData.QuotaSnapshot quota)
    {
        UpdateQuotaRow(
            quota.Primary,
            300,
            this.FindControl<ProgressBar>("FiveQuotaBar")!,
            this.FindControl<TextBlock>("FiveQuotaValue")!,
            this.FindControl<TextBlock>("FiveQuotaCaption")!,
            this.FindControl<RingGauge>("FiveResetRing")!,
            this.FindControl<TextBlock>("FiveResetValue")!,
            this.FindControl<TextBlock>("FiveResetCaption")!,
            reset => $"{reset:HH:mm} 重置");

        UpdateQuotaRow(
            quota.Secondary,
            10080,
            this.FindControl<ProgressBar>("WeekQuotaBar")!,
            this.FindControl<TextBlock>("WeekQuotaValue")!,
            this.FindControl<TextBlock>("WeekQuotaCaption")!,
            this.FindControl<RingGauge>("WeekResetRing")!,
            this.FindControl<TextBlock>("WeekResetValue")!,
            this.FindControl<TextBlock>("WeekResetCaption")!,
            reset => $"{reset:M月d日} 重置");
    }

    private static void UpdateQuotaRow(
        MonitorData.WindowLimit limit,
        double fallbackWindowMinutes,
        ProgressBar bar,
        TextBlock valueText,
        TextBlock quotaCaption,
        RingGauge ring,
        TextBlock resetValue,
        TextBlock resetCaption,
        Func<DateTime, string> formatReset)
    {
        double used = Math.Clamp(limit.UsedPercent, 0, 100);
        int remaining = (int)Math.Round(100 - used);
        bar.Value = remaining;
        valueText.Text = $"{remaining}%";
        quotaCaption.Text = $"已用 {(int)Math.Round(used)}%";
        if (limit.ResetsAt <= 0) return;

        long seconds = Math.Max(0, limit.ResetsAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        double windowMinutes = limit.WindowMinutes > 0 ? limit.WindowMinutes : fallbackWindowMinutes;
        ring.Value = Math.Clamp(100 * seconds / (windowMinutes * 60), 0, 100);
        resetValue.Text = FormatCountdown(seconds);
        resetCaption.Text = formatReset(DateTimeOffset.FromUnixTimeSeconds(limit.ResetsAt).LocalDateTime);
    }

    private static string FormatCountdown(long seconds)
    {
        if (seconds <= 0) return "即将";
        long days = seconds / 86400;
        long hours = seconds % 86400 / 3600;
        long minutes = seconds % 3600 / 60;
        if (days > 0) return $"{days}天 {hours}时";
        if (hours > 0) return $"{hours}时 {minutes}分";
        return $"{Math.Max(1, minutes)}分";
    }

    private void ApplyNextTheme()
    {
        _themeIndex = (_themeIndex + 1) % _themes.Length;
        LiquidGlassSurface panel = this.FindControl<LiquidGlassSurface>("GlassPanel")!;
        panel.TintColor = _themes[_themeIndex].Tint;
        panel.SurfaceColor = _themes[_themeIndex].Surface;
    }

    private void RefreshTaskDetails()
    {
        foreach (TaskRow row in Tasks) row.ShowTokens = _showTokens;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}

public sealed class TaskRow : INotifyPropertyChanged
{
    public TaskRow(string title, string detail, long tokens = 0)
    {
        Title = title;
        ModelDetail = detail;
        Tokens = tokens;
    }

    public string Title { get; }
    public string ModelDetail { get; }
    public long Tokens { get; }
    public string Detail => ShowTokens ? $"Σ {FormatTokens(Tokens)} Token" : ModelDetail;
    private bool _showTokens;
    public bool ShowTokens
    {
        get => _showTokens;
        set
        {
            if (_showTokens == value) return;
            _showTokens = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FormatTokens(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.00}B",
        >= 1_000_000 => $"{value / 1_000_000d:0.0}M",
        >= 1_000 => $"{value / 1_000d:0.0}K",
        _ => value.ToString("N0")
    };
}
