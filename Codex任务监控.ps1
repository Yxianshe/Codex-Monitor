$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Text.UTF8Encoding]::new($false)

$script:singleInstanceCreated = $false
$script:singleInstanceMutex = [Threading.Mutex]::new(
    $true,
    'Local\CodexTaskMonitor.UI.SingleInstance',
    [ref]$script:singleInstanceCreated)
if (-not $script:singleInstanceCreated) {
    $script:singleInstanceMutex.Dispose()
    exit
}

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeResize {
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
public static class NativeIdentity {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static void Apply() {
        try { SetCurrentProcessExplicitAppUserModelID("OpenAI.CodexTaskMonitor"); }
        catch { }
    }
}
public static class NativeBackdrop {
    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd,
        ref WINDOWCOMPOSITIONATTRIBDATA data);

    public static bool Enable(IntPtr hwnd) {
        try {
            int darkMode = 0;
            DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));
            int backdrop = 1; // Disable DWM's active/inactive material switching.
            DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));
            int corner = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));

            ACCENT_POLICY policy = new ACCENT_POLICY {
                AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
                AccentFlags = 2,
                GradientColor = unchecked((int)0x20FFFFFF),
                AnimationId = 0
            };
            int size = Marshal.SizeOf(typeof(ACCENT_POLICY));
            IntPtr memory = Marshal.AllocHGlobal(size);
            try {
                Marshal.StructureToPtr(policy, memory, false);
                WINDOWCOMPOSITIONATTRIBDATA data = new WINDOWCOMPOSITIONATTRIBDATA {
                    Attribute = 19, // WCA_ACCENT_POLICY
                    Data = memory,
                    SizeOfData = size
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally {
                Marshal.FreeHGlobal(memory);
            }
        }
        catch { return false; }
    }
}
'@

[NativeIdentity]::Apply()

$CodexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME '.codex' }
$StateDb = Get-ChildItem $CodexHome -Filter 'state_*.sqlite' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
$Sqlite = Join-Path $PSScriptRoot 'sqlite3.exe'

