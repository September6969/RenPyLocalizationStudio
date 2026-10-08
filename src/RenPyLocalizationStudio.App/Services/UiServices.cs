using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.App.Services;

[System.Windows.Data.ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBooleanToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return value is not Visibility.Visible;
    }
}

public interface IFileDialogService
{
    string? SelectProjectFolder(string? initialDirectory);
    string? SelectSdkExecutable(string? initialPath);
}

public interface IConfirmationService
{
    bool Confirm(string title, string message, MessageBoxImage image = MessageBoxImage.Question);
    void ShowDiagnostics(string title, IEnumerable<Diagnostic> diagnostics);
    void ShowMessage(string title, string message, MessageBoxImage image = MessageBoxImage.Information);
}

public interface IThemeService
{
    string AccentColor { get; }
    bool TryApplyAccent(string color, out string? error);
}

public sealed class FileDialogService : IFileDialogService
{
    public string? SelectProjectFolder(string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含 game 目录的 Ren’Py 项目",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? SelectSdkExecutable(string? initialPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Ren'Py SDK 可执行文件",
            Filter = "Ren'Py 可执行文件 (renpy.exe)|renpy.exe|所有可执行文件 (*.exe)|*.exe",
            InitialDirectory = File.Exists(initialPath) ? Path.GetDirectoryName(initialPath) :
                               Directory.Exists(initialPath) ? initialPath : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

public static class ModernDialog
{
    public static bool ShowConfirm(string title, string message, MessageBoxImage image = MessageBoxImage.Question)
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            var win = new Window
            {
                Title = title,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current.MainWindow,
                MinWidth = 360,
                MaxWidth = 480
            };

            bool result = false;

            var border = new System.Windows.Controls.Border
            {
                Background = (Brush)Application.Current.Resources["BackgroundLevel1"],
                BorderBrush = (Brush)Application.Current.Resources["BackgroundLevel2"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20),
                Margin = new Thickness(12),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24,
                    Opacity = 0.55,
                    ShadowDepth = 4,
                    Color = Colors.Black
                }
            };

            var rootGrid = new System.Windows.Controls.Grid();
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            var headerPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

            string iconGlyph = image switch
            {
                MessageBoxImage.Question => "\uE897",
                MessageBoxImage.Error => "\uEA39",
                MessageBoxImage.Warning => "\uE7BA",
                _ => "\uE946"
            };
            Brush iconBrush = image switch
            {
                MessageBoxImage.Error => (Brush)Application.Current.Resources["SemanticErrorBrush"],
                MessageBoxImage.Warning => (Brush)Application.Current.Resources["SemanticWarningBrush"],
                _ => (Brush)Application.Current.Resources["BrandAccentBrush"]
            };

            headerPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = iconGlyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 18,
                Foreground = iconBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            headerPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });

            System.Windows.Controls.Grid.SetRow(headerPanel, 0);
            rootGrid.Children.Add(headerPanel);

            var msgBlock = new System.Windows.Controls.TextBlock
            {
                Text = message,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Margin = new Thickness(0, 0, 0, 20)
            };
            System.Windows.Controls.Grid.SetRow(msgBlock, 1);
            rootGrid.Children.Add(msgBlock);

            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            var yesBtn = new System.Windows.Controls.Button
            {
                Content = "确定",
                Style = (Style)Application.Current.Resources["PrimaryButtonStyle"],
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = true
            };
            yesBtn.Click += (_, _) => { result = true; win.Close(); };

            var noBtn = new System.Windows.Controls.Button
            {
                Content = "取消",
                Style = (Style)Application.Current.Resources["GhostButtonStyle"],
                Padding = new Thickness(14, 6, 14, 6),
                IsCancel = true
            };
            noBtn.Click += (_, _) => { result = false; win.Close(); };

            btnPanel.Children.Add(noBtn);
            btnPanel.Children.Add(yesBtn);

            System.Windows.Controls.Grid.SetRow(btnPanel, 2);
            rootGrid.Children.Add(btnPanel);

            border.Child = rootGrid;
            win.Content = border;
            win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };

            win.ShowDialog();
            return result;
        });
    }

    public static void ShowAlert(string title, string message, MessageBoxImage image = MessageBoxImage.Information)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var win = new Window
            {
                Title = title,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current.MainWindow,
                MinWidth = 360,
                MaxWidth = 480
            };

            var border = new System.Windows.Controls.Border
            {
                Background = (Brush)Application.Current.Resources["BackgroundLevel1"],
                BorderBrush = (Brush)Application.Current.Resources["BackgroundLevel2"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20),
                Margin = new Thickness(12),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24,
                    Opacity = 0.55,
                    ShadowDepth = 4,
                    Color = Colors.Black
                }
            };

            var rootGrid = new System.Windows.Controls.Grid();
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            var headerPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

            string iconGlyph = image switch
            {
                MessageBoxImage.Error => "\uEA39",
                MessageBoxImage.Warning => "\uE7BA",
                _ => "\uE946"
            };
            Brush iconBrush = image switch
            {
                MessageBoxImage.Error => (Brush)Application.Current.Resources["SemanticErrorBrush"],
                MessageBoxImage.Warning => (Brush)Application.Current.Resources["SemanticWarningBrush"],
                _ => (Brush)Application.Current.Resources["BrandAccentBrush"]
            };

            headerPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = iconGlyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 18,
                Foreground = iconBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            headerPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });

            System.Windows.Controls.Grid.SetRow(headerPanel, 0);
            rootGrid.Children.Add(headerPanel);

            var msgBlock = new System.Windows.Controls.TextBlock
            {
                Text = message,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Margin = new Thickness(0, 0, 0, 20)
            };
            System.Windows.Controls.Grid.SetRow(msgBlock, 1);
            rootGrid.Children.Add(msgBlock);

            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            var okBtn = new System.Windows.Controls.Button
            {
                Content = "我知道了",
                Style = (Style)Application.Current.Resources["PrimaryButtonStyle"],
                Padding = new Thickness(16, 6, 16, 6),
                IsDefault = true,
                IsCancel = true
            };
            okBtn.Click += (_, _) => win.Close();

            btnPanel.Children.Add(okBtn);
            System.Windows.Controls.Grid.SetRow(btnPanel, 2);
            rootGrid.Children.Add(btnPanel);

            border.Child = rootGrid;
            win.Content = border;
            win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };

            win.ShowDialog();
        });
    }
}

