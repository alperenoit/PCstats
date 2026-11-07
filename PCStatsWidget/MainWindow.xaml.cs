using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PCStatsWidget.Services;

namespace PCStatsWidget;

public partial class MainWindow : Window
{
    private const int HOTKEY_ID_LOCK = 9001;
    private const int HOTKEY_ID_MODE = 9002;
    private const int HOTKEY_ID_HIDE = 9003;

    private readonly HardwareMonitorService _hardwareService;
    private readonly FpsMonitorService _fpsService;
    private readonly PingService _pingService;

    private readonly DispatcherTimer _metricsTimer;
    private readonly DispatcherTimer _pingTimer;

    private bool _isLockedClickThrough;
    private bool _isCompactMode;
    private bool _isHidden;
    private bool _isUpdatingMetrics;

    // Renk Fırçaları
    private static readonly SolidColorBrush BrushGood = new(Color.FromRgb(0x00, 0xE6, 0x76));    // Yeşil
    private static readonly SolidColorBrush BrushCyan = new(Color.FromRgb(0x38, 0xBD, 0xF8));    // Açık Mavi
    private static readonly SolidColorBrush BrushWarning = new(Color.FromRgb(0xFB, 0xBF, 0x24)); // Sarı/Turuncu
    private static readonly SolidColorBrush BrushDanger = new(Color.FromRgb(0xEF, 0x44, 0x44));  // Kırmızı
    private static readonly SolidColorBrush BrushMuted = new(Color.FromRgb(0x94, 0xA3, 0xB8));   // Gri

    public MainWindow()
    {
        InitializeComponent();

        _hardwareService = new HardwareMonitorService();
        _fpsService = new FpsMonitorService();
        _pingService = new PingService();

        // Sağ üst köşeye yakın varsayılan başlangıç pozisyonu
        double screenWidth = SystemParameters.WorkArea.Width;
        Left = screenWidth - 330;
        Top = 24;

        if (!_hardwareService.IsAdministrator)
        {
            BtnAdmin.Visibility = Visibility.Visible;
        }

        _metricsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _metricsTimer.Tick += MetricsTimer_Tick;

        _pingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(2000)
        };
        _pingTimer.Tick += PingTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Win32Helper.SetToolWindowStyle(this);
        RegisterGlobalHotkeys();

        // İlk okumaları hemen yap
        await UpdateHardwareAndFpsAsync();
        await UpdatePingAsync();

