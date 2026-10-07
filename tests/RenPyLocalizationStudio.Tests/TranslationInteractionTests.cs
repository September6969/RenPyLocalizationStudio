using System.Windows;
using System.Windows.Data;
using ICSharpCode.AvalonEdit;
using RenPyLocalizationStudio.App;
using RenPyLocalizationStudio.App.Behaviors;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.App.Views;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.Tests;

public sealed class TranslationInteractionTests
{
    [Fact]
    public async Task 采用已有译文支持撤销并保留编码保存后能重新读取()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var target = snapshot.TranslationUnits.Last();
        var suggestions = new TranslationMemoryIndex(snapshot).Find(target);
        RunSta(() =>
        {
            using var inspector = new TranslationInspectorViewModel(new TaskCenterViewModel());
            inspector.SetTarget(EditorTarget.ForUnit(target), suggestions: suggestions);
            var editor = CreateEditor(inspector);
            Assert.True(inspector.HasSuggestions);
            Assert.NotEmpty(inspector.ValidationMessage);
            TranslationInspectorView.ApplySuggestion(editor, suggestions[0]);
            Assert.Equal("你好 [name]", target.TranslationText);
            Assert.Empty(inspector.ValidationMessage);
            editor.Undo();
            Assert.Equal(string.Empty, target.TranslationText);
            TranslationInspectorView.ApplySuggestion(editor, suggestions[1]);
            editor.IsReadOnly = true;
            TranslationInspectorView.ApplySuggestion(editor, suggestions[0]);
            Assert.Equal("您好 [name]", target.TranslationText);
        });

        var saved = await TestFiles.SaveAsync(snapshot, false, true);
        Assert.True(saved.IsSuccess);
        var bytes = await File.ReadAllBytesAsync(project.TlPath);
        Assert.True(bytes.AsSpan().StartsWith(System.Text.Encoding.UTF8.Preamble));
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        var reopened = await TestFiles.AnalyzeAsync(project.Root);
        Assert.Equal("您好 [name]", reopened.TranslationUnits.Last().TranslationText);
        Assert.Equal("你好 [name]", reopened.TranslationUnits.First().TranslationText);
    }

    [Theory]
    [InlineData(720, true, 0, 300)]
    [InlineData(900, true, 0, 300)]
    [InlineData(900, false, 260, 0)]
    [InlineData(1180, true, 260, 360)]
    [InlineData(1500, false, 260, 0)]
    public void 窄窗口保留可用编辑器并为正文留出空间(double width, bool visible, double sidebar, double inspector)
    {
        var layout = WorkspacePaneLayout.Calculate(width, visible);
        Assert.Equal(sidebar, layout.SidebarWidth);
        Assert.Equal(inspector, layout.InspectorWidth);
        Assert.True(width - 52 - sidebar - inspector >= 350);
    }

    [Fact]
    public void 点击占位符替换选区同步译文并能一次撤销() => RunSta(() =>
    {
        var unit = CreateUnit("你好，姓名！");
        using var inspector = new TranslationInspectorViewModel(new TaskCenterViewModel());
        inspector.SetTarget(EditorTarget.ForUnit(unit));
        var editor = CreateEditor(inspector);
        editor.Select(3, 2);

        TranslationInspectorView.InsertPlaceholder(editor, "[name]");

        Assert.Equal("你好，[name]！", editor.Text);
        Assert.Equal(editor.Text, unit.TranslationText);
        Assert.True(unit.IsDirty);
        Assert.Equal(9, editor.CaretOffset);
        editor.Undo();
        Assert.Equal("你好，姓名！", editor.Text);
        Assert.Equal(editor.Text, unit.TranslationText);
        Assert.False(editor.CanUndo);
    });

    [Fact]
    public void 占位符在光标处插入且只读时不会改动文本() => RunSta(() =>
    {
        var editor = new TextEditor { Text = "前后" };
        editor.Select(1, 0);
        TranslationInspectorView.InsertPlaceholder(editor, "{b}");
        Assert.Equal("前{b}后", editor.Text);
        editor.IsReadOnly = true;
        TranslationInspectorView.InsertPlaceholder(editor, "[/name]");
        Assert.Equal("前{b}后", editor.Text);
    });

    [Fact]
    public void 切换译文条目不会把占位符撤销到另一条中() => RunSta(() =>
    {
        using var inspector = new TranslationInspectorViewModel(new TaskCenterViewModel());
        inspector.SetTarget(EditorTarget.ForUnit(CreateUnit("第一条")));
        var editor = CreateEditor(inspector);
        TranslationInspectorView.InsertPlaceholder(editor, "[name]");
        var second = CreateUnit("第二条", 2);
        inspector.SetTarget(EditorTarget.ForUnit(second));
        Assert.Equal("第二条", editor.Text);
        Assert.False(editor.CanUndo);
        editor.Undo();
        Assert.Equal("第二条", second.TranslationText);
    });

    private static TextEditor CreateEditor(TranslationInspectorViewModel inspector)
    {
        var editor = new TextEditor();
        BindingOperations.SetBinding(editor, AvalonEditBindingBehavior.BindableTextProperty,
            new Binding(nameof(inspector.TranslationText)) { Source = inspector, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        BindingOperations.SetBinding(editor, AvalonEditBindingBehavior.DocumentKeyProperty,
            new Binding(nameof(inspector.DocumentKey)) { Source = inspector });
        editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        return editor;
    }

    private static TranslationUnit CreateUnit(string text, int line = 1) => new()
    {
        Kind = TranslationUnitKind.Dialogue,
        Language = "schinese",
        FilePath = "E:/fixture.rpy",
        RelativeTlPath = "fixture.rpy",
        HeaderLine = line,
        BlockSpan = new TextSpan(0, 1),
        OriginalStatement = "Hello [name]",
        TranslationText = text,
        TranslationValueSpan = new TextSpan(0, text.Length)
    };

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "编辑器测试超时");
        Assert.Null(failure);
    }
}
