using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace PCStatsWidget;

public partial class MainWindow
{
    internal Border RootBorder = null!;
    internal SolidColorBrush BackgroundBrush = null!;
    internal StackPanel DetailedPanel = null!;
    internal Ellipse StatusDot = null!;
    internal Border LockBadge = null!;
    internal StackPanel HeaderButtonsPanel = null!;
    internal Button BtnAdmin = null!;
    internal Button BtnCompactMode = null!;
    internal Button BtnLock = null!;
    internal TextBlock TxtFpsSource = null!;
    internal TextBlock TxtFpsValue = null!;
    internal TextBlock TxtPingTarget = null!;
    internal TextBlock TxtPingValue = null!;
    internal TextBlock TxtCpuName = null!;
    internal TextBlock TxtCpuUsage = null!;
    internal TextBlock TxtCpuTemp = null!;
    internal ProgressBar BarCpuUsage = null!;
    internal TextBlock TxtGpuName = null!;
    internal TextBlock TxtGpuUsage = null!;
    internal TextBlock TxtGpuTemp = null!;
    internal ProgressBar BarGpuUsage = null!;
    internal TextBlock TxtFanSpeed = null!;
    internal ProgressBar BarFanSpeed = null!;
    internal StackPanel FooterControlsPanel = null!;
    internal TextBlock TxtOpacityVal = null!;
    internal Slider SliderOpacity = null!;
    internal Border CompactPanel = null!;
    internal TextBlock TxtCompactFps = null!;
    internal TextBlock TxtCompactCpu = null!;
    internal TextBlock TxtCompactGpu = null!;
    internal TextBlock TxtCompactFan = null!;
    internal TextBlock TxtCompactPing = null!;
    internal StackPanel CompactButtonsPanel = null!;

    private bool _contentLoaded;

