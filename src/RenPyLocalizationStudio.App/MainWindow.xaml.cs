using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using RenPyLocalizationStudio.App.ViewModels;

namespace RenPyLocalizationStudio.App;

public partial class MainWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HtMaxButton = 9;
    private readonly MainViewModel _viewModel;
    private HwndSource? _source;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CurrentWorkspace))
            {
                OnWorkspaceChanged();
            }
        };
    }

    private void OnWorkspaceChanged()
    {
        if (!IsLoaded) return;
        AnimateSlideIn(SidebarHost, fromX: -16, fromY: 0);
        AnimateSlideIn(MainHost, fromX: 0, fromY: 10);
        if (InspectorHost.Visibility == Visibility.Visible)
            AnimateSlideIn(InspectorHost, fromX: 16, fromY: 0);
    }

    private static void AnimateSlideIn(FrameworkElement element, double fromX = 0, double fromY = 0, double durationMs = 180)
    {
        if (element == null) return;
        var tt = new System.Windows.Media.TranslateTransform(fromX, fromY);
        element.RenderTransform = tt;
        element.Opacity = 0;

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = ease };
        element.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

        if (fromX != 0)
        {
            var slideAnim = new System.Windows.Media.Animation.DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = ease };
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slideAnim);
        }
        if (fromY != 0)
        {
            var slideAnim = new System.Windows.Media.Animation.DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = ease };
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slideAnim);
        }
    }

    private void ActivityBarList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateActivityIndicator(animate: true);
    }

    private void UpdateActivityIndicator(bool animate = true)
    {
        if (ActivityBarList == null || ActivitySelectionIndicator == null) return;
        int index = ActivityBarList.SelectedIndex;
        if (index < 0)
        {
            ActivitySelectionIndicator.Opacity = 0;
            return;
        }

        double targetY = index * 50 + 2;

        if (ActivitySelectionIndicator.Opacity == 0)
        {
            IndicatorTransform.Y = targetY;
            var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            ActivitySelectionIndicator.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            return;
        }

        ActivitySelectionIndicator.Opacity = 1;

        if (animate && IsLoaded)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(targetY, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            IndicatorTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, anim);
        }
        else
        {
            IndicatorTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            IndicatorTransform.Y = targetY;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        UpdateActivityIndicator(animate: false);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        TryEnableMica();
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowMessageHook);
        UpdateResponsiveLayout(ActualWidth);
        UpdateMaximizePresentation();
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmNcLeftButtonDown && wParam.ToInt32() == HtMaxButton)
        {
            ToggleMaximizeRestore();
            handled = true;
            return IntPtr.Zero;
        }
        if (message != WmNcHitTest || !MaximizeButton.IsVisible) return IntPtr.Zero;

        var packed = lParam.ToInt64();
        var screenPoint = new Point(unchecked((short)(packed & 0xFFFF)), unchecked((short)((packed >> 16) & 0xFFFF)));
        var buttonPoint = MaximizeButton.PointFromScreen(screenPoint);
        if (buttonPoint.X < 0 || buttonPoint.Y < 0 || buttonPoint.X > MaximizeButton.ActualWidth || buttonPoint.Y > MaximizeButton.ActualHeight)
            return IntPtr.Zero;

        handled = true;
        return new IntPtr(HtMaxButton);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        if (!IsInitialized) return;
        var showSidebar = width >= 900;
        var showInspector = width >= 1180;
        double targetSidebarWidth = showSidebar ? 260 : 0;
        double targetInspectorWidth = showInspector ? 360 : 0;

        if (IsLoaded)
        {
            AnimateWidth(SidebarHost, targetSidebarWidth);
            AnimateWidth(InspectorHost, targetInspectorWidth);
        }
        else
        {
            SidebarHost.Width = targetSidebarWidth;
            InspectorHost.Width = targetInspectorWidth;
        }

        SidebarDividerColumn.Width = showSidebar ? new GridLength(1) : new GridLength(0);
        InspectorDividerColumn.Width = showInspector ? new GridLength(1) : new GridLength(0);

        FullProductName.Visibility = width < 820 ? Visibility.Collapsed : Visibility.Visible;
        if (LanguagePanel != null) LanguagePanel.Visibility = width < 750 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void AnimateWidth(FrameworkElement element, double targetWidth, double durationMs = 180)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        element.BeginAnimation(FrameworkElement.WidthProperty, anim);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateMaximizePresentation();

    private void UpdateMaximizePresentation()
    {
        if (!IsInitialized) return;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "还原" : "最大化");
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
        => ToggleMaximizeRestore();

    private void ToggleMaximizeRestore()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.System || e.SystemKey != Key.Space) return;
        var point = PointToScreen(new Point(0, 40));
        SystemCommands.ShowSystemMenu(this, point);
        e.Handled = true;
    }

    private void TryEnableMica()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return;
        try
        {
            var backdrop = 2;
            var dark = 1;
            var handle = new WindowInteropHelper(this).Handle;
            _ = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        catch (Exception)
        {
            // Mica 仅为渐进增强，失败时继续使用纯色背景。
        }
    }

    private void PresetColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string color)
        {
            if (_viewModel.ApplyAccentCommand.CanExecute(color))
            {
                _viewModel.ApplyAccentCommand.Execute(color);
            }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