        _metricsTimer.Start();
        _pingTimer.Start();
    }

    private void RegisterGlobalHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);

        // F8: Hayalet Modu (Click-Through Kilit)
        Win32Helper.RegisterHotKey(hwnd, HOTKEY_ID_LOCK, Win32Helper.MOD_NONE, Win32Helper.VK_F8);
        // F9: Mini Bar / Detaylı Mod
        Win32Helper.RegisterHotKey(hwnd, HOTKEY_ID_MODE, Win32Helper.MOD_NONE, Win32Helper.VK_F9);
        // F10: Gizle / Göster
        Win32Helper.RegisterHotKey(hwnd, HOTKEY_ID_HIDE, Win32Helper.MOD_NONE, Win32Helper.VK_F10);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Helper.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            switch (id)
            {
                case HOTKEY_ID_LOCK:
                    ToggleLockClickThrough();
                    handled = true;
                    break;
                case HOTKEY_ID_MODE:
                    ToggleViewMode();
                    handled = true;
                    break;
                case HOTKEY_ID_HIDE:
                    ToggleVisibility();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private async void MetricsTimer_Tick(object? sender, EventArgs e)
    {
        await UpdateHardwareAndFpsAsync();
    }

    private async void PingTimer_Tick(object? sender, EventArgs e)
    {
        await UpdatePingAsync();
    }

    private async Task UpdateHardwareAndFpsAsync()
    {
        if (_isUpdatingMetrics || _isHidden) return;
        _isUpdatingMetrics = true;

        try
        {
            // Arka plan iş parçacığında donanım sensörlerini ve FPS'i oku
            var (hw, fps, fpsSource) = await Task.Run(() =>
            {
                var snapshot = _hardwareService.ReadSnapshot();
                int currentFps = _fpsService.GetCurrentFps();
                string src = _fpsService.FpsSource;
                return (snapshot, currentFps, src);
            });

            // 1. FPS Güncellemesi
            TxtFpsValue.Text = fps.ToString();
            TxtFpsSource.Text = fpsSource;
            TxtCompactFps.Text = fps.ToString();

            var fpsBrush = fps >= 55 ? BrushGood : (fps >= 30 ? BrushWarning : BrushDanger);
            TxtFpsValue.Foreground = fpsBrush;
            TxtCompactFps.Foreground = fpsBrush;

            // 2. CPU Güncellemesi
            TxtCpuName.Text = hw.CpuName;
            TxtCpuUsage.Text = $"{hw.CpuUsage:F0}%";
            BarCpuUsage.Value = hw.CpuUsage;
            BarCpuUsage.Foreground = hw.CpuUsage > 85 ? BrushDanger : (hw.CpuUsage > 65 ? BrushWarning : BrushCyan);

            string cpuTempStr = hw.CpuTemp.HasValue ? $"{hw.CpuTemp.Value:F0}°C" : "N/A";
            TxtCpuTemp.Text = cpuTempStr;
            TxtCpuTemp.Foreground = GetTemperatureBrush(hw.CpuTemp);

            TxtCompactCpu.Text = $"{hw.CpuUsage:F0}%  {cpuTempStr}";

            // 3. GPU Güncellemesi
            TxtGpuName.Text = hw.GpuName;
            TxtGpuUsage.Text = $"{hw.GpuUsage:F0}%";
            BarGpuUsage.Value = hw.GpuUsage;

            string gpuTempStr = hw.GpuTemp.HasValue ? $"{hw.GpuTemp.Value:F0}°C" : "N/A";
            TxtGpuTemp.Text = gpuTempStr;
            TxtGpuTemp.Foreground = GetTemperatureBrush(hw.GpuTemp);

            TxtCompactGpu.Text = $"{hw.GpuUsage:F0}%  {gpuTempStr}";

            // 4. Fan Hızı Güncellemesi
            TxtFanSpeed.Text = hw.FanDisplayText;
            TxtCompactFan.Text = hw.FanDisplayText;

            double fanBarValue = hw.FanControlPercent ?? Math.Clamp(hw.FanRpm / 30.0, 0, 100);
            BarFanSpeed.Value = fanBarValue;
        }
        finally
        {
            _isUpdatingMetrics = false;
        }
    }

    private async Task UpdatePingAsync()
    {
        if (_isHidden) return;

        TxtPingTarget.Text = _pingService.CurrentTargetAddress;
        long pingMs = await _pingService.MeasurePingAsync();

        if (pingMs >= 0)
        {
            TxtPingValue.Text = pingMs.ToString();
            TxtCompactPing.Text = $"{pingMs} ms";

            var pingBrush = pingMs < 45 ? BrushGood : (pingMs < 95 ? BrushWarning : BrushDanger);
            TxtPingValue.Foreground = pingBrush;
            TxtCompactPing.Foreground = pingBrush;
        }
        else
        {
            TxtPingValue.Text = "Yok";
            TxtCompactPing.Text = "Yok";
            TxtPingValue.Foreground = BrushDanger;
            TxtCompactPing.Foreground = BrushDanger;
        }
    }

    private static SolidColorBrush GetTemperatureBrush(float? temp)
    {
        if (!temp.HasValue) return BrushMuted;
        if (temp.Value >= 82f) return BrushDanger;
        if (temp.Value >= 68f) return BrushWarning;
        return BrushGood;
    }

    private void ToggleLockClickThrough()
    {
        _isLockedClickThrough = !_isLockedClickThrough;
        Win32Helper.SetClickThrough(this, _isLockedClickThrough);

        if (_isLockedClickThrough)
        {
            LockBadge.Visibility = Visibility.Visible;
            StatusDot.Fill = BrushWarning;
            HeaderButtonsPanel.Visibility = Visibility.Collapsed;
            FooterControlsPanel.Visibility = Visibility.Collapsed;
            CompactButtonsPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            LockBadge.Visibility = Visibility.Collapsed;
            StatusDot.Fill = BrushGood;
            HeaderButtonsPanel.Visibility = Visibility.Visible;
            FooterControlsPanel.Visibility = Visibility.Visible;
            CompactButtonsPanel.Visibility = Visibility.Visible;
        }
    }

    private void ToggleViewMode()
    {
        _isCompactMode = !_isCompactMode;
        if (_isCompactMode)
        {
            DetailedPanel.Visibility = Visibility.Collapsed;
            CompactPanel.Visibility = Visibility.Visible;
        }
        else
        {
            CompactPanel.Visibility = Visibility.Collapsed;
            DetailedPanel.Visibility = Visibility.Visible;
        }
    }

    private void ToggleVisibility()
    {
        _isHidden = !_isHidden;
        RootBorder.Visibility = _isHidden ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isLockedClickThrough && e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // Sürükleme hatasını yoksay
            }
        }
    }

    private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BackgroundBrush == null || TxtOpacityVal == null) return;

        double val = e.NewValue;
        byte alpha = (byte)Math.Clamp((int)(val * 255), 40, 255);
        BackgroundBrush.Color = Color.FromArgb(alpha, 0x0F, 0x13, 0x19);
        TxtOpacityVal.Text = $"{val * 100:F0}%";
    }

    private async void PingCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _pingService.CycleTarget();
        await UpdatePingAsync();
    }

    private void BtnLock_Click(object sender, RoutedEventArgs e)
    {
        ToggleLockClickThrough();
    }

    private void BtnToggleMode_Click(object sender, RoutedEventArgs e)
    {
        ToggleViewMode();
    }

    private void BtnAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                var startInfo = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(startInfo);
                Application.Current.Shutdown();
            }
        }
        catch
        {
            // Kullanıcı UAC penceresini iptal ederse devam et
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _metricsTimer.Stop();
        _pingTimer.Stop();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Win32Helper.UnregisterHotKey(hwnd, HOTKEY_ID_LOCK);
            Win32Helper.UnregisterHotKey(hwnd, HOTKEY_ID_MODE);
            Win32Helper.UnregisterHotKey(hwnd, HOTKEY_ID_HIDE);
        }

        _fpsService.Dispose();
        _hardwareService.Dispose();
        _pingService.Dispose();
    }
}
