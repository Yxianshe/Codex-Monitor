using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
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
    private readonly ConicGradientBrush _flowBorderBrush;
    private bool _dataBusy;
    private Bitmap? _sceneBitmap;
    private bool? _sceneIsDay;
    private bool _showTokens;
    private bool _isEnglish = true;
    private DateTime _lastQuotaRefresh = DateTime.MinValue;
    private MonitorData.QuotaSnapshot? _lastQuota;
    private bool? _manualIsDay;
    private IReadOnlyList<MonitorData.ModelDescriptor> _models = Array.Empty<MonitorData.ModelDescriptor>();
    private CompositionVisual? _sceneVisual;
    private readonly bool _animationsEnabled;
    private bool _syncingDefaults;
    private int _defaultChangeVersion;
    private readonly SemaphoreSlim _defaultWriteGate = new(1, 1);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        LiquidGlassBackdrop.SetIsLive(this, false);
        _animationsEnabled = Environment.GetEnvironmentVariable("CODEX_MONITOR_REDUCE_MOTION") != "1"
            && ClientAreaAnimationsEnabled();
        _isEnglish = !string.Equals(
            Environment.GetEnvironmentVariable("CODEX_MONITOR_LANGUAGE"),
            "zh",
            StringComparison.OrdinalIgnoreCase);
        ApplyLanguage();
        _manualIsDay = Environment.GetEnvironmentVariable("CODEX_MONITOR_SCENE")?.ToLowerInvariant() switch
        {
            "day" => true,
            "night" => false,
            _ => null
        };
        _flowBorderBrush = CreateFlowBorderBrush();
        Image backdrop = this.FindControl<Image>("BackdropImage")!;
        backdrop.SizeChanged += (_, _) => UpdateSceneCenterPoint();
        foreach (string name in new[] { "TokenButton", "ThemeButton", "LanguageButton", "PinButton", "MinimizeButton", "CloseButton" })
            this.FindControl<Button>(name)!.BorderBrush = _flowBorderBrush;
        this.FindControl<ListBox>("TaskList")!.SelectionChanged += (_, _) => SelectedTaskChanged();
        this.FindControl<ComboBox>("DefaultModelBox")!.SelectionChanged += (_, _) => DefaultModelChanged();
        this.FindControl<ComboBox>("DefaultEffortBox")!.SelectionChanged += (_, _) => ScheduleDefaultSettingsWrite();

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
            Task refresh = RefreshDataAsync();
            Task<IReadOnlyList<MonitorData.ModelDescriptor>> models = MonitorData.ReadModelsAsync();
            await refresh;
            _models = await models;
            await InitializeDefaultSelectorsAsync();
            _dataTimer.Start();
            StartSceneCompositionAnimation();
            await CapturePreviewIfRequestedAsync();
        };
        Closed += (_, _) =>
        {
            _dataTimer.Stop();
            StopSceneCompositionAnimation();
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
        this.FindControl<Button>("LanguageButton")!.Click += (_, _) => ToggleLanguage();
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

    private void StartSceneCompositionAnimation()
    {
        Image backdrop = this.FindControl<Image>("BackdropImage")!;
        _sceneVisual ??= ElementComposition.GetElementVisual(backdrop);
        if (_sceneVisual is null) return;

        StopSceneCompositionAnimation();
        UpdateSceneCenterPoint();

        bool isDay = _sceneIsDay ?? DateTime.Now.Hour is >= 7 and < 19;
        SceneMotionFrame rest = SampleSceneMotion(isDay, 0);
        _sceneVisual.Scale = new Vector3D(rest.ScaleX, rest.ScaleY, 1);
        _sceneVisual.Offset = new Vector3D(rest.X, rest.Y, 0);
        _sceneVisual.RotationAngle = (float)rest.Rotation;
        if (!_animationsEnabled || WindowState == WindowState.Minimized) return;

        TimeSpan duration = TimeSpan.FromSeconds(isDay ? 36 : 44);
        Vector3DKeyFrameAnimation scale = _sceneVisual.Compositor.CreateVector3DKeyFrameAnimation();
        Vector3DKeyFrameAnimation offset = _sceneVisual.Compositor.CreateVector3DKeyFrameAnimation();
        ScalarKeyFrameAnimation rotation = _sceneVisual.Compositor.CreateScalarKeyFrameAnimation();
        scale.Duration = offset.Duration = rotation.Duration = duration;
        scale.IterationBehavior = offset.IterationBehavior = rotation.IterationBehavior = AnimationIterationBehavior.Forever;

        const int steps = 64;
        for (int i = 0; i <= steps; i++)
        {
            float progress = i / (float)steps;
            SceneMotionFrame frame = SampleSceneMotion(isDay, progress);
            scale.InsertKeyFrame(progress, new Vector3D(frame.ScaleX, frame.ScaleY, 1));
            offset.InsertKeyFrame(progress, new Vector3D(frame.X, frame.Y, 0));
            rotation.InsertKeyFrame(progress, (float)frame.Rotation);
        }

        _sceneVisual.StartAnimation("Scale", scale);
        _sceneVisual.StartAnimation("Offset", offset);
        _sceneVisual.StartAnimation("RotationAngle", rotation);
    }

    private void StopSceneCompositionAnimation()
    {
        _sceneVisual?.StopAnimation("Scale");
        _sceneVisual?.StopAnimation("Offset");
        _sceneVisual?.StopAnimation("RotationAngle");
    }

    private void UpdateSceneCenterPoint()
    {
        if (_sceneVisual is null) return;
        Image backdrop = this.FindControl<Image>("BackdropImage")!;
        _sceneVisual.CenterPoint = new Vector3D(backdrop.Bounds.Width * 0.12, backdrop.Bounds.Height * 0.5, 0);
    }

    private static SceneMotionFrame SampleSceneMotion(bool isDay, double phase)
    {
        double angle = phase * Math.PI * 2;
        double second = angle * 2;
        double baseScaleX = isDay ? 1.075 : 1.050;
        double baseScaleY = isDay ? 1.045 : 1.038;
        double scaleX = baseScaleX + Math.Sin(angle - 0.40) * (isDay ? 0.010 : 0.008)
            + Math.Sin(second + 0.20) * 0.0025;
        double scaleY = baseScaleY + Math.Sin(angle - 0.15) * (isDay ? 0.008 : 0.006)
            + Math.Sin(second - 0.35) * 0.0020;
        double x = Math.Sin(angle) * (isDay ? 5.5 : 4.2) + Math.Sin(second + 0.40) * 1.1;
        double y = Math.Cos(angle) * (isDay ? 3.2 : 2.6) + Math.Sin(second - 0.60) * 0.7;
        double rotation = Math.Sin(angle + 0.70) * (isDay ? 0.0035 : 0.0026);
        return new SceneMotionFrame(scaleX, scaleY, x, y, rotation);
    }

    internal static bool SceneMotionSelfCheck()
    {
        foreach (bool isDay in new[] { false, true })
        {
            SceneMotionFrame first = SampleSceneMotion(isDay, 0);
            SceneMotionFrame last = SampleSceneMotion(isDay, 1);
            if (Math.Abs(first.ScaleX - last.ScaleX) > 0.000001
                || Math.Abs(first.ScaleY - last.ScaleY) > 0.000001
                || Math.Abs(first.X - last.X) > 0.000001
                || Math.Abs(first.Y - last.Y) > 0.000001
                || Math.Abs(first.Rotation - last.Rotation) > 0.000001)
                return false;
        }
        return true;
    }

    private readonly record struct SceneMotionFrame(
        double ScaleX,
        double ScaleY,
        double X,
        double Y,
        double Rotation);

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
        if (this.FindControl<ListBox>("TaskList")!.SelectedItem is TaskRow selected)
        {
            PopulateDefaultSelectors(selected.SelectionModel, selected.SelectionEffort);
            ApplyLanguage();
            return;
        }
        MonitorData.DefaultSettings settings = await MonitorData.ReadDefaultSettingsAsync();
        PopulateDefaultSelectors(settings.Model, settings.Effort);
    }

    private void SelectedTaskChanged()
    {
        if (_models.Count == 0) return;
        if (this.FindControl<ListBox>("TaskList")!.SelectedItem is not TaskRow selected) return;
        PopulateDefaultSelectors(selected.SelectionModel, selected.SelectionEffort);
        ApplyLanguage();
    }

    private void PopulateDefaultSelectors(string modelId, string effortId)
    {
        _syncingDefaults = true;
        ComboBox modelBox = this.FindControl<ComboBox>("DefaultModelBox")!;
        List<ModelChoice> choices = _models.Select(model => ModelChoice.From(model, _isEnglish)).ToList();
        ModelChoice? selected = choices.FirstOrDefault(item => item.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (selected is null && !string.IsNullOrWhiteSpace(modelId))
        {
            selected = ModelChoice.Fallback(modelId, _isEnglish);
            choices.Insert(0, selected);
        }
        selected ??= choices.FirstOrDefault(item => item.IsDefault) ?? choices.FirstOrDefault();
        modelBox.ItemsSource = choices;
        modelBox.SelectedItem = selected;
        RebuildDefaultEfforts(selected, effortId);
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
        List<EffortChoice> efforts = model?.Efforts.ToList() ?? new List<EffortChoice>();
        if (!string.IsNullOrWhiteSpace(preferred)
            && efforts.All(item => !item.Id.Equals(preferred, StringComparison.OrdinalIgnoreCase)))
            efforts.Insert(0, EffortChoice.From(preferred, _isEnglish));
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
            TaskRow? selected = this.FindControl<ListBox>("TaskList")!.SelectedItem as TaskRow;
            if (result.Success) selected?.SetNextSettings(model.Id, effort.Id);
            if (version == _defaultChangeVersion)
                this.FindControl<TextBlock>("StatusText")!.Text = result.Success
                    ? selected is null
                        ? T("Defaults saved", "默认设置已保存")
                        : T("Next-turn choice saved", "下回合选择已保存")
                    : $"{T("Save failed", "保存失败")}: {result.Message}";
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

        Color lensTint = Color.Parse(isDay ? "#140D0502" : "#0C020A18");
        this.FindControl<LiquidGlassSurface>("TaskLens")!.SurfaceColor = lensTint;
        this.FindControl<LiquidGlassSurface>("UsageLens")!.SurfaceColor = lensTint;
        this.FindControl<Border>("ForegroundLayer")!.Background = new SolidColorBrush(
            Color.Parse(isDay ? "#08080302" : "#04020710"));
        Bitmap? old = _sceneBitmap;
        _sceneBitmap = next;
        _sceneIsDay = isDay;
        old?.Dispose();
        StartSceneCompositionAnimation();
        Dispatcher.UIThread.Post(() => LiquidGlassBackdrop.Refresh(backdrop), DispatcherPriority.Render);
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
                    row = new TaskRow(item, _isEnglish) { ShowTokens = _showTokens };
                    Tasks.Add(row);
                }
                else
                {
                    row.Update(item);
                    row.ShowTokens = _showTokens;
                }
            }

            ListBox taskList = this.FindControl<ListBox>("TaskList")!;
            if (taskList.SelectedItem is not TaskRow selected || !Tasks.Contains(selected))
                taskList.SelectedItem = Tasks.FirstOrDefault();

            UpdateStatusText();
            this.FindControl<TextBlock>("UpdatedText")!.Text = $"{T("Updated", "更新")}：{DateTime.Now:HH:mm:ss}";

            if (DateTime.Now - _lastQuotaRefresh >= TimeSpan.FromSeconds(30))
            {
                MonitorData.QuotaSnapshot? quota = await MonitorData.ReadQuotaAsync();
                if (quota is not null)
                {
                    _lastQuota = quota;
                    UpdateQuota(quota);
                }
                _lastQuotaRefresh = DateTime.Now;
            }
        }
        catch (Exception error)
        {
            this.FindControl<TextBlock>("StatusText")!.Text = T("Read failed", "读取失败");
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
            reset => _isEnglish ? $"{reset:MMM d HH:mm}" : $"{reset:M月d日 HH:mm}");

        UpdateQuotaRow(
            quota.Weekly,
            10080,
            this.FindControl<ProgressBar>("WeekQuotaBar")!,
            this.FindControl<TextBlock>("WeekQuotaValue")!,
            this.FindControl<TextBlock>("WeekQuotaCaption")!,
            this.FindControl<RingGauge>("WeekResetRing")!,
            this.FindControl<TextBlock>("WeekResetValue")!,
            this.FindControl<TextBlock>("WeekResetCaption")!,
            reset => _isEnglish ? $"{reset:MMM d HH:mm}" : $"{reset:M月d日 HH:mm}");
    }

    private void UpdateQuotaRow(
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
            quotaCaption.Text = T("Not available", "当前账户未提供");
            ring.Value = 0;
            resetValue.Text = "--";
            resetCaption.Text = T("No date", "暂无日期");
            return;
        }

        double used = Math.Clamp(limit.UsedPercent, 0, 100);
        int remaining = (int)Math.Round(100 - used);
        bar.Value = remaining;
        valueText.Text = $"{remaining}%";
        quotaCaption.Text = $"{T("Used", "已用")} {(int)Math.Round(used)}%";
        if (limit.ResetsAt <= 0)
        {
            ring.Value = 0;
            resetValue.Text = "--";
            resetCaption.Text = T("No date", "暂无日期");
            return;
        }

        long seconds = Math.Max(0, limit.ResetsAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        double windowMinutes = limit.WindowMinutes > 0 ? limit.WindowMinutes : fallbackWindowMinutes;
        ring.Value = Math.Clamp(100 * seconds / (windowMinutes * 60), 0, 100);
        resetValue.Text = FormatCountdown(seconds);
        resetCaption.Text = formatReset(DateTimeOffset.FromUnixTimeSeconds(limit.ResetsAt).LocalDateTime);
    }

    private string FormatCountdown(long seconds)
    {
        if (seconds <= 0) return T("Soon", "即将");
        long days = seconds / 86400;
        long hours = seconds % 86400 / 3600;
        long minutes = seconds % 3600 / 60;
        if (days > 0) return _isEnglish ? $"{days}d {hours}h" : $"{days}天 {hours}时";
        if (hours > 0) return _isEnglish ? $"{hours}h {minutes}m" : $"{hours}时 {minutes}分";
        return _isEnglish ? $"{Math.Max(1, minutes)}m" : $"{Math.Max(1, minutes)}分";
    }

    private void ToggleLanguage()
    {
        string modelId = (this.FindControl<ComboBox>("DefaultModelBox")!.SelectedItem as ModelChoice)?.Id ?? "";
        string effortId = (this.FindControl<ComboBox>("DefaultEffortBox")!.SelectedItem as EffortChoice)?.Id ?? "";
        _isEnglish = !_isEnglish;
        ApplyLanguage();
        PopulateDefaultSelectors(modelId, effortId);
        foreach (TaskRow row in Tasks) row.SetLanguage(_isEnglish);
        UpdateStatusText();
        if (_lastQuota is not null) UpdateQuota(_lastQuota);
    }

    private void ApplyLanguage()
    {
        this.FindControl<TextBlock>("MainTitleText")!.Text = T("Tasks & Usage", "任务与使用量");
        this.FindControl<TextBlock>("ActiveTasksLabel")!.Text = T("Active tasks", "正在调用的任务");
        bool hasSelectedTask = this.FindControl<ListBox>("TaskList")!.SelectedItem is TaskRow;
        this.FindControl<TextBlock>("DefaultLabel")!.Text = hasSelectedTask ? T("Next", "下回合") : T("New", "新建");
        this.FindControl<TextBlock>("UsageTitleText")!.Text = T("Usage", "使用量概览");
        this.FindControl<TextBlock>("FiveTitleText")!.Text = T("5 hours", "5 小时");
        this.FindControl<TextBlock>("FiveWindowText")!.Text = T("Limit window", "限额周期");
        this.FindControl<TextBlock>("FiveRemainingText")!.Text = T("Remaining", "剩余额度");
        this.FindControl<TextBlock>("FiveResetTitleText")!.Text = T("Reset", "重置");
        this.FindControl<TextBlock>("FiveNextDateText")!.Text = T("Next date", "下次日期");
        this.FindControl<TextBlock>("WeekTitleText")!.Text = T("Weekly", "每周");
        this.FindControl<TextBlock>("WeekWindowText")!.Text = T("Limit window", "限额周期");
        this.FindControl<TextBlock>("WeekRemainingText")!.Text = T("Remaining", "剩余额度");
        this.FindControl<TextBlock>("WeekResetTitleText")!.Text = T("Reset", "重置");
        this.FindControl<TextBlock>("WeekNextDateText")!.Text = T("Next date", "下次日期");
        this.FindControl<TextBlock>("LanguageButtonText")!.Text = _isEnglish ? "中" : "EN";
        ToolTip.SetTip(this.FindControl<StackPanel>("DefaultSettingsPanel")!,
            hasSelectedTask
                ? T("Select a task, then set the global model and reasoning preference used by the next turn; the active turn remains unchanged",
                    "先选择任务，再设置下回合使用的全局模型与推理强度；正在生成的回合不会改变")
                : T("Set defaults for new Codex tasks; active turns remain unchanged",
                    "设置 Codex 新任务默认值；正在生成的回合不会改变"));
        ToolTip.SetTip(this.FindControl<Button>("LanguageButton")!,
            T("Switch to Chinese", "切换到英文"));
    }

    private void UpdateStatusText() => this.FindControl<TextBlock>("StatusText")!.Text = Tasks.Count == 0
        ? T("No active tasks", "暂无正在调用的任务")
        : _isEnglish ? $"{Tasks.Count} active" : $"正在调用 {Tasks.Count} 个任务";

    private string T(string english, string chinese) => _isEnglish ? english : chinese;

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
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref bool value, uint flags);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    private static bool ClientAreaAnimationsEnabled()
    {
        bool enabled = true;
        return !SystemParametersInfo(0x1042, 0, ref enabled, 0) || enabled;
    }
}

