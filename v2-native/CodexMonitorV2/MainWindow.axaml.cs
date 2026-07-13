using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
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
    private bool _syncingDefaults;
    private int _defaultChangeVersion;
    private readonly SemaphoreSlim _defaultWriteGate = new(1, 1);
    private readonly SceneSettingsStore _sceneSettings = SceneSettingsStore.Load();
    private bool _syncingSceneControls;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        LiquidGlassBackdrop.SetIsLive(this, false);
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
        SceneBackdrop backdrop = this.FindControl<SceneBackdrop>("BackdropImage")!;
        backdrop.SizeChanged += (_, _) =>
        {
            ApplySceneLayoutTransform();
            RefreshGlassBackdrop();
        };
        foreach (string name in new[] { "SettingsButton", "TokenButton", "ThemeButton", "LanguageButton", "PinButton", "MinimizeButton", "CloseButton", "SettingsCloseButton" })
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
            await CapturePreviewIfRequestedAsync();
        };
        Closed += (_, _) =>
        {
            _dataTimer.Stop();
            _sceneBitmap?.Dispose();
        };

        WireWindowGrips();
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("MinimizeButton")!.Click += (_, _) => WindowState = WindowState.Minimized;
        this.FindControl<Button>("PinButton")!.Click += (_, _) => TogglePin();
        this.FindControl<Button>("TokenButton")!.Click += (_, _) =>
        {
            _showTokens = !_showTokens;
            RefreshTaskDetails();
        };
        this.FindControl<Button>("ThemeButton")!.Click += (_, _) => ToggleScene();
        this.FindControl<Button>("LanguageButton")!.Click += (_, _) => ToggleLanguage();
        this.FindControl<Button>("SettingsButton")!.Click += (_, _) => ToggleSceneSettings(true);
        this.FindControl<Button>("SettingsCloseButton")!.Click += (_, _) => ToggleSceneSettings(false);
        this.FindControl<Button>("ChooseSceneImageButton")!.Click += async (_, _) => await ChooseSceneImageAsync();
        this.FindControl<Button>("ResetSceneImageButton")!.Click += (_, _) => ResetCurrentSceneSettings();
        this.FindControl<Button>("SaveSceneSettingsButton")!.Click += (_, _) => SaveSceneSettings();
        this.FindControl<Slider>("SceneZoomSlider")!.ValueChanged += (_, _) => SceneControlsChanged();
        this.FindControl<Slider>("ScenePositionXSlider")!.ValueChanged += (_, _) => SceneControlsChanged();
        this.FindControl<Slider>("ScenePositionYSlider")!.ValueChanged += (_, _) => SceneControlsChanged();
        UpdatePinVisual();
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

    private void ApplySceneLayoutTransform()
    {
        SceneBackdrop backdrop = this.FindControl<SceneBackdrop>("BackdropImage")!;
        bool isDay = _sceneIsDay ?? DateTime.Now.Hour is >= 7 and < 19;
        SceneViewSettings settings = GetSceneSettings(isDay);
        backdrop.Zoom = settings.Zoom;
        backdrop.PositionX = settings.PositionX;
        backdrop.PositionY = settings.PositionY;
    }

    private void RefreshGlassBackdrop()
    {
        SceneBackdrop backdrop = this.FindControl<SceneBackdrop>("BackdropImage")!;
        Dispatcher.UIThread.Post(
            () => LiquidGlassBackdrop.Refresh(backdrop),
            DispatcherPriority.Render);
    }

    private async Task CapturePreviewIfRequestedAsync()
    {
        string? path = Environment.GetEnvironmentVariable("CODEX_MONITOR_CAPTURE");
        if (string.IsNullOrWhiteSpace(path)) return;

        if (Environment.GetEnvironmentVariable("CODEX_MONITOR_CAPTURE_DEMO") == "1")
            ApplyDemoPreviewData();
        if (string.Equals(
                Environment.GetEnvironmentVariable("CODEX_MONITOR_CAPTURE_VIEW"),
                "settings",
                StringComparison.OrdinalIgnoreCase))
            ToggleSceneSettings(true);

        await Task.Delay(900);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using RenderTargetBitmap bitmap = new(PixelSize.FromSize(ClientSize, RenderScaling));
        bitmap.Render(this);
        bitmap.Save(path);
    }

    private void ApplyDemoPreviewData()
    {
        Tasks.Clear();
        MonitorData.TaskSnapshot[] demoTasks =
        {
            new("demo-1", "Design liquid-glass dashboard", "gpt-5.6-sol", "xhigh", "priority", 128_460),
            new("demo-2", "Refine Windows interaction", "gpt-5.6-luna", "high", "default", 86_240),
            new("demo-3", "Prepare V2.1.3 release", "gpt-5.6-terra", "medium", "default", 52_810)
        };
        foreach (MonitorData.TaskSnapshot snapshot in demoTasks)
            Tasks.Add(new TaskRow(snapshot, _isEnglish) { ShowTokens = _showTokens });

        this.FindControl<ListBox>("TaskList")!.SelectedItem = Tasks[0];
        UpdateStatusText();
        this.FindControl<TextBlock>("UpdatedText")!.Text = $"{T("Updated", "更新")}：19:26:13";

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _lastQuota = new MonitorData.QuotaSnapshot(
            new MonitorData.WindowLimit(32, now + 2 * 3600 + 18 * 60, 300),
            new MonitorData.WindowLimit(28, now + 5 * 86400 + 14 * 3600, 10080));
        UpdateQuota(_lastQuota);
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
        int nonClientRenderingDisabled = 1;
        DwmSetWindowAttribute(hwnd, 2, ref nonClientRenderingDisabled, sizeof(int));
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
        bool resizing = hitTest >= 10;
        if (resizing) CanResize = true;
        try
        {
            ReleaseCapture();
            SendMessage(hwnd, 0x00A1, (nint)hitTest, 0);
        }
        finally
        {
            if (resizing) CanResize = false;
            ApplyNativeWindowMaterial();
            RefreshGlassBackdrop();
        }
    }

    private void ApplySceneBackground(bool force = false)
    {
        bool isDay = _manualIsDay ?? DateTime.Now.Hour is >= 7 and < 19;
        if (!force && _sceneIsDay == isDay) return;

        SceneViewSettings settings = GetSceneSettings(isDay);
        Bitmap next = LoadSceneBitmap(isDay, settings);
        SceneBackdrop backdrop = this.FindControl<SceneBackdrop>("BackdropImage")!;
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
        UpdateSceneSettingsControls();
        ApplySceneLayoutTransform();
        RefreshGlassBackdrop();
    }

    private SceneViewSettings GetSceneSettings(bool isDay) => isDay ? _sceneSettings.Day : _sceneSettings.Night;

    private static Bitmap LoadSceneBitmap(bool isDay, SceneViewSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ImagePath) && File.Exists(settings.ImagePath))
        {
            try
            {
                return new Bitmap(settings.ImagePath);
            }
            catch
            {
                settings.ImagePath = null;
            }
        }

        Uri uri = new($"avares://CodexMonitorV2/Assets/{(isDay ? "sun" : "moon")}.png");
        return new Bitmap(AssetLoader.Open(uri));
    }

    private void ToggleSceneSettings(bool visible)
    {
        this.FindControl<Grid>("SceneSettingsOverlay")!.IsVisible = visible;
        this.FindControl<Border>("ForegroundLayer")!.IsVisible = !visible;
        if (visible) UpdateSceneSettingsControls();
    }

    private void UpdateSceneSettingsControls()
    {
        if (this.FindControl<Slider>("SceneZoomSlider") is not Slider zoom) return;
        bool isDay = _sceneIsDay ?? DateTime.Now.Hour is >= 7 and < 19;
        SceneViewSettings settings = GetSceneSettings(isDay);
        _syncingSceneControls = true;
        zoom.Value = settings.Zoom;
        this.FindControl<Slider>("ScenePositionXSlider")!.Value = settings.PositionX * 100;
        this.FindControl<Slider>("ScenePositionYSlider")!.Value = settings.PositionY * 100;
        this.FindControl<TextBlock>("SceneSettingsSceneValue")!.Text = isDay ? T("Sun", "太阳") : T("Moon", "月球");
        this.FindControl<TextBlock>("SceneImageNameText")!.Text = string.IsNullOrWhiteSpace(settings.ImagePath)
            ? T("Built-in image", "内置图片")
            : Path.GetFileName(settings.ImagePath);
        UpdateSceneSettingValueLabels(settings);
        _syncingSceneControls = false;
    }

    private void SceneControlsChanged()
    {
        if (_syncingSceneControls) return;
        bool isDay = _sceneIsDay ?? DateTime.Now.Hour is >= 7 and < 19;
        SceneViewSettings settings = GetSceneSettings(isDay);
        settings.Zoom = this.FindControl<Slider>("SceneZoomSlider")!.Value;
        settings.PositionX = this.FindControl<Slider>("ScenePositionXSlider")!.Value / 100;
        settings.PositionY = this.FindControl<Slider>("ScenePositionYSlider")!.Value / 100;
        UpdateSceneSettingValueLabels(settings);
        MarkSceneSettingsDirty();
        ApplySceneLayoutTransform();
        RefreshGlassBackdrop();
    }

    private void UpdateSceneSettingValueLabels(SceneViewSettings settings)
    {
        this.FindControl<TextBlock>("SceneZoomValue")!.Text = $"{settings.Zoom:0.00}×";
        this.FindControl<TextBlock>("ScenePositionXValue")!.Text = $"{settings.PositionX * 100:+0;-0;0}%";
        this.FindControl<TextBlock>("ScenePositionYValue")!.Text = $"{settings.PositionY * 100:+0;-0;0}%";
    }

    private async Task ChooseSceneImageAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Choose a background image", "选择背景图片"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(T("Image files", "图片文件"))
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" }
                }
            }
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        bool isDay = _sceneIsDay ?? DateTime.Now.Hour is >= 7 and < 19;
        GetSceneSettings(isDay).ImagePath = path;
        MarkSceneSettingsDirty();
        ApplySceneBackground(force: true);
    }

    private void ResetCurrentSceneSettings()
    {
        bool isDay = _sceneIsDay ?? DateTime.Now.Hour is >= 7 and < 19;
        GetSceneSettings(isDay).Reset(isDay);
        MarkSceneSettingsDirty();
        ApplySceneBackground(force: true);
    }

    private void MarkSceneSettingsDirty() =>
        this.FindControl<TextBlock>("SaveSceneSettingsText")!.Text = T("Save settings", "保存设置");

    private void SaveSceneSettings()
    {
        _sceneSettings.Save();
        this.FindControl<TextBlock>("SaveSceneSettingsText")!.Text = T("Saved", "已保存");
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
        this.FindControl<TextBlock>("SceneSettingsTitleText")!.Text = T("Background settings", "背景设置");
        this.FindControl<TextBlock>("SceneSettingsSubtitleText")!.Text = T(
            "Adjust the current scene without changing the window layout",
            "调整当前场景，不改变窗口布局");
        this.FindControl<TextBlock>("SceneSettingsSceneLabel")!.Text = T("Current scene", "当前场景");
        this.FindControl<TextBlock>("ChooseSceneImageText")!.Text = T("Choose image", "选择图片");
        this.FindControl<TextBlock>("ResetSceneImageText")!.Text = T("Restore default", "恢复默认");
        this.FindControl<TextBlock>("SceneZoomLabel")!.Text = T("Planet size", "星球大小");
        this.FindControl<TextBlock>("ScenePositionXLabel")!.Text = T("Horizontal", "水平位置");
        this.FindControl<TextBlock>("ScenePositionYLabel")!.Text = T("Vertical", "垂直位置");
        this.FindControl<TextBlock>("SceneSettingsHintText")!.Text = T(
            "The image keeps its aspect ratio. Position values are relative to the app window.",
            "图片始终保持宽高比，位置相对于软件窗口。");
        this.FindControl<TextBlock>("SaveSceneSettingsText")!.Text = T("Save settings", "保存设置");
        ToolTip.SetTip(this.FindControl<StackPanel>("DefaultSettingsPanel")!,
            hasSelectedTask
                ? T("Select a task, then set the global model and reasoning preference used by the next turn; the active turn remains unchanged",
                    "先选择任务，再设置下回合使用的全局模型与推理强度；正在生成的回合不会改变")
                : T("Set defaults for new Codex tasks; active turns remain unchanged",
                    "设置 Codex 新任务默认值；正在生成的回合不会改变"));
        ToolTip.SetTip(this.FindControl<Button>("LanguageButton")!,
            T("Switch to Chinese", "切换到英文"));
        ToolTip.SetTip(this.FindControl<Button>("SettingsButton")!,
            T("Background settings", "背景设置"));
        ToolTip.SetTip(this.FindControl<Button>("PinButton")!, Topmost
            ? T("Unpin window", "取消置顶")
            : T("Keep window on top", "窗口置顶"));
        UpdateSceneSettingsControls();
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
        if (this.FindControl<Grid>("SceneSettingsOverlay")!.IsVisible) UpdateSceneSettingsControls();
    }

    private void TogglePin()
    {
        Topmost = !Topmost;
        UpdatePinVisual();
    }

    private void UpdatePinVisual()
    {
        bool pinned = Topmost;
        this.FindControl<PathIcon>("PinIdleIcon")!.IsVisible = !pinned;
        this.FindControl<PathIcon>("PinActiveIcon")!.IsVisible = pinned;
        this.FindControl<Avalonia.Controls.Shapes.Ellipse>("PinActiveDot")!.IsVisible = pinned;
        Button button = this.FindControl<Button>("PinButton")!;
        if (pinned)
            button.Background = new SolidColorBrush(Color.Parse("#5A68458E"));
        else
            button.ClearValue(Button.BackgroundProperty);
        ToolTip.SetTip(button, pinned
            ? T("Unpin window", "取消置顶")
            : T("Keep window on top", "窗口置顶"));
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
    public bool PriorityVisible => !ShowTokens && _tier.Equals("priority", StringComparison.OrdinalIgnoreCase);
    public string PriorityTip => _isEnglish ? "Priority · 1.5× speed" : "优先通道 · 1.5× 速度";
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
            OnChanged(nameof(PriorityVisible));
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
        OnChanged(nameof(PriorityVisible));
        OnChanged(nameof(PriorityTip));
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