function Find-CodexExecutable {
    $paths = @()
    $command = Get-Command codex.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { $paths += $command.Source }
    $paths += Join-Path $env:LOCALAPPDATA 'Programs\OpenAI\Codex\bin\codex.exe'
    $paths += Get-ChildItem (Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin\*\codex.exe') -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -ExpandProperty FullName
    $running = Get-CimInstance Win32_Process -Filter "name='codex.exe'" -ErrorAction SilentlyContinue |
        Where-Object ExecutablePath | Select-Object -First 1 -ExpandProperty ExecutablePath
    if ($running) { $paths += $running }
    $package = Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($package) { $paths += Join-Path $package.InstallLocation 'app\resources\codex.exe' }
    $paths | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

$Codex = Find-CodexExecutable
$script:defaultServiceTier = 'default'
$configPath = Join-Path $CodexHome 'config.toml'
if (Test-Path $configPath) {
    $tierLine = Select-String -Path $configPath -Pattern '^\s*service_tier\s*=\s*"([^"]+)"' | Select-Object -First 1
    if ($tierLine) { $script:defaultServiceTier = $tierLine.Matches[0].Groups[1].Value }
}

function U([string]$Text) { [regex]::Unescape($Text) }

$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:shell="clr-namespace:System.Windows.Shell;assembly=PresentationFramework"
        Title="Codex Monitor" Width="550" Height="460" MinWidth="500" MinHeight="420"
        WindowStartupLocation="CenterScreen" WindowStyle="None" AllowsTransparency="False"
        Background="Transparent" FontFamily="Segoe UI Variable Text, Segoe UI" ResizeMode="CanResize">
  <shell:WindowChrome.WindowChrome>
    <shell:WindowChrome CaptionHeight="0" ResizeBorderThickness="7" CornerRadius="0"
                        GlassFrameThickness="0" UseAeroCaptionButtons="False"/>
  </shell:WindowChrome.WindowChrome>
  <Window.Resources>
    <Style x:Key="FlatProgress" TargetType="ProgressBar">
      <Setter Property="Height" Value="8"/>
      <Setter Property="Background" Value="#E5E5E7"/>
      <Setter Property="Foreground" Value="#202124"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="ProgressBar">
            <Border Background="{TemplateBinding Background}" CornerRadius="4" ClipToBounds="True">
              <Grid x:Name="PART_Track">
                <Border x:Name="PART_Indicator" Background="{TemplateBinding Foreground}"
                        HorizontalAlignment="Left" CornerRadius="4"/>
              </Grid>
            </Border>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key="TaskItem" TargetType="ListBoxItem">
      <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
      <Setter Property="Padding" Value="0"/>
      <Setter Property="BorderThickness" Value="0"/>
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="Focusable" Value="False"/>
    </Style>
    <Style x:Key="GlassCard" TargetType="Border">
      <Setter Property="Background" Value="#66FFFFFF"/>
      <Setter Property="BorderBrush" Value="#B5FFFFFF"/>
      <Setter Property="BorderThickness" Value="1.2"/>
      <Setter Property="CornerRadius" Value="20"/>
      <Setter Property="Padding" Value="14"/>
      <Setter Property="Effect">
        <Setter.Value>
          <DropShadowEffect Color="#385467" BlurRadius="22" ShadowDepth="4" Opacity="0.18"/>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key="CircleButton" TargetType="Button">
      <Setter Property="BorderThickness" Value="0"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Focusable" Value="False"/>
      <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="ButtonBorder" Background="{TemplateBinding Background}" CornerRadius="18">
              <Border.Effect>
                <DropShadowEffect Color="#536273" BlurRadius="7" ShadowDepth="2" Opacity="0.14"/>
              </Border.Effect>
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="ButtonBorder" Property="Opacity" Value="0.72"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Window.Resources>

  <Border Name="RootShell" BorderBrush="#BFFFFFFF" BorderThickness="1" CornerRadius="22" Padding="16">
    <Border.Background>
      <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
        <GradientStop Color="#D9F4F8FA" Offset="0"/>
        <GradientStop Color="#D9EEF1F5" Offset="0.52"/>
        <GradientStop Color="#D9F7F4F1" Offset="1"/>
      </LinearGradientBrush>
    </Border.Background>
    <Grid>
      <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="*"/>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="Auto"/>
      </Grid.RowDefinitions>
      <Border Name="GlassRim" Grid.RowSpan="5" Margin="-14" CornerRadius="20" BorderThickness="1.4"
              Background="Transparent" IsHitTestVisible="False">
        <Border.BorderBrush>
          <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
            <GradientStop Color="#F4FFFFFF" Offset="0"/>
            <GradientStop Color="#82E8F7FF" Offset="0.28"/>
            <GradientStop Color="#38FFFFFF" Offset="0.58"/>
            <GradientStop Color="#A8FFF0F8" Offset="1"/>
          </LinearGradientBrush>
        </Border.BorderBrush>
      </Border>
      <Border Name="SpectralBlue" Grid.RowSpan="5" Margin="-12.4,-13.4,-13.6,-12.6" CornerRadius="19"
              BorderThickness="0.8" BorderBrush="#7C8BD9FF" Background="Transparent"
              IsHitTestVisible="False"/>
      <Border Name="SpectralRose" Grid.RowSpan="5" Margin="-13.6,-12.6,-12.4,-13.4" CornerRadius="19"
              BorderThickness="0.8" BorderBrush="#68FF9FCB" Background="Transparent"
              IsHitTestVisible="False"/>
      <Border Name="LiquidHighlight" Grid.RowSpan="5" Margin="-13" CornerRadius="19" IsHitTestVisible="False" Opacity="0.48">
        <Border.Background>
          <RadialGradientBrush Center="0.13,0.06" GradientOrigin="0.13,0.06" RadiusX="0.72" RadiusY="0.68">
            <GradientStop Color="#F2FFFFFF" Offset="0"/>
            <GradientStop Color="#64FFFFFF" Offset="0.3"/>
            <GradientStop Color="#16D9F5FF" Offset="0.66"/>
            <GradientStop Color="#00FFFFFF" Offset="1"/>
          </RadialGradientBrush>
        </Border.Background>
      </Border>
      <Border Grid.RowSpan="5" Margin="-13" CornerRadius="19" IsHitTestVisible="False">
        <Border.Background>
          <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
            <GradientStop Color="#00000000" Offset="0.5"/>
            <GradientStop Color="#12364B5D" Offset="0.82"/>
            <GradientStop Color="#244B6375" Offset="1"/>
          </LinearGradientBrush>
        </Border.Background>
      </Border>
      <Grid Name="TitleBar" Margin="0,0,0,11">
        <Border Name="DragArea" Background="Transparent" Margin="0,0,190,0" Cursor="SizeAll"/>
        <StackPanel IsHitTestVisible="False">
          <TextBlock Text="C O D E X   M O N I T O R" FontSize="9" FontWeight="SemiBold" Foreground="#7C8291"/>
          <TextBlock Text="&#x4EFB;&#x52A1;&#x4E0E;&#x4F7F;&#x7528;&#x91CF;" FontSize="21" FontWeight="SemiBold"
                     Foreground="#20232A" Margin="0,1,0,0"/>
        </StackPanel>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
          <Button Name="TokenButton" Content="Token" Width="64" Height="34"
                  Style="{StaticResource CircleButton}" Background="#70FFFFFF" Foreground="#61718A"
                  FontSize="12" FontWeight="SemiBold" ToolTip="&#x5207;&#x6362;&#x6A21;&#x578B;&#x4E0E; Token &#x4FE1;&#x606F;"/>
          <Button Name="ThemeButton" Width="34" Height="34" Margin="6,0,0,0"
                  Style="{StaticResource CircleButton}" Background="#70FFFFFF" ToolTip="&#x5207;&#x6362;&#x914d;&#x8272;">
            <Grid Width="18" Height="18">
              <Ellipse>
                <Ellipse.Fill>
                  <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                    <GradientStop Color="#A9B9D9" Offset="0"/>
                    <GradientStop Color="#B7A9D8" Offset="0.38"/>
                    <GradientStop Color="#8EC9BE" Offset="0.68"/>
                    <GradientStop Color="#D9AEB8" Offset="1"/>
                  </LinearGradientBrush>
                </Ellipse.Fill>
              </Ellipse>
              <Ellipse Margin="5" Fill="#8AFFFFFF"/>
            </Grid>
          </Button>
          <Button Name="PinButton" Width="34" Height="34" Margin="6,0,0,0" Opacity="0.88"
                  Style="{StaticResource CircleButton}" Background="#78FFFFFF" Foreground="#58677C"
                  ToolTip="&#x7F6E;&#x9876;">
            <Viewbox Width="17" Height="17">
              <Path Fill="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"
                    Data="M 5,2 L 11,2 L 10,6 L 12,8 L 9,8 L 8,14 L 7,8 L 4,8 L 6,6 Z"/>
            </Viewbox>
          </Button>
          <Button Name="MinimizeButton" Content="&#x2212;" Width="34" Height="34" Margin="6,0,0,0"
                  Style="{StaticResource CircleButton}" Background="#78FFFFFF" Foreground="#58677C"
                  FontSize="18" FontWeight="SemiBold" ToolTip="&#x6700;&#x5C0F;&#x5316;"/>
          <Button Name="CloseButton" Content="&#x00D7;" Width="34" Height="34" Margin="6,0,0,0"
                  Style="{StaticResource CircleButton}" Background="#78F9EEEE" Foreground="#925F66" FontSize="20"/>
        </StackPanel>
      </Grid>

      <DockPanel Grid.Row="1" Margin="2,0,2,6">
        <TextBlock Text="&#x6B63;&#x5728;&#x8C03;&#x7528;&#x7684;&#x4EFB;&#x52A1;" FontSize="14.5" FontWeight="SemiBold" Foreground="#30333B"/>
        <TextBlock Name="StatusText" DockPanel.Dock="Right" HorizontalAlignment="Right"
                   Foreground="#858B98" FontSize="12.5" VerticalAlignment="Center"/>
      </DockPanel>

      <Border Name="TaskCard" Grid.Row="2" Style="{StaticResource GlassCard}" Margin="0,0,0,10" Padding="4" MinHeight="88">
        <Grid>
          <Border Margin="1" CornerRadius="17" BorderThickness="1" IsHitTestVisible="False">
            <Border.BorderBrush>
              <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                <GradientStop Color="#CAFFFFFF" Offset="0"/>
                <GradientStop Color="#28FFFFFF" Offset="0.52"/>
                <GradientStop Color="#82DDF8F0" Offset="1"/>
              </LinearGradientBrush>
            </Border.BorderBrush>
          </Border>
          <Border Margin="3" CornerRadius="15" IsHitTestVisible="False" Opacity="0.46">
            <Border.Background>
              <RadialGradientBrush Center="0.12,0.05" GradientOrigin="0.12,0.05" RadiusX="0.82" RadiusY="0.9">
                <GradientStop Color="#B8FFFFFF" Offset="0"/>
                <GradientStop Color="#24FFFFFF" Offset="0.5"/>
                <GradientStop Color="#00FFFFFF" Offset="1"/>
              </RadialGradientBrush>
            </Border.Background>
          </Border>
          <ListBox Name="TaskGrid" Background="Transparent" BorderThickness="0" Foreground="#202124"
                   ItemContainerStyle="{StaticResource TaskItem}" ScrollViewer.HorizontalScrollBarVisibility="Disabled">
            <ListBox.ItemTemplate>
              <DataTemplate>
                <Border Background="#24FFFFFF" BorderBrush="#52FFFFFF" BorderThickness="1"
                        CornerRadius="13" Margin="2" Padding="10,7">
                <Grid>
                  <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                  </Grid.ColumnDefinitions>
                  <TextBlock Text="{Binding Title}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"
                             FontSize="13.5" Foreground="#252529" Margin="0,0,12,0" ToolTip="{Binding Title}"/>
                  <Border Grid.Column="1" Background="#CAE7E9F4" BorderBrush="#62FFFFFF" BorderThickness="1"
                          CornerRadius="12" Padding="10,5" ToolTip="{Binding DetailToolTip}">
                    <StackPanel Orientation="Horizontal">
                      <TextBlock Text="{Binding FastPrefix}" FontSize="12" Foreground="#252529"/>
                      <TextBlock Text="{Binding ModelLabel}" FontSize="12" Foreground="#252529"/>
                      <TextBlock Text="{Binding EffortText}" FontSize="12" Foreground="#8B5CF6"/>
                      <TextBlock Text="{Binding SpeedText}" FontSize="12" Foreground="#6E6E74"/>
                    </StackPanel>
                  </Border>
                </Grid>
                </Border>
              </DataTemplate>
            </ListBox.ItemTemplate>
          </ListBox>
        </Grid>
      </Border>

      <Grid Grid.Row="3" Margin="2,0,2,6">
        <TextBlock Text="&#x4F7F;&#x7528;&#x91CF;&#x6982;&#x89C8;" FontSize="15.5" FontWeight="SemiBold" Foreground="#272A31"/>
        <TextBlock Name="UpdatedText" HorizontalAlignment="Right" VerticalAlignment="Center"
                   Foreground="#969BA7" FontSize="10"/>
      </Grid>

      <Border Name="UsageCard" Grid.Row="4" BorderBrush="#84FFFFFF"
              BorderThickness="1" CornerRadius="14" Padding="10,6">
        <Border.Background>
          <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
            <GradientStop Color="#68FFFFFF" Offset="0"/>
            <GradientStop Color="#38F2F8FA" Offset="1"/>
          </LinearGradientBrush>
        </Border.Background>
        <Border.Effect>
          <DropShadowEffect Color="#526274" BlurRadius="13" ShadowDepth="3" Opacity="0.14"/>
        </Border.Effect>
        <Grid>
          <Grid.RowDefinitions>
            <RowDefinition Height="72"/>
            <RowDefinition Height="1"/>
            <RowDefinition Height="72"/>
          </Grid.RowDefinitions>

          <Border Grid.RowSpan="3" Margin="-5,-2" CornerRadius="12" BorderThickness="1"
                  IsHitTestVisible="False" Opacity="0.72">
            <Border.BorderBrush>
              <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                <GradientStop Color="#D8FFFFFF" Offset="0"/>
                <GradientStop Color="#2AFFFFFF" Offset="0.5"/>
                <GradientStop Color="#80E6F6FF" Offset="1"/>
              </LinearGradientBrush>
            </Border.BorderBrush>
          </Border>
          <Border Grid.RowSpan="3" Margin="-4,-1" CornerRadius="11" IsHitTestVisible="False" Opacity="0.32">
            <Border.Background>
              <RadialGradientBrush Center="0.08,0.02" GradientOrigin="0.08,0.02" RadiusX="0.78" RadiusY="0.86">
                <GradientStop Color="#E2FFFFFF" Offset="0"/>
                <GradientStop Color="#20FFFFFF" Offset="0.5"/>
                <GradientStop Color="#00FFFFFF" Offset="1"/>
              </RadialGradientBrush>
            </Border.Background>
          </Border>

          <Grid Grid.Row="0">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="72"/>
              <ColumnDefinition Width="*"/>
              <ColumnDefinition Width="1"/>
              <ColumnDefinition Width="148"/>
            </Grid.ColumnDefinitions>
            <StackPanel VerticalAlignment="Center" Margin="2,0,8,0">
              <TextBlock Text="5 &#x5C0F;&#x65F6;" FontSize="13.5" FontWeight="SemiBold" Foreground="#353B45"/>
              <TextBlock Text="&#x9650;&#x989D;&#x5468;&#x671F;" FontSize="10.5" Foreground="#858C99" Margin="0,2,0,0"/>
            </StackPanel>
            <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,14,0">
              <DockPanel Margin="0,0,0,5">
                <TextBlock Text="&#x5269;&#x4F59;&#x989D;&#x5EA6;" FontSize="12" Foreground="#59606C"/>
                <TextBlock Name="FiveQuotaValue" Text="--%" DockPanel.Dock="Right" HorizontalAlignment="Right"
                           FontSize="15" FontWeight="SemiBold" Foreground="#20242B"/>
              </DockPanel>
              <ProgressBar Name="FiveQuotaBar" Maximum="100" Value="0" Height="7"
                           Background="#45C9D0D9" Foreground="#5EA7FF" Style="{StaticResource FlatProgress}"/>
              <TextBlock Name="FiveQuotaCaption" Text="&#x7B49;&#x5F85;&#x66F4;&#x65B0;" FontSize="10.5"
                         Foreground="#858C99" Margin="0,4,0,0"/>
            </StackPanel>
            <Border Grid.Column="2" Background="#38FFFFFF" Margin="0,10"/>
            <Grid Grid.Column="3" Margin="12,0,0,0">
              <Grid.ColumnDefinitions><ColumnDefinition Width="62"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
              <Grid Width="62" Height="62">
                <Ellipse Width="52" Height="52" Stroke="#60E2DEED" StrokeThickness="5.5"/>
                <Path Name="FiveResetArc" Stroke="#998AFB" StrokeThickness="5.5"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
                <TextBlock Name="FiveResetValue" Text="--" HorizontalAlignment="Center" VerticalAlignment="Center"
                           FontSize="13" FontWeight="SemiBold" Foreground="#20242B"/>
              </Grid>
              <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="6,0,0,0">
                <TextBlock Text="&#x91CD;&#x7F6E;" FontSize="12" FontWeight="SemiBold" Foreground="#4D5360"/>
                <TextBlock Name="FiveResetSub" Text="&#x5269;&#x4F59;&#x65F6;&#x95F4;" FontSize="10.5" Foreground="#858C99"/>
                <TextBlock Name="FiveResetCaption" Text="--" FontSize="10.5" Foreground="#858C99" Margin="0,2,0,0"/>
              </StackPanel>
            </Grid>
          </Grid>

          <Border Grid.Row="1" Background="#42FFFFFF" Margin="4,0"/>

          <Grid Grid.Row="2">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="72"/>
              <ColumnDefinition Width="*"/>
              <ColumnDefinition Width="1"/>
              <ColumnDefinition Width="148"/>
            </Grid.ColumnDefinitions>
            <StackPanel VerticalAlignment="Center" Margin="2,0,8,0">
              <TextBlock Text="&#x6BCF;&#x5468;" FontSize="13.5" FontWeight="SemiBold" Foreground="#353B45"/>
              <TextBlock Text="&#x9650;&#x989D;&#x5468;&#x671F;" FontSize="10.5" Foreground="#858C99" Margin="0,2,0,0"/>
            </StackPanel>
            <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,14,0">
              <DockPanel Margin="0,0,0,5">
                <TextBlock Text="&#x5269;&#x4F59;&#x989D;&#x5EA6;" FontSize="12" Foreground="#59606C"/>
                <TextBlock Name="WeekQuotaValue" Text="--%" DockPanel.Dock="Right" HorizontalAlignment="Right"
                           FontSize="15" FontWeight="SemiBold" Foreground="#20242B"/>
              </DockPanel>
              <ProgressBar Name="WeekQuotaBar" Maximum="100" Value="0" Height="7"
                           Background="#45C9D0D9" Foreground="#58C6A5" Style="{StaticResource FlatProgress}"/>
              <TextBlock Name="WeekQuotaCaption" Text="&#x7B49;&#x5F85;&#x66F4;&#x65B0;" FontSize="10.5"
                         Foreground="#858C99" Margin="0,4,0,0"/>
            </StackPanel>
            <Border Grid.Column="2" Background="#38FFFFFF" Margin="0,10"/>
            <Grid Grid.Column="3" Margin="12,0,0,0">
              <Grid.ColumnDefinitions><ColumnDefinition Width="62"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
              <Grid Width="62" Height="62">
                <Ellipse Width="52" Height="52" Stroke="#60EAE2D8" StrokeThickness="5.5"/>
                <Path Name="WeekResetArc" Stroke="#E7AD62" StrokeThickness="5.5"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
                <TextBlock Name="WeekResetValue" Text="--" HorizontalAlignment="Center" VerticalAlignment="Center"
                           FontSize="12.5" FontWeight="SemiBold" Foreground="#20242B"/>
              </Grid>
              <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="6,0,0,0">
                <TextBlock Text="&#x91CD;&#x7F6E;" FontSize="12" FontWeight="SemiBold" Foreground="#4D5360"/>
                <TextBlock Name="WeekResetSub" Text="&#x5269;&#x4F59;&#x65F6;&#x95F4;" FontSize="10.5" Foreground="#858C99"/>
                <TextBlock Name="WeekResetCaption" Text="--" FontSize="10.5" Foreground="#858C99" Margin="0,2,0,0"/>
              </StackPanel>
            </Grid>
          </Grid>
        </Grid>
      </Border>
    </Grid>
  </Border>
</Window>
'@

$reader = [Xml.XmlNodeReader]::new([xml]$xaml)
$window = [Windows.Markup.XamlReader]::Load($reader)
$iconPath = Join-Path $PSScriptRoot 'Codex.ico'
if (Test-Path $iconPath) {
    $iconStream = [IO.File]::OpenRead($iconPath)
    try {
        $decoder = [Windows.Media.Imaging.BitmapDecoder]::Create(
            $iconStream,
            [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $window.Icon = $decoder.Frames[0]
    }
    finally { $iconStream.Dispose() }
}
$taskGrid = $window.FindName('TaskGrid')
$statusText = $window.FindName('StatusText')
$rootShell = $window.FindName('RootShell')
$taskCard = $window.FindName('TaskCard')
$usageCard = $window.FindName('UsageCard')
$fiveQuotaCard = $window.FindName('FiveQuotaCard')
$fiveResetCard = $window.FindName('FiveResetCard')
$weekQuotaCard = $window.FindName('WeekQuotaCard')
$weekResetCard = $window.FindName('WeekResetCard')
$fiveQuotaBar = $window.FindName('FiveQuotaBar')
$fiveQuotaValue = $window.FindName('FiveQuotaValue')
$fiveQuotaCaption = $window.FindName('FiveQuotaCaption')
$fiveResetArc = $window.FindName('FiveResetArc')
$fiveResetValue = $window.FindName('FiveResetValue')
$fiveResetSub = $window.FindName('FiveResetSub')
$fiveResetCaption = $window.FindName('FiveResetCaption')
$weekQuotaBar = $window.FindName('WeekQuotaBar')
$weekQuotaValue = $window.FindName('WeekQuotaValue')
$weekQuotaCaption = $window.FindName('WeekQuotaCaption')
$weekResetArc = $window.FindName('WeekResetArc')
$weekResetValue = $window.FindName('WeekResetValue')
$weekResetSub = $window.FindName('WeekResetSub')
$weekResetCaption = $window.FindName('WeekResetCaption')
$updatedText = $window.FindName('UpdatedText')
$closeButton = $window.FindName('CloseButton')
$minimizeButton = $window.FindName('MinimizeButton')
$pinButton = $window.FindName('PinButton')
$tokenButton = $window.FindName('TokenButton')
$themeButton = $window.FindName('ThemeButton')
$titleBar = $window.FindName('TitleBar')
$dragArea = $window.FindName('DragArea')
$glassRim = $window.FindName('GlassRim')
$liquidHighlight = $window.FindName('LiquidHighlight')
$spectralBlue = $window.FindName('SpectralBlue')
$spectralRose = $window.FindName('SpectralRose')

# Optical constants for common crown glass. Fresnel F0 and the edge shift are
# derived from the IOR rather than tuned as unrelated opacity values.
$script:glassIOR = 1.52
$script:fresnelF0 = [Math]::Pow(($script:glassIOR - 1.0) / ($script:glassIOR + 1.0), 2)
$script:refractionShift = 1.0 - (1.0 / $script:glassIOR)

function Apply-GlassPhysics {
    $liquidHighlight.Opacity = [Math]::Min(0.62, 0.30 + ($script:refractionShift * 0.55))
    $spectralBlue.Opacity = [Math]::Min(0.22, $script:fresnelF0 * 3.4)
    $spectralRose.Opacity = [Math]::Min(0.18, $script:fresnelF0 * 2.8)

    Update-GlassOptics 0.13 0.06
}

function Update-GlassOptics([double]$X, [double]$Y) {
    $dx = ($X - 0.5) * 2.0
    $dy = ($Y - 0.5) * 2.0
    $radius = [Math]::Min(1.0, [Math]::Sqrt(($dx * $dx) + ($dy * $dy)))

    # Schlick's approximation: reflected light increases toward grazing angles.
    $cosTheta = [Math]::Max(0.08, 1.0 - ($radius * 0.92))
    $fresnel = $script:fresnelF0 + ((1.0 - $script:fresnelF0) * [Math]::Pow(1.0 - $cosTheta, 5))
    $glassRim.Opacity = [Math]::Min(0.82, 0.36 + ($fresnel * 0.46))

    $highlightBrush = $liquidHighlight.Background -as [Windows.Media.RadialGradientBrush]
    if ($highlightBrush) {
        $center = [Windows.Point]::new(0.08 + ($X * 0.74), 0.04 + ($Y * 0.58))
        $highlightBrush.Center = $center
        $highlightBrush.GradientOrigin = $center
    }

    $edgeOffset = $script:refractionShift * (0.7 + ($radius * 1.8))
    $spectralBlue.RenderTransform = [Windows.Media.TranslateTransform]::new(-$dx * $edgeOffset, -$dy * $edgeOffset)
    $spectralRose.RenderTransform = [Windows.Media.TranslateTransform]::new($dx * $edgeOffset, $dy * $edgeOffset)
}

$script:themes = @(
    [pscustomobject]@{ Name = U '\u73bb\u7483\u7070'; Colors = @('#78F9FCFE', '#56DDEBF2', '#68FFFFFF'); CardTop = '#66FFFFFF'; CardBottom = '#2EDCEBF2'; Border = '#B5FFFFFF' },
    [pscustomobject]@{ Name = U '\u96fe\u7d2b'; Colors = @('#78FCFAFF', '#56DED5F3', '#68FFF9FF'); CardTop = '#66FFFFFF'; CardBottom = '#2EE8DFF8'; Border = '#B5FFFFFF' },
    [pscustomobject]@{ Name = U '\u6d77\u76d0\u84dd\u7eff'; Colors = @('#78F4FCFF', '#56CFEDE8', '#68F7FFFC'); CardTop = '#66FFFFFF'; CardBottom = '#2ED6F2EC'; Border = '#B5FFFFFF' },
    [pscustomobject]@{ Name = U '\u67d4\u7c89'; Colors = @('#78FFF7FA', '#56EBCFD9', '#68FFFDFC'); CardTop = '#66FFFFFF'; CardBottom = '#2EF4DEE7'; Border = '#B5FFFFFF' }
)
$script:themeIndex = 0

function New-ColorBrush([string]$Color) {
    [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($Color))
}

function Set-Theme([int]$Index) {
    $count = $script:themes.Count
    $script:themeIndex = (($Index % $count) + $count) % $count
    $theme = $script:themes[$script:themeIndex]

    $gradient = [Windows.Media.LinearGradientBrush]::new()
    $gradient.StartPoint = [Windows.Point]::new(0, 0)
    $gradient.EndPoint = [Windows.Point]::new(1, 1)
    for ($i = 0; $i -lt $theme.Colors.Count; $i++) {
        $color = [Windows.Media.ColorConverter]::ConvertFromString($theme.Colors[$i])
        [void]$gradient.GradientStops.Add([Windows.Media.GradientStop]::new($color, $i / ($theme.Colors.Count - 1)))
    }
    $rootShell.Background = $gradient

    $cardBrush = [Windows.Media.LinearGradientBrush]::new()
    $cardBrush.StartPoint = [Windows.Point]::new(0, 0)
    $cardBrush.EndPoint = [Windows.Point]::new(0, 1)
    [void]$cardBrush.GradientStops.Add([Windows.Media.GradientStop]::new(
        [Windows.Media.ColorConverter]::ConvertFromString($theme.CardTop), 0))
    [void]$cardBrush.GradientStops.Add([Windows.Media.GradientStop]::new(
        [Windows.Media.ColorConverter]::ConvertFromString($theme.CardBottom), 1))
    $borderBrush = New-ColorBrush $theme.Border
    foreach ($card in @($taskCard)) {
        if ($card) {
            $card.Background = $cardBrush
            $card.BorderBrush = $borderBrush
        }
    }
    $themeButton.Background = New-ColorBrush '#70FFFFFF'
    $themeButton.ToolTip = ((U '\u914d\u8272\uff1a{0}\uff08\u70b9\u51fb\u5207\u6362\uff09  \u00b7  Liquid Glass IOR {1:0.00}') -f $theme.Name, $script:glassIOR)
}

Apply-GlassPhysics
Set-Theme $script:themeIndex

$script:taskTitleIndexStamp = ''
$script:taskTitleById = @{}

function Get-TaskTitleMap {
    $indexPath = Join-Path $CodexHome 'session_index.jsonl'
    if (-not (Test-Path -LiteralPath $indexPath)) { return $script:taskTitleById }

    $indexFile = Get-Item -LiteralPath $indexPath
    $stamp = '{0}:{1}' -f $indexFile.Length, $indexFile.LastWriteTimeUtc.Ticks
    if ($stamp -ne $script:taskTitleIndexStamp) {
        $titleById = @{}
        Get-Content -LiteralPath $indexPath -Encoding UTF8 | ForEach-Object {
            try {
                $record = $_ | ConvertFrom-Json
                if ($record.id -and -not [string]::IsNullOrWhiteSpace([string]$record.thread_name)) {
                    $titleById[[string]$record.id] = [string]$record.thread_name
                }
            }
            catch {}
        }
        $script:taskTitleById = $titleById
        $script:taskTitleIndexStamp = $stamp
    }
    $script:taskTitleById
}

$script:showTaskTokens = $false

function Format-TokenCount([long]$Tokens) {
    if ($Tokens -ge 1000000000) { return ('{0:0.00}B' -f ($Tokens / 1000000000.0)) }
    if ($Tokens -ge 1000000) { return ('{0:0.0}M' -f ($Tokens / 1000000.0)) }
    if ($Tokens -ge 1000) { return ('{0:0.0}K' -f ($Tokens / 1000.0)) }
    '{0:N0}' -f $Tokens
}

function Update-TokenButtonAppearance {
    if ($script:showTaskTokens) {
        $tokenButton.Background = New-ColorBrush '#9ADCE7F4'
        $tokenButton.Foreground = New-ColorBrush '#405674'
        $tokenButton.ToolTip = U '\u5f53\u524d\u663e\u793a\u6bcf\u4efb\u52a1\u7d2f\u8ba1 Token\uff0c\u70b9\u51fb\u5207\u6362\u6a21\u578b'
    }
    else {
        $tokenButton.Background = New-ColorBrush '#70FFFFFF'
        $tokenButton.Foreground = New-ColorBrush '#61718A'
        $tokenButton.ToolTip = U '\u5f53\u524d\u663e\u793a\u6a21\u578b\uff0c\u70b9\u51fb\u5207\u6362\u7d2f\u8ba1 Token'
    }
}

Update-TokenButtonAppearance

function Get-ActiveTasks {
    if (-not $StateDb -or -not (Test-Path $Sqlite) -or -not (Test-Path $StateDb)) { return @() }
    $titleById = Get-TaskTitleMap
    $query = "select id, rollout_path as path, title, coalesce(model, '-') as model, coalesce(reasoning_effort, '') as effort, coalesce(tokens_used, 0) as tokens_used from threads where archived=0 order by updated_at_ms desc limit 12;"
    $json = (& $Sqlite -json $StateDb $query) -join "`n"
    if ([string]::IsNullOrWhiteSpace($json)) { return @() }

    ($json | ConvertFrom-Json) | ForEach-Object {
        $runtime = Get-ThreadRuntimeState $_.path
        if (-not $runtime.Active) { return }
        $title = [string]$titleById[[string]$_.id]
        if ([string]::IsNullOrWhiteSpace($title)) { $title = (($_.title -split "`r?`n")[0]).Trim() }
        if ($title.Length -gt 80) { $title = $title.Substring(0, 80) + '...' }
        if ($script:showTaskTokens) {
            $tokenValue = [long]$_.tokens_used
            [pscustomobject]@{
                Title = $title
                FastPrefix = U '\u2211 '
                ModelLabel = Format-TokenCount $tokenValue
                EffortText = '  Token'
                SpeedText = ''
                DetailToolTip = ((U '\u7d2f\u8ba1 Token\uff1a{0:N0}') -f $tokenValue)
            }
            return
        }
        $model = if ($runtime.Model) { $runtime.Model } else { $_.model }
        $effort = if ($runtime.Effort) { $runtime.Effort } else { $_.effort }
        $tier = if ($runtime.ServiceTier) { $runtime.ServiceTier } else { $script:defaultServiceTier }
        $effortText = @{
            minimal = U '\u6700\u4f4e'; low = U '\u4f4e'; medium = U '\u4e2d'; high = U '\u9ad8'
            xhigh = U '\u6781\u9ad8'; max = U '\u6700\u9ad8'; ultra = U '\u8d85\u9ad8'
        }[$effort]
        if (-not $effortText) { $effortText = $effort }
        $modelLabel = [Globalization.CultureInfo]::InvariantCulture.TextInfo.ToTitleCase((($model -replace '^gpt-', '') -replace '-', ' '))
        $fast = $tier -eq 'priority'
        [pscustomobject]@{
            Title = $title
            FastPrefix = if ($fast) { U '\u26a1 ' } else { '' }
            ModelLabel = $modelLabel
            EffortText = if ($effortText) { "  $effortText" } else { '' }
            SpeedText = if ($fast) { (U '  1.5\u00d7') } else { (U '  \u6807\u51c6') }
            DetailToolTip = ((U '\u6a21\u578b\uff1a{0}') -f $model)
        }
    }
}

$script:threadRuntimeCache = @{}

function Get-ThreadRuntimeState([string]$Path) {
    $result = [ordered]@{ Active = $false; Model = $null; Effort = $null; ServiceTier = $null }
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return [pscustomobject]$result }

    $fileLength = (Get-Item -LiteralPath $Path).Length
    $cached = $script:threadRuntimeCache[$Path]
    if ($cached -and $fileLength -ge $cached.Length) {
        if ($fileLength -eq $cached.Length) { return $cached.State }

        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            [void]$stream.Seek($cached.Length, [IO.SeekOrigin]::Begin)
            $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false), $false, 65536, $true)
            try { $text = $cached.Tail + $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }

        $start = $text.LastIndexOf('"type":"task_started"')
        $complete = $text.LastIndexOf('"type":"task_complete"')
        $aborted = $text.LastIndexOf('"type":"turn_aborted"')
        $latestTerminal = [Math]::Max($complete, $aborted)
        if ($start -ge 0 -or $latestTerminal -ge 0) { $cached.State.Active = $start -gt $latestTerminal }

        $settingsAt = $text.LastIndexOf('"type":"thread_settings_applied"')
        if ($settingsAt -ge 0) {
            $length = [Math]::Min(8192, $text.Length - $settingsAt)
            $snippet = $text.Substring($settingsAt, $length)
            $match = [regex]::Match($snippet, '"model":"([^"]+)"')
            if ($match.Success) { $cached.State.Model = $match.Groups[1].Value }
            $match = [regex]::Match($snippet, '"reasoning_effort":"([^"]+)"')
            if ($match.Success) { $cached.State.Effort = $match.Groups[1].Value }
            $match = [regex]::Match($snippet, '"service_tier":"([^"]+)"')
            if ($match.Success) { $cached.State.ServiceTier = $match.Groups[1].Value }
        }

        $cached.Length = $fileLength
        $cached.Tail = $text.Substring([Math]::Max(0, $text.Length - 8192))
        return $cached.State
    }

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $position = $stream.Length
        $suffix = ''
        $endTail = ''
        $firstChunk = $true
        $stateFound = $false
        $settingsFound = $false
        $scanned = 0L
        $chunkSize = 1MB
        while ($position -gt 0 -and $scanned -lt 128MB -and (-not $stateFound -or ($result.Active -and -not $settingsFound))) {
            $count = [int][Math]::Min($chunkSize, $position)
            $position -= $count
            [void]$stream.Seek($position, [IO.SeekOrigin]::Begin)
            $bytes = New-Object byte[] $count
            $read = $stream.Read($bytes, 0, $count)
            $text = [Text.Encoding]::UTF8.GetString($bytes, 0, $read) + $suffix
            $scanned += $read
            if ($firstChunk) {
                $endTail = $text.Substring([Math]::Max(0, $text.Length - 8192))
                $firstChunk = $false
            }

            if (-not $stateFound) {
                $start = $text.LastIndexOf('"type":"task_started"')
                $complete = $text.LastIndexOf('"type":"task_complete"')
                $aborted = $text.LastIndexOf('"type":"turn_aborted"')
                $latestTerminal = [Math]::Max($complete, $aborted)
                if ($start -ge 0 -or $latestTerminal -ge 0) {
                    $result.Active = $start -gt $latestTerminal
                    $stateFound = $true
                }
            }

            if (-not $settingsFound) {
                $settingsAt = $text.LastIndexOf('"type":"thread_settings_applied"')
                if ($settingsAt -ge 0) {
                    $length = [Math]::Min(8192, $text.Length - $settingsAt)
                    $snippet = $text.Substring($settingsAt, $length)
                    $match = [regex]::Match($snippet, '"model":"([^"]+)"')
                    if ($match.Success) { $result.Model = $match.Groups[1].Value }
                    $match = [regex]::Match($snippet, '"reasoning_effort":"([^"]+)"')
                    if ($match.Success) { $result.Effort = $match.Groups[1].Value }
                    $match = [regex]::Match($snippet, '"service_tier":"([^"]+)"')
                    if ($match.Success) { $result.ServiceTier = $match.Groups[1].Value }
                    $settingsFound = $true
                }
            }

            $suffix = $text.Substring(0, [Math]::Min(8192, $text.Length))
        }
    }
    finally { $stream.Dispose() }
    $state = [pscustomobject]$result
    $script:threadRuntimeCache[$Path] = [pscustomobject]@{ Length = $fileLength; Tail = $endTail; State = $state }
    $state
}

