using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RenPyLocalizationStudio.App.Services;

internal static class WindowThemeHelper
{
    public static void ApplyDarkChrome(Window window, bool mica = false)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        // 高对比度下保留系统标题栏及背景，装饰效果失败时继续使用纯色窗口。
        var dark = SystemParameters.HighContrast ? 0 : 1;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            var backdrop = mica && !SystemParameters.HighContrast ? 2 : 1;
            _ = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
        }
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
