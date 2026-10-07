using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using RenPyLocalizationStudio.App.ViewModels;

namespace RenPyLocalizationStudio.App;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HtMaxButton = 9;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private readonly MainViewModel _viewModel;
    private HwndSource? _source;
    private bool? _sidebarExpanded;
    private double? _inspectorWidth;
    private bool _shutdownInProgress;
    private bool _shutdownComplete;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.CurrentWorkspace))
            OnWorkspaceChanged();
    }

    private void OnWorkspaceChanged()
    {
        UpdateResponsiveLayout(ActualWidth);
        if (!IsLoaded) return;
        AnimateSlideIn(SidebarHost, fromX: -16, fromY: 0);
        AnimateSlideIn(MainHost, fromX: 0, fromY: 10);
        if (InspectorHost.Visibility == Visibility.Visible)
            AnimateSlideIn(InspectorHost, fromX: 16, fromY: 0);
    }

    private static readonly System.Windows.Media.Animation.CubicEase SharedEaseOut = new() { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

    private static void AnimateSlideIn(FrameworkElement element, double fromX = 0, double fromY = 0, double durationMs = 180)
    {
        if (element == null) return;
        var tt = new System.Windows.Media.TranslateTransform(fromX, fromY);
        element.RenderTransform = tt;
        element.Opacity = 0;

        var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = SharedEaseOut };
        element.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

        if (fromX != 0)
        {
            var slideAnim = new System.Windows.Media.Animation.DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = SharedEaseOut };
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slideAnim);
        }
        if (fromY != 0)
        {
            var slideAnim = new System.Windows.Media.Animation.DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = SharedEaseOut };
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

        var container = ActivityBarList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        var targetY = container is null
            ? index * 50d + 2d
            : container.TranslatePoint(new Point(0, 0), ActivityBarList).Y +
              Math.Max(0, (container.ActualHeight - ActivitySelectionIndicator.Height) / 2d);

        if (ActivitySelectionIndicator.Opacity == 0)
        {
            IndicatorTransform.Y = targetY;
            var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = SharedEaseOut
            };
            ActivitySelectionIndicator.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            return;
        }

        ActivitySelectionIndicator.Opacity = 1;

        if (animate && IsLoaded)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(targetY, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = SharedEaseOut
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
        try
        {
            await _viewModel.InitializeAsync();
            UpdateActivityIndicator(animate: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] InitializeAsync failed: {ex}");
            _viewModel.Tasks.StatusMessage = $"初始化失败：{ex.Message}";
            _viewModel.Tasks.Logs.Add(new ToolLogEntry(_viewModel.Tasks.StatusMessage, "Error"));
        }
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
        if (message == WmGetMinMaxInfo)
        {
            ConstrainMaximizedWindowToWorkArea(hwnd, lParam);
            handled = true;
            return IntPtr.Zero;
        }

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

    private static void ConstrainMaximizedWindowToWorkArea(IntPtr windowHandle, IntPtr minMaxInfoPointer)
    {
        var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitorHandle == IntPtr.Zero) return;

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitorHandle, ref monitorInfo)) return;

        var monitor = monitorInfo.Monitor.ToPixelRectangle();
        var workArea = monitorInfo.WorkArea.ToPixelRectangle();
        var bounds = MaximizedWindowBounds.Calculate(monitor, workArea);
        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(minMaxInfoPointer);
        minMaxInfo.MaxPosition.X = bounds.Position.X;
        minMaxInfo.MaxPosition.Y = bounds.Position.Y;
        minMaxInfo.MaxSize.X = bounds.Size.Width;
        minMaxInfo.MaxSize.Y = bounds.Size.Height;
        Marshal.StructureToPtr(minMaxInfo, minMaxInfoPointer, false);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownInProgress) return;

        _shutdownInProgress = true;
        var canClose = false;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            canClose = await _viewModel.ShutdownAsync(cancellation.Token);
        }
        catch (Exception exception)
        {
            _viewModel.Tasks.Logs.Add(new ToolLogEntry($"关闭前保存失败：{exception.Message}", "Warning"));
            _viewModel.Tasks.StatusMessage = "关闭已中止：后台任务或保存尚未安全结束。";
        }
        finally
        {
            _shutdownInProgress = false;
        }

        if (canClose)
        {
            _shutdownComplete = true;
            Close();
        }
    }

    private void InspectorHost_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => UpdateResponsiveLayout(ActualWidth);

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        if (!IsInitialized) return;
        var layout = WorkspacePaneLayout.Calculate(width, _viewModel.CurrentWorkspace?.IsInspectorVisible == true);
        var showSidebar = layout.SidebarWidth > 0;
        var showInspector = layout.InspectorWidth > 0;
        var targetSidebarWidth = layout.SidebarWidth;
        var targetInspectorWidth = layout.InspectorWidth;

        if (_sidebarExpanded != showSidebar)
        {
            _sidebarExpanded = showSidebar;
            if (IsLoaded) AnimateWidth(SidebarHost, targetSidebarWidth);
            else SidebarHost.Width = targetSidebarWidth;
        }
        if (_inspectorWidth != targetInspectorWidth)
        {
            _inspectorWidth = targetInspectorWidth;
            if (IsLoaded) AnimateWidth(InspectorHost, targetInspectorWidth);
            else InspectorHost.Width = targetInspectorWidth;
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
            EasingFunction = SharedEaseOut
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
        if (HandleTranslationShortcut(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }

        // Alt+←/→ 保留给文本编辑器的单词移动；在编辑器之外拦截，避免被误当成窗口/历史导航。
        var pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) &&
            (pressedKey is Key.Left or Key.Right) &&
            !IsTextEditorFocused(Keyboard.FocusedElement as DependencyObject ?? e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.System || e.SystemKey != Key.Space) return;
        var point = PointToScreen(new Point(0, 40));
        SystemCommands.ShowSystemMenu(this, point);
        e.Handled = true;
    }

    internal bool HandleTranslationShortcut(Key key, ModifierKeys modifiers)
    {
        // 只作用于翻译工作区，避免在补丁文档中误保存另一类内容。
        if (_viewModel.CurrentWorkspace != _viewModel.TranslationWorkspace) return false;
        if (modifiers == ModifierKeys.Control && key == Key.F)
        {
            FindVisualChild<Views.TranslationMainView>(MainHost)?.FocusSearch();
            return true;
        }
        ICommand? command = modifiers == ModifierKeys.Control && key == Key.S
            ? _viewModel.Session.SaveCommand
            : modifiers == ModifierKeys.None && key == Key.F8
                ? _viewModel.TranslationWorkspace.MoveNextPendingCommand : null;
        if (command is null) return false;
        if (command.CanExecute(null)) command.Execute(null);
        return true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private static bool IsTextEditorFocused(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is TextEditor or TextBoxBase) return true;
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element) => element switch
    {
        Visual visual => VisualTreeHelper.GetParent(visual),
        FrameworkContentElement content => content.Parent,
        _ => null
    };

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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly PixelRectangle ToPixelRectangle() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }
}