public sealed class TaskRow : INotifyPropertyChanged
{
    private long _tokens;
    private bool _isEnglish;
    private string _currentModel = "";
    private string _currentEffort = "";
    private string _nextModel = "";
    private string _nextEffort = "";
    private string _tier = "default";

    internal TaskRow(MonitorData.TaskSnapshot snapshot, bool isEnglish)
    {
        _isEnglish = isEnglish;
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
    public string CurrentLabel { get; private set; } = "Current";
    public string CurrentSummary { get; private set; } = "";
    public string SelectionModel => _nextModel.Length > 0 ? _nextModel : _currentModel;
    public string SelectionEffort => _nextEffort.Length > 0 ? _nextEffort : _currentEffort;

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
        _currentModel = snapshot.CurrentModel;
        _currentEffort = snapshot.CurrentEffort;
        _tier = snapshot.Tier;
        if (_nextModel.Equals(_currentModel, StringComparison.OrdinalIgnoreCase)
            && _nextEffort.Equals(_currentEffort, StringComparison.OrdinalIgnoreCase))
        {
            _nextModel = "";
            _nextEffort = "";
        }
        ApplyLocalizedValues();
    }

    internal void SetLanguage(bool isEnglish)
    {
        if (_isEnglish == isEnglish) return;
        _isEnglish = isEnglish;
        ApplyLocalizedValues();
    }