public sealed class ConfirmationService : IConfirmationService
{
    public bool Confirm(string title, string message, MessageBoxImage image = MessageBoxImage.Question) =>
        ModernDialog.ShowConfirm(title, message, image);

    public void ShowDiagnostics(string title, IEnumerable<Diagnostic> diagnostics)
    {
        var items = diagnostics.ToArray();
        var message = items.Length == 0
            ? "操作未完成。"
            : string.Join(Environment.NewLine, items.Take(12).Select(x => $"[{x.Code}] {x.Message}"));
        ModernDialog.ShowAlert(title, message,
            items.Any(x => x.Severity == DiagnosticSeverity.Error) ? MessageBoxImage.Error : MessageBoxImage.Information);
    }

    public void ShowMessage(string title, string message, MessageBoxImage image = MessageBoxImage.Information) =>
        ModernDialog.ShowAlert(title, message, image);
}

public sealed class ThemeService : IThemeService
{
    private System.Windows.Threading.DispatcherTimer? _colorTimer;
    private Color _startColor;
    private Color _targetColor;
    private DateTime _animStartTime;
    private const double DurationMs = 220.0;
    public const string DefaultAccentColor = "#D16BA5";

    public string AccentColor { get; private set; } = DefaultAccentColor;

    public bool TryApplyAccent(string color, out string? error)
    {
        error = null;
        if (!TryParseAccentColor(color, out var accent))
        {
            error = "自定义强调色应使用 #RRGGBB。";
            return false;
        }

        AccentColor = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        var resources = Application.Current.Resources;

        Color fromColor = Colors.Transparent;
        if (resources["BrandAccentBrush"] is SolidColorBrush curBrush)
        {
            fromColor = curBrush.Color;
        }
        else
        {
            fromColor = accent;
        }

        if (fromColor == accent)
        {
            ApplyInstantColor(accent);
            return true;
        }

        _colorTimer?.Stop();
        _startColor = fromColor;
        _targetColor = accent;
        _animStartTime = DateTime.UtcNow;

        _colorTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _colorTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - _animStartTime).TotalMilliseconds;
            var progress = Math.Clamp(elapsed / DurationMs, 0.0, 1.0);

            // 使用三次缓出，避免颜色切换突然跳变。
            double ease = 1.0 - Math.Pow(1.0 - progress, 3);