function Read-JsonLine($Process, [int]$TimeoutMilliseconds) {
    $read = $Process.StandardOutput.ReadLineAsync()
    if (-not $read.Wait($TimeoutMilliseconds)) { throw 'Codex response timed out.' }
    if ([string]::IsNullOrWhiteSpace($read.Result)) { throw 'Codex response stream closed.' }
    $read.Result | ConvertFrom-Json
}

function Get-RateLimits {
    if (-not $Codex -or -not (Test-Path $Codex)) { throw 'Codex executable not found.' }

    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Codex
    $info.Arguments = 'app-server --stdio'
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info

    try {
        [void]$process.Start()
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"quota-widget","version":"1.0"},"capabilities":null}}')
        $process.StandardInput.Flush()

        $initialized = $false
        for ($i = 0; $i -lt 6; $i++) {
            $message = Read-JsonLine $process 8000
            if ($message.id -eq 1) { $initialized = $true; break }
        }
        if (-not $initialized) { throw 'Codex app-server did not initialize.' }

        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"initialized"}')
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"account/rateLimits/read","params":null}')
        $process.StandardInput.Flush()

        for ($i = 0; $i -lt 8; $i++) {
            $message = Read-JsonLine $process 10000
            if ($message.id -eq 2) { return $message.result.rateLimits }
        }
        throw 'Rate limit request timed out.'
    }
    finally {
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

function Limit-Percent([double]$Value) {
    [Math]::Max(0.0, [Math]::Min(100.0, $Value))
}

function Set-RingArc($Path, [double]$Percent) {
    $value = Limit-Percent $Percent
    if (-not $Path -or $value -le 0) {
        if ($Path) { $Path.Data = $null }
        return
    }

    $angle = 359.99 * $value / 100.0
    $radians = (-90.0 + $angle) * [Math]::PI / 180.0
    $figure = [Windows.Media.PathFigure]::new()
    $figure.StartPoint = [Windows.Point]::new(31, 5)
    $segment = [Windows.Media.ArcSegment]::new()
    $segment.Point = [Windows.Point]::new(
        31 + 26 * [Math]::Cos($radians),
        31 + 26 * [Math]::Sin($radians))
    $segment.Size = [Windows.Size]::new(26, 26)
    $segment.IsLargeArc = $angle -gt 180
    $segment.SweepDirection = [Windows.Media.SweepDirection]::Clockwise
    $segment.IsStroked = $true
    [void]$figure.Segments.Add($segment)
    $geometry = [Windows.Media.PathGeometry]::new()
    [void]$geometry.Figures.Add($figure)
    $Path.Data = $geometry
}

function Get-CountdownText([double]$Seconds) {
    $secondsLeft = [Math]::Max(0, [long]$Seconds)
    if ($secondsLeft -le 0) {
        return [pscustomobject]@{ Value = U '\u5373\u5c06'; Sub = U '\u91cd\u7f6e' }
    }

    $days = [Math]::Floor($secondsLeft / 86400)
    $hours = [Math]::Floor(($secondsLeft % 86400) / 3600)
    $minutes = [Math]::Floor(($secondsLeft % 3600) / 60)
    if ($days -gt 0) {
        return [pscustomobject]@{
            Value = ((U '{0}\u5929 {1}\u65f6') -f $days, $hours)
            Sub = U '\u5269\u4f59\u65f6\u95f4'
        }
    }
    if ($hours -gt 0) {
        return [pscustomobject]@{
            Value = ((U '{0}\u65f6 {1}\u5206') -f $hours, $minutes)
            Sub = U '\u5269\u4f59\u65f6\u95f4'
        }
    }
    [pscustomobject]@{
        Value = ((U '{0}\u5206') -f [Math]::Max(1, $minutes))
        Sub = U '\u5269\u4f59\u65f6\u95f4'
    }
}

function Set-ShellTint([double]$Remaining) {
    if (-not $rootShell) { return }
    if ($Remaining -lt 10) {
        $colors = @('#FFF7F6', '#FFF0ED', '#F7F2FA')
    }
    elseif ($Remaining -lt 50) {
        $colors = @('#FBFCF5', '#FFF8E8', '#F5F4FF')
    }
    else {
        $colors = @('#F7FBFF', '#F1F5FF', '#FFF8F2')
    }
    $brush = [Windows.Media.LinearGradientBrush]::new()
    $brush.StartPoint = [Windows.Point]::new(0, 0)
    $brush.EndPoint = [Windows.Point]::new(1, 1)
    for ($i = 0; $i -lt $colors.Count; $i++) {
        $color = [Windows.Media.ColorConverter]::ConvertFromString($colors[$i])
        $stop = [Windows.Media.GradientStop]::new($color, $i / ($colors.Count - 1))
        [void]$brush.GradientStops.Add($stop)
    }
    $rootShell.Background = $brush
}

function Update-QuotaView($limits) {
    if (-not $limits -or -not $limits.primary -or -not $limits.secondary) { return }

    $primaryUsed = Limit-Percent ([double]$limits.primary.usedPercent)
    $secondaryUsed = Limit-Percent ([double]$limits.secondary.usedPercent)
    $primary = [int][Math]::Round(100 - $primaryUsed)
    $secondary = [int][Math]::Round(100 - $secondaryUsed)

    $fiveQuotaBar.Value = $primary
    $weekQuotaBar.Value = $secondary
    $fiveQuotaValue.Text = '{0}%' -f $primary
    $weekQuotaValue.Text = '{0}%' -f $secondary
    $fiveQuotaCaption.Text = ((U '\u5df2\u7528 {0}%') -f ([int][Math]::Round($primaryUsed)))
    $weekQuotaCaption.Text = ((U '\u5df2\u7528 {0}%') -f ([int][Math]::Round($secondaryUsed)))

    $nowEpoch = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $primaryWindow = if ([double]$limits.primary.windowDurationMins -gt 0) { [double]$limits.primary.windowDurationMins } else { 300.0 }
    $secondaryWindow = if ([double]$limits.secondary.windowDurationMins -gt 0) { [double]$limits.secondary.windowDurationMins } else { 10080.0 }

    if ($limits.primary.resetsAt) {
        $primarySeconds = [Math]::Max(0, [double]$limits.primary.resetsAt - $nowEpoch)
        Set-RingArc $fiveResetArc (100 * $primarySeconds / ($primaryWindow * 60))
        $countdown = Get-CountdownText $primarySeconds
        $fiveResetValue.Text = $countdown.Value
        $fiveResetSub.Text = $countdown.Sub
        $reset = [DateTimeOffset]::FromUnixTimeSeconds([long]$limits.primary.resetsAt).LocalDateTime
        $fiveResetCaption.Text = ((U '{0} \u91cd\u7f6e') -f $reset.ToString('HH:mm'))
        $fiveResetArc.ToolTip = $reset.ToString('yyyy-MM-dd HH:mm')
    }

    if ($limits.secondary.resetsAt) {
        $secondarySeconds = [Math]::Max(0, [double]$limits.secondary.resetsAt - $nowEpoch)
        Set-RingArc $weekResetArc (100 * $secondarySeconds / ($secondaryWindow * 60))
        $countdown = Get-CountdownText $secondarySeconds
        $weekResetValue.Text = $countdown.Value
        $weekResetSub.Text = $countdown.Sub
        $reset = [DateTimeOffset]::FromUnixTimeSeconds([long]$limits.secondary.resetsAt).LocalDateTime
        $weekResetCaption.Text = ((U '{0}\u6708{1}\u65e5 \u91cd\u7f6e') -f $reset.Month, $reset.Day)
        $weekResetArc.ToolTip = $reset.ToString('yyyy-MM-dd HH:mm')
    }

}

$script:cachedLimits = $null
$script:lastQuotaRefresh = [DateTime]::MinValue
$script:quotaJob = $null
$script:readJsonLineCode = ${function:Read-JsonLine}.ToString()
$script:getRateLimitsCode = ${function:Get-RateLimits}.ToString()

function Update-QuotaAsync {
    if ($script:quotaJob) {
        if ($script:quotaJob.State -eq 'Completed') {
            try {
                $value = Receive-Job $script:quotaJob -ErrorAction Stop | Select-Object -Last 1
                if ($value) { $script:cachedLimits = $value }
            }
            catch {}
            Remove-Job $script:quotaJob -Force -ErrorAction SilentlyContinue
            $script:quotaJob = $null
            $script:lastQuotaRefresh = Get-Date
        }
        elseif ($script:quotaJob.State -in @('Failed', 'Stopped', 'Disconnected')) {
            Remove-Job $script:quotaJob -Force -ErrorAction SilentlyContinue
            $script:quotaJob = $null
            $script:lastQuotaRefresh = Get-Date
        }
    }

    if (-not $script:quotaJob -and (-not $script:cachedLimits -or ((Get-Date) - $script:lastQuotaRefresh).TotalSeconds -ge 30)) {
        $script:quotaJob = Start-Job -ArgumentList $Codex, $script:readJsonLineCode, $script:getRateLimitsCode -ScriptBlock {
            param($codexPath, $readCode, $rateCode)
            Set-Item function:Read-JsonLine ([scriptblock]::Create($readCode))
            Set-Item function:Get-RateLimits ([scriptblock]::Create($rateCode))
            $Codex = $codexPath
            Get-RateLimits
        }
    }
}

function Refresh-View {
    try {
        $tasks = @(Get-ActiveTasks)
        $taskGrid.ItemsSource = $tasks
        if ($tasks.Count -eq 0) {
            $statusText.Text = U '\u6682\u65e0\u6b63\u5728\u8c03\u7528\u7684\u4efb\u52a1'
        } else {
            $statusText.Text = (U '\u6b63\u5728\u8c03\u7528 {0} \u4e2a\u4efb\u52a1') -f $tasks.Count
        }
    }
    catch {
        $statusText.Text = U '\u8bfb\u53d6\u5931\u8d25'
        $updatedText.Text = $_.Exception.Message
    }

    Update-QuotaAsync
    if ($script:cachedLimits) { Update-QuotaView $script:cachedLimits }
    $updatedText.Text = ((U '\u66f4\u65b0\uff1a{0}') -f (Get-Date -Format 'HH:mm:ss'))
}

$timer = [Windows.Threading.DispatcherTimer]::new()
$timer.Interval = [TimeSpan]::FromSeconds(5)
$timer.Add_Tick({ Refresh-View })
$window.Add_SourceInitialized({
    $handle = [Windows.Interop.WindowInteropHelper]::new($window).Handle
    $source = [Windows.Interop.HwndSource]::FromHwnd($handle)
    if ($source -and $source.CompositionTarget) {
        $source.CompositionTarget.BackgroundColor = [Windows.Media.Colors]::Transparent
    }
    if (-not [NativeBackdrop]::Enable($handle)) {
        $window.Background = New-ColorBrush '#FFF8FAFC'
    }
})
$window.Add_ContentRendered({ Refresh-View; $timer.Start() })
$window.Add_Closed({
    $timer.Stop()
    if ($script:quotaJob) {
        Stop-Job $script:quotaJob -ErrorAction SilentlyContinue
        Remove-Job $script:quotaJob -Force -ErrorAction SilentlyContinue
    }
    if ($script:singleInstanceMutex) {
        try { $script:singleInstanceMutex.ReleaseMutex() } catch {}
        $script:singleInstanceMutex.Dispose()
        $script:singleInstanceMutex = $null
    }
})
$closeButton.Add_Click({ $window.Close() })
$minimizeButton.Add_Click({ $window.WindowState = [Windows.WindowState]::Minimized })
$tokenButton.Add_Click({
    $script:showTaskTokens = -not $script:showTaskTokens
    Update-TokenButtonAppearance
    Refresh-View
})
$themeButton.Add_Click({
    Set-Theme ($script:themeIndex + 1)
})
$rootShell.Add_MouseMove({
    param($sender, $eventArgs)
    if ($rootShell.ActualWidth -le 0 -or $rootShell.ActualHeight -le 0) { return }

    $position = $eventArgs.GetPosition($rootShell)
    $x = [Math]::Max(0.04, [Math]::Min(0.96, $position.X / $rootShell.ActualWidth))
    $y = [Math]::Max(0.03, [Math]::Min(0.94, $position.Y / $rootShell.ActualHeight))
    Update-GlassOptics $x $y
})
$rootShell.Add_MouseLeave({
    Update-GlassOptics 0.13 0.06
})
$pinButton.Add_Click({
    $window.Topmost = -not $window.Topmost
    if ($window.Topmost) {
        $pinButton.Opacity = 1.0
        $pinButton.Background = New-ColorBrush '#9ADCE7F4'
        $pinButton.Foreground = New-ColorBrush '#40516A'
    }
    else {
        $pinButton.Opacity = 0.88
        $pinButton.Background = New-ColorBrush '#78FFFFFF'
        $pinButton.Foreground = New-ColorBrush '#58677C'
    }
})
$dragArea.Add_PreviewMouseLeftButtonDown({
    param($sender, $eventArgs)
    if ($eventArgs.LeftButton -eq [Windows.Input.MouseButtonState]::Pressed) {
        $eventArgs.Handled = $true
        $timer.Stop()
        [void][NativeResize]::ReleaseCapture()
        $handle = (New-Object Windows.Interop.WindowInteropHelper($window)).Handle
        [void][NativeResize]::SendMessage($handle, 0x00A1, [IntPtr]2, [IntPtr]::Zero)
        $timer.Start()
    }
})
[void]$window.ShowDialog()