    internal void SetNextSettings(string model, string effort)
    {
        _nextModel = model;
        _nextEffort = effort;
        ApplyLocalizedValues();
    }

    private void ApplyLocalizedValues()
    {
        ModelChoice model = ModelChoice.Fallback(_currentModel, _isEnglish);
        EffortChoice effort = EffortChoice.From(_currentEffort, _isEnglish);
        ModelDisplay = model.DisplayName;
        EffortDisplay = effort.DisplayName;
        ModelAccent = model.Accent;
        ModelBackground = model.Background;
        EffortAccent = effort.Accent;
        EffortBackground = effort.Background;
        CurrentLabel = _isEnglish ? "Current" : "当前";
        CurrentSummary = _isEnglish
            ? $"Current turn: {ModelDisplay} · {EffortDisplay}{(_tier == "priority" ? " · Priority" : "")}" +
              (_nextModel.Length > 0 ? $"\nNext preference: {ModelChoice.Fallback(_nextModel, true).DisplayName} · {EffortChoice.From(_nextEffort, true).DisplayName}" : "")
            : $"当前回合：{ModelDisplay} · {EffortDisplay}{(_tier == "priority" ? " · Priority" : "")}" +
              (_nextModel.Length > 0 ? $"\n下回合偏好：{ModelChoice.Fallback(_nextModel, false).DisplayName} · {EffortChoice.From(_nextEffort, false).DisplayName}" : "");
        OnChanged(nameof(ModelDisplay));
        OnChanged(nameof(EffortDisplay));
        OnChanged(nameof(ModelAccent));
        OnChanged(nameof(ModelBackground));
        OnChanged(nameof(EffortAccent));
        OnChanged(nameof(EffortBackground));
        OnChanged(nameof(CurrentLabel));
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
        Background = new SolidColorBrush(Color.FromArgb(76, color.R, color.G, color.B));
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string DefaultEffort { get; }
    public IReadOnlyList<EffortChoice> Efforts { get; }
    public bool IsDefault { get; }
    public IBrush Accent { get; }
    public IBrush Background { get; }

    internal static ModelChoice From(MonitorData.ModelDescriptor model, bool isEnglish) => new(
        model.Id,
        model.DisplayName,
        model.DefaultEffort,
        model.Efforts.Select(item => EffortChoice.From(item.Id, isEnglish)).ToArray(),
        model.IsDefault);

    internal static ModelChoice Fallback(string model, bool isEnglish) => new(
        model,
        PrettyName(model),
        "medium",
        new[] { "low", "medium", "high", "xhigh", "max", "ultra" }
            .Select(item => EffortChoice.From(item, isEnglish)).ToArray(),
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
        Background = new SolidColorBrush(Color.FromArgb(78, accent.R, accent.G, accent.B));
    }

    public string Id { get; }
    public string DisplayName { get; }
    public IBrush Accent { get; }
    public IBrush Background { get; }

    internal static EffortChoice From(string effort, bool isEnglish) => effort.ToLowerInvariant() switch
    {
        "minimal" => new("minimal", isEnglish ? "Minimal" : "最低", Color.Parse("#9AA6B6")),
        "low" => new("low", isEnglish ? "Low" : "低", Color.Parse("#66AFFF")),
        "medium" => new("medium", isEnglish ? "Medium" : "中", Color.Parse("#58C7A7")),
        "high" => new("high", isEnglish ? "High" : "高", Color.Parse("#F2B55F")),
        "xhigh" => new("xhigh", isEnglish ? "X-High" : "极高", Color.Parse("#F08B68")),
        "max" => new("max", isEnglish ? "Max" : "最高", Color.Parse("#D878E8")),
        "ultra" => new("ultra", isEnglish ? "Ultra" : "超高", Color.Parse("#9B8CFF")),
        _ => new(effort, string.IsNullOrWhiteSpace(effort) ? "--" : effort, Color.Parse("#AAB6C8"))
    };
}