            byte r = (byte)(_startColor.R + (_targetColor.R - _startColor.R) * ease);
            byte g = (byte)(_startColor.G + (_targetColor.G - _startColor.G) * ease);
            byte b = (byte)(_startColor.B + (_targetColor.B - _startColor.B) * ease);

            var stepColor = Color.FromRgb(r, g, b);
            ApplyInstantColor(stepColor);

            if (progress >= 1.0)
            {
                _colorTimer?.Stop();
                _colorTimer = null;
            }
        };
        _colorTimer.Start();
        return true;
    }

    internal static bool TryParseAccentColor(string? value, out Color color)
    {
        color = default;
        if (value is null || value.Length != 7 || value[0] != '#') return false;
        if (!byte.TryParse(value.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var red) ||
            !byte.TryParse(value.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var green) ||
            !byte.TryParse(value.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue)) return false;
        color = Color.FromRgb(red, green, blue);
        return true;
    }

    private static void ApplyInstantColor(Color c)
    {
        var resources = Application.Current.Resources;
        var main = new SolidColorBrush(c); main.Freeze();
        var hover = new SolidColorBrush(Blend(c, Colors.White, 0.16)); hover.Freeze();
        var soft = new SolidColorBrush(Color.FromArgb(45, c.R, c.G, c.B)); soft.Freeze();
        resources["BrandAccentBrush"] = main;
        resources["BrandAccentHoverBrush"] = hover;
        resources["BrandAccentSoftBrush"] = soft;
    }

    private static Color Blend(Color source, Color target, double amount) => Color.FromRgb(
        (byte)(source.R + (target.R - source.R) * amount),
        (byte)(source.G + (target.G - source.G) * amount),
        (byte)(source.B + (target.B - source.B) * amount));
}

/// <summary>
/// 应用级会话设置。新增字段均带默认值，确保旧版本生成的 settings.json 仍可读取。
/// </summary>
public sealed record AppSettings(
    string? LastProject,
    string? LastLanguage,
    string AccentColor,
    string? SdkPath = null,
    bool AutoSaveEnabled = false,
    string? LastTranslationItemId = null,
    string? LastTranslationViewMode = null,
    string? LastTranslationGroupingMode = null,
    IReadOnlyList<TranslationWorkspaceState>? TranslationWorkspaces = null);

public sealed class AppSettingsStore
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _settingsPath;
    private readonly string _legacySettingsPath;

    public AppSettingsStore() : this(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenPyLocalizationStudio", "settings.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenPyFlowTranslator", "settings.json"))
    {
    }

    internal AppSettingsStore(string settingsPath, string? legacySettingsPath = null)
    {
        _settingsPath = settingsPath;
        _legacySettingsPath = legacySettingsPath ?? settingsPath;
    }

    public async Task<OperationResult<AppSettings>> LoadAsync(CancellationToken cancellationToken)
    {
        var defaults = new AppSettings(null, null, "#D16BA5");
        try
        {
            var path = File.Exists(_settingsPath) ? _settingsPath : _legacySettingsPath;
            if (!File.Exists(path)) return OperationResult<AppSettings>.Success(defaults);
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, cancellationToken: cancellationToken);
            if (settings is null)
                return new OperationResult<AppSettings>(OperationStatus.Failed, defaults,
                    [SettingsDiagnostic("SETTINGS_EMPTY", "设置文件内容为空，已使用默认设置。", path)]);
            return OperationResult<AppSettings>.Success(settings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException exception)
        {
            return new OperationResult<AppSettings>(OperationStatus.Failed, defaults,
                [SettingsDiagnostic("SETTINGS_JSON_INVALID", "设置文件 JSON 已损坏，已使用默认设置。", _settingsPath, exception.Message)]);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new OperationResult<AppSettings>(OperationStatus.Failed, defaults,
                [SettingsDiagnostic("SETTINGS_ACCESS_DENIED", "没有权限读取设置文件，已使用默认设置。", _settingsPath, exception.Message)]);
        }
        catch (IOException exception)
        {
            return new OperationResult<AppSettings>(OperationStatus.Failed, defaults,
                [SettingsDiagnostic("SETTINGS_READ_FAILED", "读取设置文件失败，已使用默认设置。", _settingsPath, exception.Message)]);
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        var temporaryPath = _settingsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, _settingsPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            _writeLock.Release();
        }
    }

    private static Diagnostic SettingsDiagnostic(string code, string message, string path, string? details = null) =>
        new(DiagnosticSeverity.Warning, code, message, path, Category: DiagnosticCategory.FileSystem, TechnicalDetails: details);
}
