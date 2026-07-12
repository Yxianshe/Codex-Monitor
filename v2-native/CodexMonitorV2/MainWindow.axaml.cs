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
    private readonly DispatcherTimer _glassTimer;
    private readonly ConicGradientBrush _flowBorderBrush;
    private bool _dataBusy;
    private Bitmap? _sceneBitmap;
    private bool? _sceneIsDay;
    private bool _showTokens;
    private DateTime _lastQuotaRefresh = DateTime.MinValue;
    private bool? _manualIsDay;
    private IReadOnlyList<MonitorData.ModelDescriptor> _models = Array.Empty<MonitorData.ModelDescriptor>();
    private double _flowPhase;
    private bool _syncingDefaults;
    private int _defaultChangeVersion;
    private readonly SemaphoreSlim _defaultWriteGate = new(1, 1);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _manualIsDay = Environment.GetEnvironmentVariable("CODEX_MONITOR_SCENE")?.ToLowerInvariant() switch
        {
            "day" => true,
            "night" => false,
            _ => null
        };
        _flowBorderBrush = CreateFlowBorderBrush();
        foreach (string name in new[] { "TokenButton", "ThemeButton", "PinButton", "MinimizeButton", "CloseButton" })
            this.FindControl<Button>(name)!.BorderBrush = _flowBorderBrush;
        this.FindControl<ComboBox>("DefaultModelBox")!.SelectionChanged += (_, _) => DefaultModelChanged();
        this.FindControl<ComboBox>("DefaultEffortBox")!.SelectionChanged += (_, _) => ScheduleDefaultSettingsWrite();

        _dataTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _dataTimer.Tick += async (_, _) =>
        {
            ApplySceneBackground();
            await RefreshDataAsync();
        };
        _glassTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _glassTimer.Tick += (_, _) => TickGlassAnimation();
        Opened += async (_, _) =>
        {
            ApplyNativeWindowMaterial();
            ApplySceneBackground();
            Task refresh = RefreshDataAsync();
            Task<IReadOnlyList<MonitorData.ModelDescriptor>> models = MonitorData.ReadModelsAsync();
            await refresh;
            _models = await models;
            await InitializeDefaultSelectorsAsync();
            _dataTimer.Start();
            _glassTimer.Start();
            await CapturePreviewIfRequestedAsync();
        };
        Closed += (_, _) =>
        {
            _dataTimer.Stop();
            _glassTimer.Stop();
            _sceneBitmap?.Dispose();
        };

        WireWindowGrips();
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("MinimizeButton")!.Click += (_, _) => WindowState = WindowState.Minimized;
        this.FindControl<Button>("PinButton")!.Click += (_, _) => Topmost = !Topmost;
        this.FindControl<Button>("TokenButton")!.Click += (_, _) =>
        {
            _showTokens = !_showTokens;
            RefreshTaskDetails();
        };
        this.FindControl<Button>("ThemeButton")!.Click += (_, _) => ToggleScene();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private static ConicGradientBrush CreateFlowBorderBrush() => new()
    {
        Center = RelativePoint.Center,
        Angle = 0,
        GradientStops = new GradientStops
        {
            new(Color.Parse("#E6FFFFFF"), 0.00),
            new(Color.Parse("#8FCBF7FF"), 0.18),
            new(Color.Parse("#6BAE9BFF"), 0.42),
            new(Color.Parse("#96FFEBC2"), 0.66),
            new(Color.Parse("#E6FFFFFF"), 1.00)
        }
    };

    private void TickGlassAnimation()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        _flowPhase = (_flowPhase + 1.0 / 240.0) % 1.0;
        _flowBorderBrush.Angle = _flowPhase * 360.0;
        this.FindControl<LiquidGlassSurface>("GlassPanel")!.FlowPhase = _flowPhase;
    }

    private async Task CapturePreviewIfRequestedAsync()
    {
        string? path = Environment.GetEnvironmentVariable("CODEX_MONITOR_CAPTURE");
        if (string.IsNullOrWhiteSpace(path)) return;
        await Task.Delay(900);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using RenderTargetBitmap bitmap = new(PixelSize.FromSize(ClientSize, RenderScaling));
        bitmap.Render(this);
        bitmap.Save(path);
    }

    private async Task InitializeDefaultSelectorsAsync()
    {
        _syncingDefaults = true;
        ComboBox modelBox = this.FindControl<ComboBox>("DefaultModelBox")!;
        ComboBox effortBox = this.FindControl<ComboBox>("DefaultEffortBox")!;
        ModelChoice[] choices = _models.Select(ModelChoice.From).ToArray();
        modelBox.ItemsSource = choices;

        MonitorData.DefaultSettings settings = await MonitorData.ReadDefaultSettingsAsync();
        ModelChoice? selected = choices.FirstOrDefault(item => item.Id.Equals(settings.Model, StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault(item => item.IsDefault)
            ?? choices.FirstOrDefault();
        modelBox.SelectedItem = selected;
        RebuildDefaultEfforts(selected, settings.Effort);
        UpdateDefaultChoiceColors();
        _syncingDefaults = false;
    }

    private void DefaultModelChanged()
    {
        if (_syncingDefaults) return;
        _syncingDefaults = true;
        ModelChoice? model = this.FindControl<ComboBox>("DefaultModelBox")!.SelectedItem as ModelChoice;
        RebuildDefaultEfforts(model, model?.DefaultEffort ?? "");
        UpdateDefaultChoiceColors();
        _syncingDefaults = false;
        ScheduleDefaultSettingsWrite();
    }

    private void RebuildDefaultEfforts(ModelChoice? model, string preferred)
    {
        ComboBox effortBox = this.FindControl<ComboBox>("DefaultEffortBox")!;
        EffortChoice[] efforts = model?.Efforts.ToArray() ?? Array.Empty<EffortChoice>();
        effortBox.ItemsSource = efforts;
        effortBox.SelectedItem = efforts.FirstOrDefault(item => item.Id.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            ?? efforts.FirstOrDefault(item => item.Id.Equals(model?.DefaultEffort, StringComparison.OrdinalIgnoreCase))
            ?? efforts.FirstOrDefault();
    }

    private void UpdateDefaultChoiceColors()
    {
        ComboBox modelBox = this.FindControl<ComboBox>("DefaultModelBox")!;
        ComboBox effortBox = this.FindControl<ComboBox>("DefaultEffortBox")!;
        modelBox.Background = (modelBox.SelectedItem as ModelChoice)?.Background ?? Brushes.Transparent;
        effortBox.Background = (effortBox.SelectedItem as EffortChoice)?.Background ?? Brushes.Transparent;
    }

    private void ScheduleDefaultSettingsWrite()
    {
        if (_syncingDefaults) return;
        UpdateDefaultChoiceColors();
        int version = ++_defaultChangeVersion;
        _ = WriteDefaultSettingsAsync(version);
    }

    private async Task WriteDefaultSettingsAsync(int version)
    {
        await Task.Delay(300);
        await _defaultWriteGate.WaitAsync();
        try
        {
            if (version != _defaultChangeVersion) return;
            ModelChoice? model = this.FindControl<ComboBox>("DefaultModelBox")!.SelectedItem as ModelChoice;
            EffortChoice? effort = this.FindControl<ComboBox>("DefaultEffortBox")!.SelectedItem as EffortChoice;
            if (model is null || effort is null) return;
            MonitorData.SettingsUpdateResult result = await MonitorData.UpdateDefaultSettingsAsync(model.Id, effort.Id);
            if (version == _defaultChangeVersion)
                this.FindControl<TextBlock>("StatusText")!.Text = result.Message;
        }
        finally
        {
            _defaultWriteGate.Release();
        }
    }

    private void ApplyNativeWindowMaterial()
    {
        nint hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd == 0) return;
        int rounded = 2;
        DwmSetWindowAttribute(hwnd, 33, ref rounded, sizeof(int));
        int noSystemBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 34, ref noSystemBorder, sizeof(int));
    }

    private void WireWindowGrips()
    {
        this.FindControl<Border>("MoveGrip")!.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginNativeDrag(2);
        };
        WireResizeGrip("TopGrip", WindowEdge.North);
        WireResizeGrip("BottomGrip", WindowEdge.South);
        WireResizeGrip("LeftGrip", WindowEdge.West);
        WireResizeGrip("RightGrip", WindowEdge.East);
        WireResizeGrip("TopLeftGrip", WindowEdge.NorthWest);
        WireResizeGrip("TopRightGrip", WindowEdge.NorthEast);
        WireResizeGrip("BottomLeftGrip", WindowEdge.SouthWest);
        WireResizeGrip("BottomRightGrip", WindowEdge.SouthEast);
    }

    private void WireResizeGrip(string name, WindowEdge edge)
    {
        this.FindControl<Border>(name)!.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginNativeDrag(edge switch
                {
                    WindowEdge.West => 10,
                    WindowEdge.East => 11,
                    WindowEdge.North => 12,
                    WindowEdge.NorthWest => 13,
                    WindowEdge.NorthEast => 14,
                    WindowEdge.South => 15,
                    WindowEdge.SouthWest => 16,
                    _ => 17
                });
        };
    }

    private void BeginNativeDrag(int hitTest)
    {
        nint hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd == 0) return;
        ReleaseCapture();
        SendMessage(hwnd, 0x00A1, (nint)hitTest, 0);
    }

    private void ApplySceneBackground()
    {
        bool isDay = _manualIsDay ?? DateTime.Now.Hour is >= 7 and < 19;
        if (_sceneIsDay == isDay) return;

        Uri uri = new($"avares://CodexMonitorV2/Assets/{(isDay ? "sun" : "moon")}.png");
        Bitmap next = new(AssetLoader.Open(uri));
        Image backdrop = this.FindControl<Image>("BackdropImage")!;
        backdrop.Source = next;
        backdrop.RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative);
        backdrop.RenderTransform = new ScaleTransform(isDay ? 1.06 : 1.0, isDay ? 1.02 : 1.0);

        Color lensTint = Color.Parse(isDay ? "#140D0502" : "#0C020A18");
        this.FindControl<LiquidGlassSurface>("TaskLens")!.SurfaceColor = lensTint;
        this.FindControl<LiquidGlassSurface>("UsageLens")!.SurfaceColor = lensTint;
        this.FindControl<Border>("ForegroundLayer")!.Background = new SolidColorBrush(
            Color.Parse(isDay ? "#08080302" : "#04020710"));
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
            HashSet<string> activeIds = snapshots.Select(item => item.ThreadId).ToHashSet(StringComparer.Ordinal);
            for (int i = Tasks.Count - 1; i >= 0; i--)
                if (!activeIds.Contains(Tasks[i].ThreadId)) Tasks.RemoveAt(i);

            foreach (MonitorData.TaskSnapshot item in snapshots)
            {
                TaskRow? row = Tasks.FirstOrDefault(existing => existing.ThreadId == item.ThreadId);
                if (row is null)
                {
                    row = new TaskRow(item) { ShowTokens = _showTokens };
                    Tasks.Add(row);
                }
                else
                {
                    row.Update(item);
                    row.ShowTokens = _showTokens;
                }
            }

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
            quota.FiveHour,
            300,
            this.FindControl<ProgressBar>("FiveQuotaBar")!,
            this.FindControl<TextBlock>("FiveQuotaValue")!,
            this.FindControl<TextBlock>("FiveQuotaCaption")!,
            this.FindControl<RingGauge>("FiveResetRing")!,
            this.FindControl<TextBlock>("FiveResetValue")!,
            this.FindControl<TextBlock>("FiveResetCaption")!,
            reset => $"{reset:M月d日 HH:mm}");

        UpdateQuotaRow(
            quota.Weekly,
            10080,
            this.FindControl<ProgressBar>("WeekQuotaBar")!,
            this.FindControl<TextBlock>("WeekQuotaValue")!,
            this.FindControl<TextBlock>("WeekQuotaCaption")!,
            this.FindControl<RingGauge>("WeekResetRing")!,
            this.FindControl<TextBlock>("WeekResetValue")!,
            this.FindControl<TextBlock>("WeekResetCaption")!,
            reset => $"{reset:M月d日 HH:mm}");
    }

    private static void UpdateQuotaRow(
        MonitorData.WindowLimit? limit,
        double fallbackWindowMinutes,
        ProgressBar bar,
        TextBlock valueText,
        TextBlock quotaCaption,
        RingGauge ring,
        TextBlock resetValue,
        TextBlock resetCaption,
        Func<DateTime, string> formatReset)
    {
        if (limit is null)
        {
            bar.Value = 0;
            valueText.Text = "--%";
            quotaCaption.Text = "当前账户未提供";
            ring.Value = 0;
            resetValue.Text = "--";
            resetCaption.Text = "暂无日期";
            return;
        }

        double used = Math.Clamp(limit.UsedPercent, 0, 100);
        int remaining = (int)Math.Round(100 - used);
        bar.Value = remaining;
        valueText.Text = $"{remaining}%";
        quotaCaption.Text = $"已用 {(int)Math.Round(used)}%";
        if (limit.ResetsAt <= 0)
        {
            ring.Value = 0;
            resetValue.Text = "--";
            resetCaption.Text = "暂无日期";
            return;
        }

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

    private void ToggleScene()
    {
        bool current = _sceneIsDay ?? DateTime.Now.Hour is >= 7 and < 19;
        _manualIsDay = !current;
        _sceneIsDay = null;
        ApplySceneBackground();
    }

    private void RefreshTaskDetails()
    {
        foreach (TaskRow row in Tasks) row.ShowTokens = _showTokens;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}

public sealed class TaskRow : INotifyPropertyChanged
{
    private long _tokens;

    internal TaskRow(MonitorData.TaskSnapshot snapshot)
    {
        ThreadId = snapshot.ThreadId;
        Update(snapshot);
    }

    public string ThreadId { get; }
    public string Title { get; private set; } = "";
    public string ModelDisplay { get; private set; } = "--";
    public string EffortDisplay { get; private set; } = "--";
    public IBrush ModelAccent { get; private set; } = Brushes.Transparent;
    public IBrush ModelBackground { get; private set; } = Brushes.Transparent;
    public IBrush EffortAccent { get; private set; } = Brushes.Transparent;
    public IBrush EffortBackground { get; private set; } = Brushes.Transparent;
    public string TokenDetail => $"Σ {FormatTokens(_tokens)} Token";
    public bool TokenVisible => ShowTokens;
    public bool SettingsVisible => !ShowTokens;
    public string CurrentSummary { get; private set; } = "";

    private bool _showTokens;
    public bool ShowTokens
    {
        get => _showTokens;
        set
        {
            if (_showTokens == value) return;
            _showTokens = value;
            OnChanged(nameof(TokenVisible));
            OnChanged(nameof(SettingsVisible));
        }
    }

    internal void Update(MonitorData.TaskSnapshot snapshot)
    {
        if (Title != snapshot.Title)
        {
            Title = snapshot.Title;
            OnChanged(nameof(Title));
        }
        _tokens = snapshot.Tokens;
        OnChanged(nameof(TokenDetail));

        ModelChoice model = ModelChoice.Fallback(snapshot.CurrentModel);
        EffortChoice effort = EffortChoice.From(snapshot.CurrentEffort);
        ModelDisplay = model.DisplayName;
        EffortDisplay = effort.DisplayName;
        ModelAccent = model.Accent;
        ModelBackground = model.Background;
        EffortAccent = effort.Accent;
        EffortBackground = effort.Background;
        CurrentSummary = $"当前回合：{ModelDisplay} · {EffortDisplay}{(snapshot.Tier == "priority" ? " · Priority" : "")}";
        OnChanged(nameof(ModelDisplay));
        OnChanged(nameof(EffortDisplay));
        OnChanged(nameof(ModelAccent));
        OnChanged(nameof(ModelBackground));
        OnChanged(nameof(EffortAccent));
        OnChanged(nameof(EffortBackground));
        OnChanged(nameof(CurrentSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private static string FormatTokens(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.00}B",
        >= 1_000_000 => $"{value / 1_000_000d:0.0}M",
        >= 1_000 => $"{value / 1_000d:0.0}K",
        _ => value.ToString("N0")
    };

}

public sealed class ModelChoice
{
    private ModelChoice(
        string id,
        string displayName,
        string defaultEffort,
        IReadOnlyList<EffortChoice> efforts,
        bool isDefault)
    {
        Id = id;
        DisplayName = displayName;
        DefaultEffort = defaultEffort;
        Efforts = efforts;
        IsDefault = isDefault;
        Accent = new SolidColorBrush(ModelAccent(id));
        Color color = ModelAccent(id);
        Background = new SolidColorBrush(Color.FromArgb(54, color.R, color.G, color.B));
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string DefaultEffort { get; }
    public IReadOnlyList<EffortChoice> Efforts { get; }
    public bool IsDefault { get; }
    public IBrush Accent { get; }
    public IBrush Background { get; }

    internal static ModelChoice From(MonitorData.ModelDescriptor model) => new(
        model.Id,
        model.DisplayName,
        model.DefaultEffort,
        model.Efforts.Select(item => EffortChoice.From(item.Id)).ToArray(),
        model.IsDefault);

    internal static ModelChoice Fallback(string model) => new(
        model,
        PrettyName(model),
        "medium",
        new[] { "low", "medium", "high", "xhigh", "max", "ultra" }.Select(EffortChoice.From).ToArray(),
        false);

    private static string PrettyName(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "--";
        string value = model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ? model[4..] : model;
        return string.Join(' ', value.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(part =>
            part.Length > 0 && char.IsLetter(part[0])
                ? char.ToUpperInvariant(part[0]) + part[1..]
                : part));
    }

    private static Color ModelAccent(string model)
    {
        string value = model.ToLowerInvariant();
        if (value.Contains("sol")) return Color.Parse("#6EA8FF");
        if (value.Contains("terra")) return Color.Parse("#58C7A7");
        if (value.Contains("luna")) return Color.Parse("#A78BFA");
        if (value.Contains("spark")) return Color.Parse("#FFB45F");
        if (value.Contains("mini")) return Color.Parse("#66D4E8");
        Color[] palette =
        {
            Color.Parse("#7AA7FF"), Color.Parse("#64D2B1"), Color.Parse("#B38CFF"),
            Color.Parse("#FF8FB7"), Color.Parse("#F2C66D")
        };
        int hash = value.Aggregate(17, (current, character) => unchecked(current * 31 + character));
        return palette[(hash & int.MaxValue) % palette.Length];
    }
}

public sealed class EffortChoice
{
    private EffortChoice(string id, string displayName, Color accent)
    {
        Id = id;
        DisplayName = displayName;
        Accent = new SolidColorBrush(accent);
        Background = new SolidColorBrush(Color.FromArgb(58, accent.R, accent.G, accent.B));
    }

    public string Id { get; }
    public string DisplayName { get; }
    public IBrush Accent { get; }
    public IBrush Background { get; }

    internal static EffortChoice From(string effort) => effort.ToLowerInvariant() switch
    {
        "minimal" => new("minimal", "最低", Color.Parse("#9AA6B6")),
        "low" => new("low", "低", Color.Parse("#66AFFF")),
        "medium" => new("medium", "中", Color.Parse("#58C7A7")),
        "high" => new("high", "高", Color.Parse("#F2B55F")),
        "xhigh" => new("xhigh", "极高", Color.Parse("#F08B68")),
        "max" => new("max", "最高", Color.Parse("#D878E8")),
        "ultra" => new("ultra", "超高", Color.Parse("#9B8CFF")),
        _ => new(effort, string.IsNullOrWhiteSpace(effort) ? "--" : effort, Color.Parse("#AAB6C8"))
    };
}
