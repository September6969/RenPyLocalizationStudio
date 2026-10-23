using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Messaging;
using ICSharpCode.AvalonEdit;
using RenPyLocalizationStudio.App;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.App.Views;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

internal static class Program
{
    private static string OutputDirectory = "";
    [STAThread]
    private static void Main(string[] args)
    {
        OutputDirectory = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-verification");
        Directory.CreateDirectory(OutputDirectory);
        var app = new Application();
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        app.Resources["StudioUiFontFamily"] = new FontFamily("Segoe UI, Microsoft YaHei UI");
        app.Resources["StudioReadingFontFamily"] = new FontFamily(new Uri("pack://application:,,,/RenPyLocalizationStudio;component/"), "./Assets/Fonts/#LXGW WenKai");
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/RenPyLocalizationStudio;component/Themes/StudioTheme.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/RenPyLocalizationStudio;component/Themes/WorkspaceTemplates.xaml") });
        var fs = new FileSystemService();
        var saves = new Saves();
        var session = new ProjectSessionViewModel(new Analysis(), new ProjectCatalogService(fs), saves, new Dialog(), new Confirmation(), new AppSettingsStore(Path.Combine(OutputDirectory, "settings.json")), new Theme(), new TaskCenterViewModel());
        using var workspace = new TranslationWorkspaceViewModel(session, new Preview());
        var view = new TranslationMainView { DataContext = workspace.Main };
        var sidebar = new TranslationSidebarView { DataContext = workspace.Sidebar };
        var grid = new Grid { Background = (Brush)app.Resources["BackgroundLevel0"] };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(sidebar);
        grid.Children.Add(view);
        Grid.SetColumn(view, 1);
        Render(grid, "empty", 1080);
        session.ProjectPath = Path.Combine(OutputDirectory, "SdkProject");
        session.Language = "schinese";
        Wait(session.AnalyzeAsync());
        workspace.ViewMode = TranslationViewMode.Strings;
        Render(grid, "expanded", 1080);
        ((ToggleButton)view.FindName("PreviewCollapseToggle")).IsChecked = false;
        var previewRow = (RowDefinition)view.FindName("PreviewRow");
        var flowRow = (RowDefinition)view.FindName("FlowRow");
        previewRow.Height = new GridLength(170);
        flowRow.Height = new GridLength(1, GridUnitType.Star);
        Render(grid, "resized-before-collapse", 1080);
        var toggle = (ToggleButton)view.FindName("PreviewCollapseToggle");
        toggle.IsChecked = true;
        Render(grid, "collapsed", 1080);
        if (previewRow.ActualHeight != 36 || !workspace.ImagePreview.IsCollapsed) throw new Exception("预览收起失败");
        toggle.IsChecked = false;
        Render(grid, "restored", 1080);
        if (Math.Abs(previewRow.ActualHeight - 170) > 2 || workspace.ImagePreview.IsCollapsed) throw new Exception($"分栏比例恢复失败 preview={previewRow.ActualHeight}/{previewRow.Height} flow={flowRow.ActualHeight}/{flowRow.Height} prefs={workspace.UiLayoutPreferences} collapsed={workspace.ImagePreview.IsCollapsed}");
        workspace.SearchText = "不存在";
        Render(grid, "search-empty", 1080);
        Render(grid, "narrow", 820);
        Console.WriteLine("Rendered six states; collapse and resized-row restoration verified.");
        workspace.UpdatePreviewPreferences(true, 0.4);
        var runner = new ProcessRunnerService();
        var confirmation = new Confirmation();
        var dialogs = new Dialog();
        var theme = new Theme();
        var coordinator = new WorkspaceTaskCoordinator(_ => { });
        var messenger = new WeakReferenceMessenger();
        var patchService = new ManagedPatchService(fs);
        var tl = new TlWorkspaceViewModel(session, new RenPySdkService(fs, runner), confirmation, dialogs);
        var extra = new ExtraTextWorkspaceViewModel(session, fs, new ExtraTextScanService(fs), patchService, confirmation);
        var patch = new PatchWorkspaceViewModel(session, fs, patchService, new ReplacementRuleService(), confirmation);
        var archive = new ArchiveWorkspaceViewModel(session, fs, new ArchiveExtractionService(fs, runner), new ScriptDecompilerService(fs, runner), new PrefixRenameService(fs), new ImageCompressionService(fs), confirmation);
        var diagnostics = new DiagnosticsWorkspaceViewModel(session, messenger);
        using var main = new MainViewModel(session, session.Tasks, theme, coordinator, messenger, workspace, tl, extra, patch, archive, diagnostics);
        var window = new MainWindow(main);
        workspace.SearchText = "";
        workspace.SelectedItem = workspace.VisibleItems.First();
        foreach (var width in new[] { 720, 900, 1180, 1500 })
        {
            typeof(MainWindow).GetMethod("UpdateResponsiveLayout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [(double)width]);
            Render((FrameworkElement)window.Content, $"window-{width}", width);
            var inspectorHost = (ContentControl)window.FindName("InspectorHost");
            if (inspectorHost.ActualWidth < 280) throw new Exception($"窄窗口检查器不可用 width={width},host={inspectorHost.ActualWidth}, column={((ColumnDefinition)window.FindName("InspectorColumn")).Width},visible={inspectorHost.Visibility}");
        }
        var root = (FrameworkElement)window.Content;
        var sidebarSplitter = (GridSplitter)window.FindName("SidebarSplitter");
        sidebarSplitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        sidebarSplitter.RaiseEvent(new DragDeltaEventArgs(60, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        Render(root, "sidebar-dragged", 1500);
        sidebarSplitter.RaiseEvent(new DragCompletedEventArgs(60, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        if (session.UiLayout.SidebarWidth <= 260) throw new Exception("侧栏拖动未保存首选宽度");
        var inspectorSplitter = (GridSplitter)window.FindName("InspectorSplitter");
        inspectorSplitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        inspectorSplitter.RaiseEvent(new DragDeltaEventArgs(-40, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        Render(root, "inspector-dragged", 1500);
        inspectorSplitter.RaiseEvent(new DragCompletedEventArgs(-40, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        if (session.UiLayout.InspectorWidth <= 360) throw new Exception("检查器拖动未保存首选宽度");
        session.UpdateUiLayout(new());
        Render(root, "layout-restored", 1500);
        Console.WriteLine("Real GridSplitter drag events resize both panels and update preferences.");
        var reuse = Descendants<Expander>(root).Single(expander => expander.Header?.ToString()?.StartsWith("项目内相同原文") == true);
        reuse.IsExpanded = true;
        Render(root, "translation-reuse", 1500);
        var reuseButton = Descendants<Button>(root).First(button => button.DataContext is TranslationSuggestion);
        var reuseEditor = (TextEditor)reuseButton.Tag;
        reuseEditor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        reuseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (workspace.Inspector.TranslationText != ((TranslationSuggestion)reuseButton.DataContext).Translation) throw new Exception("译文复用按钮未生效");
        reuseEditor.Undo();
        if (workspace.Inspector.TranslationText.Length != 0) throw new Exception("译文复用撤销失败");
        typeof(MainWindow).GetMethod("UpdateResponsiveLayout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [720d]);
        Render(root, "translation-reuse-narrow", 720);
        typeof(MainWindow).GetMethod("UpdateResponsiveLayout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [1500d]);
        var tokenButton = Descendants<Button>(root).First(button => button.DataContext as string == "[name]" && button.Tag is TextEditor);
        var editor = (TextEditor)tokenButton.Tag;
        editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        editor.Text = "你好，姓名！";
        editor.Select(3, 2);
        tokenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (workspace.Inspector.TranslationText != "你好，[name]！") throw new Exception("占位符按钮未同步编辑器");
        editor.Undo();
        if (workspace.Inspector.TranslationText != "你好，姓名！") throw new Exception("占位符按钮撤销失败");
        window.HandleTranslationShortcut(Key.S, ModifierKeys.Control);
        Wait(session.SaveCommand.ExecutionTask!);
        if (saves.Count != 1) throw new Exception("Ctrl+S 未调用保存");
        var busySignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = session.Tasks.RunAsync("只读验证", _ => busySignal.Task);
        Render(root, "window-busy", 1500);
        if (tokenButton.IsEnabled || !editor.IsReadOnly) throw new Exception("忙碌期间占位符未禁用");
        window.HandleTranslationShortcut(Key.S, ModifierKeys.Control);
        if (saves.Count != 1) throw new Exception("忙碌期间重复保存");
        busySignal.SetResult();
        Wait(busy);
        workspace.SearchText = "Continue";
        Render(root, "window-search", 1500);
        window.HandleTranslationShortcut(Key.F, ModifierKeys.Control);
        var search = Descendants<TextBox>(root).Single(box => box.Name == "SearchBox");
        if (search.SelectionLength != "Continue".Length) throw new Exception("Ctrl+F 未选择搜索词");
        window.HandleTranslationShortcut(Key.F8, ModifierKeys.None);
        if (workspace.SelectedItem?.SharedString?.OldText != "Continue") throw new Exception("F8 未定位待译");
        workspace.ClearSearchCommand.Execute(null);
        workspace.ViewMode = TranslationViewMode.Quality;
        workspace.SelectedItem = workspace.VisibleItems.First(item => item.SharedString?.OldText == "Continue");
        Render(root, "quality-wide", 1500);
        typeof(MainWindow).GetMethod("UpdateResponsiveLayout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [720d]);
        Render(root, "quality-narrow", 720);
        var filterPopup = (Popup)Descendants<TranslationMainView>(root).Single().FindName("FilterPopup");
        var statusSelector = Descendants<ComboBox>(filterPopup.Child).Single(combo => System.Windows.Automation.AutomationProperties.GetName(combo) == "译文状态筛选");
        statusSelector.SelectedValue = TranslationStatusFilter.Pending;
        Render(root, "quality-pending", 720);
        if (workspace.StatusFilter != TranslationStatusFilter.Pending || workspace.VisibleItems.Any(item => item.IsTranslationComplete)) throw new Exception("状态筛选绑定无效");
        workspace.SelectedItem = workspace.VisibleItems.First(item => item.SharedString?.OldText == "Continue");
        var issueEditor = Descendants<TextEditor>(root).Single(text => text.Name == "TranslationEditor");
        issueEditor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        issueEditor.Text = "继续";
        if (!workspace.NeedsResultsRefresh) throw new Exception("编辑后缺少刷新提示");
        var refresh = Descendants<Button>(root).Single(button => button.Content as string == "刷新结果");
        refresh.Command.Execute(null);
        Render(root, "quality-refreshed", 720);
        if (workspace.VisibleItems.Any(item => item.SharedString?.OldText == "Continue")) throw new Exception("刷新未移除已修复项");
        workspace.StatusFilter = TranslationStatusFilter.All;
        var scopeSelector = Descendants<ComboBox>(filterPopup.Child).Single(combo => System.Windows.Automation.AutomationProperties.GetName(combo) == "文件或Label进度范围");
        var scopeId = workspace.ProgressScopes.First(option => option.Id != "all").Id;
        scopeSelector.SelectedValue = scopeId;
        Render(root, "quality-file-scope", 720);
        if (workspace.SelectedScope != scopeId || workspace.VisibleItems.Count != 2) throw new Exception("文件范围绑定未正确限定问题");
        workspace.RefreshResultsCommand.Execute(null);
        Render(root, "quality-file-refreshed", 720);
        if (scopeSelector.SelectedValue as string != scopeId) throw new Exception("刷新丢失了文件范围选择");
        workspace.ClearSearchCommand.Execute(null);
        workspace.ViewMode = TranslationViewMode.Strings;
        var batchSnapshot = session.Snapshot!;
        var template = batchSnapshot.TranslationUnits.First();
        foreach (var unit in batchSnapshot.TranslationUnits) unit.TranslationText = "你好 [name]，欢迎{b}回来{/b}！";
        var targetEntry = batchSnapshot.SharedStrings.First();
        targetEntry.Translation = "";
        if (targetEntry.Definitions.Count == 0)
            targetEntry.Definitions.Add(new TranslationUnit
            {
                Kind = TranslationUnitKind.String,
                Language = "schinese",
                FilePath = template.FilePath,
                RelativeTlPath = template.RelativeTlPath,
                BlockSpan = new TextSpan(0, 0),
                HeaderLine = 250,
                TranslationValueSpan = new TextSpan(0, 0),
                OldText = targetEntry.OldText
            });
        var matchingDonor = new TranslationUnit
        {
            Kind = TranslationUnitKind.Dialogue,
            Language = "schinese",
            FilePath = template.FilePath,
            RelativeTlPath = template.RelativeTlPath,
            BlockSpan = new TextSpan(0, 0),
            HeaderLine = 251,
            TranslationValueSpan = new TextSpan(0, 0),
            OriginalStatement = "e \"" + targetEntry.OldText + "\"",
            TranslationText = "你好 [name]，欢迎{b}回来{/b}！"
        };
        batchSnapshot.TlDocuments[0].Units.Add(matchingDonor);
        var batchWindow = new BulkTranslationWindow(workspace);
        var batchModel = (BulkTranslationViewModel)batchWindow.DataContext;
        batchModel.EntireProject = true;
        Wait(batchModel.RefreshCommand.ExecuteAsync(null));
        Render((FrameworkElement)batchWindow.Content, "bulk-preview-wide", 1000);
        Render((FrameworkElement)batchWindow.Content, "bulk-preview-narrow", 640);
        if (!batchModel.Rows.Any(row => row.CanApply)) throw new Exception("缺少批量候选");
        var check = Descendants<CheckBox>((FrameworkElement)batchWindow.Content).First(box => box.DataContext is BulkTranslationRow row && row.CanApply);
        check.IsChecked = false;
        if (((BulkTranslationRow)check.DataContext).IsSelected) throw new Exception("取消勾选未同步");
        check.IsChecked = true;
        batchModel.ApplyCommand.Execute(null);
        if (!workspace.UndoBulkCommand.CanExecute(null)) throw new Exception("批量撤销不可用");
        workspace.UndoBulkCommand.Execute(null);
        if (targetEntry.Translation != "") throw new Exception("批量撤销未恢复空译文");
        batchModel.Dispose();
        Console.WriteLine("Bulk preview rendered at 640/1000; real checkbox binding, apply and undo verified.");
        workspace.ViewMode = TranslationViewMode.Unbound;
        workspace.ClearSearchCommand.Execute(null);
        workspace.SelectedItem = workspace.VisibleItems.First(item => item.Unit is not null);
        var toolsWindow = new TranslationToolsWindow(workspace);
        var toolsModel = (TranslationToolsViewModel)toolsWindow.DataContext;
        toolsModel.Terms.Add(new() { Source = "Hello", Preferred = "你好", Forbidden = "您好", CaseSensitive = true });
        Wait(toolsModel.SaveTermsCommand.ExecuteAsync(null));
        if (workspace.Glossary.Count != 1) throw new Exception("术语保存失败");
        var tabs = (TabControl)toolsWindow.FindName("Tabs");
        for (var tab = 0; tab < 4; tab++)
        {
            tabs.SelectedIndex = tab;
            Render((FrameworkElement)toolsWindow.Content, $"editorial-{tab}-wide", 1100);
            Render((FrameworkElement)toolsWindow.Content, $"editorial-{tab}-narrow", 720);
        }
        foreach (var column in ((DataGrid)toolsWindow.FindName("TermsGrid")).Columns.OfType<DataGridCheckBoxColumn>())
            if (column.ElementStyle != app.Resources["GridCheckBoxStyle"]) throw new Exception("表格勾选列未使用深色模板");
        tabs.SelectedIndex = 1;
        Render((FrameworkElement)toolsWindow.Content, "editorial-review", 1100);
        var reviewSelector = Descendants<ComboBox>((FrameworkElement)toolsWindow.Content).Single();
        reviewSelector.SelectedValue = ReviewStatus.Discussion;
        toolsModel.Note = "核对角色说话语气";
        Wait(toolsModel.SaveNoteCommand.ExecuteAsync(null));
        if (workspace.SelectedReviewText != "需讨论" || workspace.SelectedReviewNote != toolsModel.Note) throw new Exception("校对状态绑定失败");
        tabs.SelectedIndex = 2;
        toolsModel.WholeProject = true; toolsModel.Find = "你好"; toolsModel.Replacement = "您好";
        Wait(toolsModel.PreviewCommand.ExecuteAsync(null));
        if (!toolsModel.PreviewRows.Any(row => row.CanApply)) throw new Exception("替换预览无结果：" + toolsModel.Message);
        Render((FrameworkElement)toolsWindow.Content, "editorial-replace-wide", 1100);
        Render((FrameworkElement)toolsWindow.Content, "editorial-replace-narrow", 720);
        var replacementCheck = Descendants<CheckBox>((FrameworkElement)toolsWindow.Content).First(box => box.DataContext is BulkTranslationRow row && row.CanApply);
        replacementCheck.IsChecked = false;
        if (((BulkTranslationRow)replacementCheck.DataContext).IsSelected) throw new Exception("替换勾选绑定失败");
        replacementCheck.IsChecked = true;
        Wait(toolsModel.ApplyCommand.ExecuteAsync(null));
        if (!workspace.UndoBulk()) throw new Exception("替换撤销失败：" + toolsModel.Message);
        toolsModel.Dispose();
        using (var palette = new ThemeService())
        {
            foreach (var color in new[] { "#D16BA5", "#9B59B6", "#3498DB", "#1ABC9C", "#2ECC71", "#E67E22", "#E74C3C", "#F1C40F" })
            {
                palette.TryApplyAccent(color, out _);
                Wait(Task.Delay(250));
                Render(root, "accent-" + color[1..], 1500);
                var primary = Descendants<Button>(root).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "保存译文");
                var foreground = ((SolidColorBrush)app.Resources["BrandAccentForegroundBrush"]).Color;
                foreach (var text in Descendants<TextBlock>(primary))
                    if (((SolidColorBrush)text.Foreground).Color != foreground) throw new Exception("主按钮内容未采用对比前景：" + color);
            }
        }
        Console.WriteLine("Editorial four tabs rendered at 720/1100; glossary save, review selector, replacement checkbox, apply and undo verified.");
        typeof(MainWindow).GetMethod("UpdateResponsiveLayout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [1920d]);
        Render(root, "window-1920", 1920);
        var toolRows = new[] { new { Source = "images/x-scene.png", Target = "images/scene.png", Status = "待处理", OriginalSize = "1 MB", CompressedSize = "800 KB", Saving = "20%" } };
        var toolData = new { Title = "工具预览", Description = "主题回归夹具", RelativeDirectory = "images", Prefix = "x-", Recursive = true, Quality = 85, MinimumSavingPercent = 5, OutputDirectory = "compressed", ReplaceOriginals = false, Summary = "1 项", Items = toolRows };
        foreach (var tool in new UserControl[] { new PrefixRenameToolView(), new ImageCompressionToolView() })
        {
            tool.DataContext = toolData;
            Render(tool, tool.GetType().Name, 1000);
            var table = Descendants<DataGrid>(tool).Single();
            table.SelectedIndex = 0;
            Render(tool, tool.GetType().Name + "-selected", 1000);
            if (((SolidColorBrush)table.RowBackground).Color != ((SolidColorBrush)app.Resources["BackgroundLevel0"]).Color) throw new Exception("工具表格回落到系统背景");
        }
        var rulesView = new ReplacementRulesDocumentView { DataContext = new { Rules = new[] { new { Enabled = true, Order = 1, Source = "Hello", Replacement = "你好", Description = "主题回归夹具" } } } };
        Render(rulesView, "replacement-rules", 1000);
        Console.WriteLine("Prefix rename, image compression and replacement-rule grids render with shared dark styles.");
        main.CurrentWorkspace = diagnostics;
        if (window.HandleTranslationShortcut(Key.S, ModifierKeys.Control)) throw new Exception("快捷键泄漏到其他工作区");
        Console.WriteLine("Full windows at 720/900/1180/1500 rendered; placeholder button, undo, Ctrl+F, Ctrl+S, busy guard, F8 and shortcut scope verified.");
    }

    private static void Wait(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void Render(FrameworkElement element, string name, int width)
    {
        if (element is Panel panel && panel.Background is null)
            panel.Background = (Brush)Application.Current.Resources["BackgroundLevel0"];
        TextOptions.SetTextRenderingMode(element, TextRenderingMode.Grayscale);
        element.Measure(new Size(width, 650));
        element.Arrange(new Rect(0, 0, width, 650));
        element.UpdateLayout();
        var image = new RenderTargetBitmap(width, 650, 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(OutputDirectory, name + ".png"));
        encoder.Save(stream);
    }

    private sealed class Analysis : IProjectAnalysisService
    {
        public Task<OperationResult<ProjectSnapshot>> ExecuteAsync(ProjectAnalysisRequest request, IProgress<ToolOperationProgress> progress, CancellationToken cancellationToken)
        {
            var snapshot = new ProjectSnapshot { ProjectRoot = request.ProjectRoot, GameDirectory = request.ProjectRoot + "/game", Language = request.Language };
            foreach (var text in new[] { "Hello [name], welcome {b}back{/b}!", "Continue", "Preferences", "Return to main menu" })
            {
                var entry = new SharedStringEntry { OldText = text, Language = request.Language };
                snapshot.SharedStrings.Add(entry);
            }
            var doc = new TlDocument
            {
                File = new Utf8TextFile { FullPath = Path.Combine(OutputDirectory, "fixture.rpy"), Text = "", HasBom = false, NewLine = "\n", HasFinalNewLine = true, Sha256 = "" },
                RelativePath = "chapter1.rpy",
                Language = request.Language
            };
            foreach (var translation in new[] { "你好 [name]，欢迎{b}回来{/b}！", "[name]，欢迎你{b}再次光临{/b}！" })
                doc.Units.Add(new TranslationUnit
                {
                    Kind = TranslationUnitKind.Dialogue,
                    Language = request.Language,
                    FilePath = doc.File.FullPath,
                    RelativeTlPath = doc.RelativePath,
                    HeaderLine = 120 + doc.Units.Count * 5,
                    BlockSpan = new TextSpan(0, 1),
                    OriginalStatement = "e \"Hello [name], welcome {b}back{/b}!\"",
                    TranslationText = translation,
                    TranslationValueSpan = new TextSpan(0, translation.Length)
                });
            snapshot.TlDocuments.Add(doc);
            return Task.FromResult(OperationResult<ProjectSnapshot>.Success(snapshot));
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private sealed class Preview : IRenPyImagePreviewService
    {
        public Task<RenPySceneContext> ResolveSceneContextAsync(RenPyImagePreviewRequest request, CancellationToken cancellationToken) => Task.FromResult(new RenPySceneContext(null, null, []));
        public void InvalidateProject(string path) { }
    }
    private sealed class Saves : IProjectSaveService
    {
        public int Count;
        public IReadOnlyList<Diagnostic> Validate(ProjectSnapshot snapshot) => [];
        public Task<OperationResult<SaveSummary>> ExecuteAsync(ProjectSaveRequest request, IProgress<ToolOperationProgress> progress, CancellationToken token)
        {
            Count++;
            return Task.FromResult(OperationResult<SaveSummary>.Success(new SaveSummary([])));
        }
    }
    private sealed class Dialog : IFileDialogService
    {
        public string? SelectProjectFolder(string? path) => null;
        public string? SelectSdkExecutable(string? path) => null;
    }
    private sealed class Confirmation : IConfirmationService
    {
        public bool Confirm(string title, string message, MessageBoxImage image = MessageBoxImage.Question) => true;
        public void ShowDiagnostics(string title, IEnumerable<Diagnostic> diagnostics) { }
        public void ShowMessage(string title, string message, MessageBoxImage image = MessageBoxImage.Information) { }
    }
    private sealed class Theme : IThemeService
    {
        public string AccentColor => "#D16BA5";
        public bool TryApplyAccent(string color, out string? error) { error = null; return true; }
    }
}