    public void InitializeComponent()
    {
        if (_contentLoaded) return;
        _contentLoaded = true;

        Title = "PC Stats Overlay Widget";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.NoResize;

        Loaded += Window_Loaded;
        Closing += Window_Closing;
        MouseLeftButtonDown += Window_MouseLeftButtonDown;

        BackgroundBrush = new SolidColorBrush(Color.FromArgb(0xD8, 0x0F, 0x13, 0x19));

        RootBorder = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(8),
            Background = BackgroundBrush,
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.55,
                Color = Colors.Black
            }
        };

        var rootGrid = new Grid();

        // ==================== 1. DETAYLI KART MODU ====================
        DetailedPanel = new StackPanel
        {
            Width = 295,
            Margin = new Thickness(14, 12, 14, 12)
        };

        // Üst Başlık ve Kontroller
        var headerDock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        StatusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76)),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        titleStack.Children.Add(StatusDot);

        titleStack.Children.Add(new TextBlock
        {
            Text = "PC STATS",
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF4, 0xF8)),
            VerticalAlignment = VerticalAlignment.Center
        });

        LockBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x98, 0x00)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(6, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "KİLİTLİ (F8)",
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D))
            }
        };
        titleStack.Children.Add(LockBadge);
        headerDock.Children.Add(titleStack);

        HeaderButtonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        BtnAdmin = CreateHeaderButton("🛡️ Yönetici", "Tüm anakart/sıcaklık sensörleri için Yönetici olarak yeniden başlat", BtnAdmin_Click);
        BtnAdmin.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
        BtnAdmin.Visibility = Visibility.Collapsed;

        BtnCompactMode = CreateHeaderButton("▬", "Yatay Oyun Şeridi Moduna Geç (F9)", BtnToggleMode_Click);
        BtnLock = CreateHeaderButton("🔓", "Tıklama Geçirgenliği / Hayalet Modu (F8)", BtnLock_Click);
        var btnClose = CreateHeaderButton("✕", "Kapat", BtnClose_Click);

        HeaderButtonsPanel.Children.Add(BtnAdmin);
        HeaderButtonsPanel.Children.Add(BtnCompactMode);
        HeaderButtonsPanel.Children.Add(BtnLock);
        HeaderButtonsPanel.Children.Add(btnClose);
        headerDock.Children.Add(HeaderButtonsPanel);

        DetailedPanel.Children.Add(headerDock);

        // FPS & PING Grid
        var topCardsGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        topCardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topCardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        topCardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // FPS Kartı
        var fpsCard = CreateCardBorder();
        var fpsStack = new StackPanel();
        var fpsTopDock = new DockPanel();
        fpsTopDock.Children.Add(new TextBlock
        {
            Text = "🎮 FPS",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
        });
        TxtFpsSource = new TextBlock
        {
            Text = "Ekran",
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        fpsTopDock.Children.Add(TxtFpsSource);
        fpsStack.Children.Add(fpsTopDock);

        var fpsValStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        TxtFpsValue = new TextBlock
        {
            Text = "60",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76))
        };
        fpsValStack.Children.Add(TxtFpsValue);
        fpsValStack.Children.Add(new TextBlock
        {
            Text = " FPS",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(2, 0, 0, 3)
        });
        fpsStack.Children.Add(fpsValStack);
        fpsCard.Child = fpsStack;
        Grid.SetColumn(fpsCard, 0);
        topCardsGrid.Children.Add(fpsCard);

        // PING Kartı
        var pingCard = CreateCardBorder();
        pingCard.Cursor = Cursors.Hand;
        pingCard.ToolTip = "Ping sunucusunu değiştirmek için tıklayın";
        pingCard.MouseLeftButtonDown += PingCard_MouseLeftButtonDown;

        var pingStack = new StackPanel();
        var pingTopDock = new DockPanel();
        pingTopDock.Children.Add(new TextBlock
        {
            Text = "🌐 PING",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
        });
        TxtPingTarget = new TextBlock
        {
            Text = "1.1.1.1",
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        pingTopDock.Children.Add(TxtPingTarget);
        pingStack.Children.Add(pingTopDock);

        var pingValStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        TxtPingValue = new TextBlock
        {
            Text = "--",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
        };
        pingValStack.Children.Add(TxtPingValue);
        pingValStack.Children.Add(new TextBlock
        {
            Text = " ms",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(2, 0, 0, 3)
        });
        pingStack.Children.Add(pingValStack);
        pingCard.Child = pingStack;
        Grid.SetColumn(pingCard, 2);
        topCardsGrid.Children.Add(pingCard);

        DetailedPanel.Children.Add(topCardsGrid);

        // CPU Kartı
        var cpuCard = CreateCardBorder(new Thickness(0, 0, 0, 8));
        var cpuStack = new StackPanel();
        var cpuDock = new DockPanel();
        var cpuLeft = new StackPanel { Orientation = Orientation.Horizontal };
        cpuLeft.Children.Add(new TextBlock
        {
            Text = "⚡ CPU",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
        });
        TxtCpuName = new TextBlock
        {
            Text = "",
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 115,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        cpuLeft.Children.Add(TxtCpuName);
        cpuDock.Children.Add(cpuLeft);

        var cpuRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        TxtCpuUsage = new TextBlock
        {
            Text = "0%",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC))
        };
        cpuRight.Children.Add(TxtCpuUsage);
        cpuRight.Children.Add(new TextBlock
        {
            Text = "  •  ",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
            VerticalAlignment = VerticalAlignment.Center
        });
        TxtCpuTemp = new TextBlock
        {
            Text = "--°C",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24))
        };
        cpuRight.Children.Add(TxtCpuTemp);
        cpuDock.Children.Add(cpuRight);
        cpuStack.Children.Add(cpuDock);

        BarCpuUsage = CreateMetricProgressBar(Color.FromRgb(0x38, 0xBD, 0xF8));
        cpuStack.Children.Add(BarCpuUsage);
        cpuCard.Child = cpuStack;
        DetailedPanel.Children.Add(cpuCard);

        // GPU Kartı
        var gpuCard = CreateCardBorder(new Thickness(0, 0, 0, 8));
        var gpuStack = new StackPanel();
        var gpuDock = new DockPanel();
        var gpuLeft = new StackPanel { Orientation = Orientation.Horizontal };
        gpuLeft.Children.Add(new TextBlock
        {
            Text = "🔥 GPU",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7))
        });
        TxtGpuName = new TextBlock
        {
            Text = "",
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 115,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        gpuLeft.Children.Add(TxtGpuName);
        gpuDock.Children.Add(gpuLeft);

        var gpuRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        TxtGpuUsage = new TextBlock
        {
            Text = "0%",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC))
        };
        gpuRight.Children.Add(TxtGpuUsage);
        gpuRight.Children.Add(new TextBlock
        {
            Text = "  •  ",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
            VerticalAlignment = VerticalAlignment.Center
        });
        TxtGpuTemp = new TextBlock
        {
            Text = "--°C",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24))
        };
        gpuRight.Children.Add(TxtGpuTemp);
        gpuDock.Children.Add(gpuRight);
        gpuStack.Children.Add(gpuDock);

        BarGpuUsage = CreateMetricProgressBar(Color.FromRgb(0xA8, 0x55, 0xF7));
        gpuStack.Children.Add(BarGpuUsage);
        gpuCard.Child = gpuStack;
        DetailedPanel.Children.Add(gpuCard);

        // FAN HIZI Kartı
        var fanCard = CreateCardBorder(new Thickness(0, 0, 0, 8));
        var fanStack = new StackPanel();
        var fanDock = new DockPanel();
        fanDock.Children.Add(new TextBlock
        {
            Text = "🌀 FAN HIZI",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF))
        });
        TxtFanSpeed = new TextBlock
        {
            Text = "0 RPM",
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        fanDock.Children.Add(TxtFanSpeed);
        fanStack.Children.Add(fanDock);

        BarFanSpeed = CreateMetricProgressBar(Color.FromRgb(0x2D, 0xD4, 0xBF));
        fanStack.Children.Add(BarFanSpeed);
        fanCard.Child = fanStack;
        DetailedPanel.Children.Add(fanCard);

        // Alt Kontrol Paneli (Saydamlık & Kısayollar)
        FooterControlsPanel = new StackPanel { Margin = new Thickness(2, 2, 2, 0) };
        var opacityDock = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        opacityDock.Children.Add(new TextBlock
        {
            Text = "Saydamlık",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        TxtOpacityVal = new TextBlock
        {
            Text = "85%",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        DockPanel.SetDock(TxtOpacityVal, Dock.Right);
        opacityDock.Children.Add(TxtOpacityVal);

        SliderOpacity = new Slider
        {
            Minimum = 0.25,
            Maximum = 1.0,
            Value = 0.85,
            VerticalAlignment = VerticalAlignment.Center
        };
        SliderOpacity.ValueChanged += SliderOpacity_ValueChanged;
        opacityDock.Children.Add(SliderOpacity);
        FooterControlsPanel.Children.Add(opacityDock);

        FooterControlsPanel.Children.Add(new TextBlock
        {
            Text = "F8: Hayalet Modu (Kilit)  •  F9: Mini Bar  •  F10: Gizle",
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });
        DetailedPanel.Children.Add(FooterControlsPanel);

        rootGrid.Children.Add(DetailedPanel);

        // ==================== 2. MİNİ YATAY OYUN ŞERİDİ MODU ====================
        CompactPanel = new Border
        {
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(12, 7, 12, 7)
        };
        var compactStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        compactStack.Children.Add(CreateCompactLabel("FPS ", Color.FromRgb(0x94, 0xA3, 0xB8)));
        TxtCompactFps = CreateCompactValue("60", Color.FromRgb(0x00, 0xE6, 0x76));
        compactStack.Children.Add(TxtCompactFps);
        compactStack.Children.Add(CreateCompactSeparator());

        compactStack.Children.Add(CreateCompactLabel("CPU ", Color.FromRgb(0x38, 0xBD, 0xF8)));
        TxtCompactCpu = CreateCompactValue("0%  --°C", Color.FromRgb(0xF8, 0xFA, 0xFC));
        compactStack.Children.Add(TxtCompactCpu);
        compactStack.Children.Add(CreateCompactSeparator());

        compactStack.Children.Add(CreateCompactLabel("GPU ", Color.FromRgb(0xA8, 0x55, 0xF7)));
        TxtCompactGpu = CreateCompactValue("0%  --°C", Color.FromRgb(0xF8, 0xFA, 0xFC));
        compactStack.Children.Add(TxtCompactGpu);
        compactStack.Children.Add(CreateCompactSeparator());

        compactStack.Children.Add(CreateCompactLabel("FAN ", Color.FromRgb(0x2D, 0xD4, 0xBF)));
        TxtCompactFan = CreateCompactValue("0 RPM", Color.FromRgb(0xF8, 0xFA, 0xFC));
        compactStack.Children.Add(TxtCompactFan);
        compactStack.Children.Add(CreateCompactSeparator());

        compactStack.Children.Add(CreateCompactLabel("PING ", Color.FromRgb(0x94, 0xA3, 0xB8)));
        TxtCompactPing = CreateCompactValue("-- ms", Color.FromRgb(0x38, 0xBD, 0xF8));
        compactStack.Children.Add(TxtCompactPing);

        CompactButtonsPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        CompactButtonsPanel.Children.Add(CreateHeaderButton("▤", "Detaylı Görünüme Dön (F9)", BtnToggleMode_Click));
        CompactButtonsPanel.Children.Add(CreateHeaderButton("🔓", "Hayalet Modu (F8)", BtnLock_Click));
        compactStack.Children.Add(CompactButtonsPanel);

        CompactPanel.Child = compactStack;
        rootGrid.Children.Add(CompactPanel);

        RootBorder.Child = rootGrid;
        Content = RootBorder;
    }

    private static Border CreateCardBorder(Thickness margin = default)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = margin,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };
    }

    private static ProgressBar CreateMetricProgressBar(Color fgColor)
    {
        return new ProgressBar
        {
            Height = 5,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(fgColor),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 6, 0, 0)
        };
    }

    private static Button CreateHeaderButton(string content, string tooltip, RoutedEventHandler onClick)
    {
        var btn = new Button
        {
            Content = content,
            ToolTip = tooltip,
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xED)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(3, 0, 0, 0),
            FontSize = 11,
            Cursor = Cursors.Hand
        };
        btn.Click += onClick;
        return btn;
    }

    private static TextBlock CreateCompactLabel(string text, Color color)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static TextBlock CreateCompactValue(string text, Color color)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static TextBlock CreateCompactSeparator()
    {
        return new TextBlock
        {
            Text = "   |   ",
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
            VerticalAlignment = VerticalAlignment.Center
        };
    }
}
