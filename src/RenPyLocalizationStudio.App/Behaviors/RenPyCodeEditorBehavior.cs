using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace RenPyLocalizationStudio.App.Behaviors;

public static class RenPyCodeEditorBehavior
{
    private sealed class EditorState
    {
        public bool IsAttached;
        public bool IsInteractive;
        public CompletionWindow? CompletionWindow;
    }

    private sealed record CompletionDefinition(string Text, string InsertText, string Description);

    private sealed record CompletionSegment(int Offset, int Length) : ISegment
    {
        public int EndOffset => Offset + Length;
    }

    private sealed class CompletionItem(CompletionDefinition definition) : ICompletionData
    {
        public ImageSource? Image => null;
        public string Text => definition.Text;
        public object Content => definition.Text;
        public object Description => definition.Description;
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, definition.InsertText);
    }

    private static readonly ConditionalWeakTable<TextEditor, EditorState> States = new();
    private static readonly ConditionalWeakTable<TextArea, TextEditor> TextAreaOwners = new();
    private static readonly CompletionDefinition[] Completions =
    [
        new("label", "label name:\n    ", "声明剧情入口 label"),
        new("jump", "jump target", "跳转到目标 label"),
        new("call", "call target", "调用目标 label，之后返回"),
        new("return", "return", "从当前 label 返回"),
        new("menu", "menu:\n    \"选项\":\n        ", "创建选择菜单"),
        new("if", "if condition:\n    ", "条件分支"),
        new("elif", "elif condition:\n    ", "追加条件分支"),
        new("else", "else:\n    ", "默认条件分支"),
        new("init python", "init python:\n    ", "初始化 Python 块"),
        new("translate strings", "translate schinese strings:\n\n    old \"\"\n    new \"\"", "字符串翻译块"),
        new("define", "define name = value", "定义常量或角色"),
        new("default", "default name = value", "定义存档变量默认值"),
        new("config.replace_text", "define config.replace_text = lambda text: text", "配置全局文本替换"),
        new("renpy.change_language", "renpy.change_language(\"schinese\")", "切换当前语言"),
        new("Language", "Language(\"schinese\")", "Ren’Py 语言切换 Action")
    ];

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(RenPyCodeEditorBehavior), new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void OpenCompletion(TextEditor editor)
    {
        // 工具栏触发后把键盘焦点交还编辑器，Tab 才能像 IDE 一样接受候选。
        editor.Focus();
        editor.TextArea.Focus();
        ShowCompletion(editor, true);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor) return;
        var state = States.GetOrCreateValue(editor);
        if ((bool)e.NewValue && !state.IsAttached)
        {
            editor.Loaded += EditorLoaded;
            editor.Unloaded += EditorUnloaded;
            editor.Options.ConvertTabsToSpaces = true;
            editor.Options.IndentationSize = 4;
            state.IsAttached = true;
            if (editor.IsLoaded) AttachInteractive(editor, state);
        }
        else if (!(bool)e.NewValue && state.IsAttached)
        {
            Detach(editor, state);
        }
    }

    private static void EditorLoaded(object sender, RoutedEventArgs e)
    {
        var editor = (TextEditor)sender;
        AttachInteractive(editor, States.GetOrCreateValue(editor));
    }

    private static void EditorUnloaded(object sender, RoutedEventArgs e)
    {
        var editor = (TextEditor)sender;
        DetachInteractive(editor, States.GetOrCreateValue(editor));
    }

    private static void Detach(TextEditor editor, EditorState state)
    {
        DetachInteractive(editor, state);
        editor.Loaded -= EditorLoaded;
        editor.Unloaded -= EditorUnloaded;
        state.IsAttached = false;
    }

    private static void AttachInteractive(TextEditor editor, EditorState state)
    {
        if (state.IsInteractive) return;
        TextAreaOwners.Remove(editor.TextArea);
        TextAreaOwners.Add(editor.TextArea, editor);
        editor.TextArea.PreviewKeyDown += EditorPreviewKeyDown;
        editor.TextArea.TextEntered += EditorTextEntered;
        state.IsInteractive = true;
    }

    private static void DetachInteractive(TextEditor editor, EditorState state)
    {
        editor.TextArea.PreviewKeyDown -= EditorPreviewKeyDown;
        editor.TextArea.TextEntered -= EditorTextEntered;
        TextAreaOwners.Remove(editor.TextArea);
        state.CompletionWindow?.Close();
        state.CompletionWindow = null;
        state.IsInteractive = false;
    }

    private static void EditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextArea textArea || !TextAreaOwners.TryGetValue(textArea, out var editor)) return;
        var state = States.GetOrCreateValue(editor);
        if (e.Key == Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ShowCompletion(editor, true);
            e.Handled = true;
            return;
        }

        // AvalonEdit 默认不会用 Tab 接受候选，这里显式补齐常见 IDE 行为。
        if (state.CompletionWindow?.IsVisible == true && e.Key == Key.Tab)
        {
            var window = state.CompletionWindow;
            var selected = window.CompletionList.SelectedItem ?? window.CompletionList.CompletionData.FirstOrDefault();
            if (selected is not null)
            {
                var segment = new CompletionSegment(window.StartOffset, Math.Max(0, window.EndOffset - window.StartOffset));
                selected.Complete(editor.TextArea, segment, e);
                window.Close();
            }
            e.Handled = true;
            return;
        }

        if (state.CompletionWindow?.IsVisible == true && e.Key is Key.Enter or Key.Up or Key.Down or Key.Escape)
            return;

        if (e.Key == Key.Oem2 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ToggleComments(editor);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab)
        {
            ApplyTab(editor, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            InsertIndentedLine(editor);
            e.Handled = true;
        }
    }

    private static void EditorTextEntered(object sender, TextCompositionEventArgs e)
    {
        var textArea = (TextArea)sender;
        if (!TextAreaOwners.TryGetValue(textArea, out var editor) ||
            States.GetOrCreateValue(editor).CompletionWindow?.IsVisible == true ||
            string.IsNullOrEmpty(e.Text)) return;
        var value = e.Text[0];
        var prefixLength = GetIdentifierPrefixLength(editor);
        if ((char.IsLetter(value) && prefixLength >= 2) || value == '.') ShowCompletion(editor, false);
    }

    public static void ApplyTab(TextEditor editor, bool outdent)
    {
        if (editor.SelectionLength == 0)
        {
            var line = editor.Document.GetLineByOffset(editor.CaretOffset);
            if (outdent)
            {
                var count = CountLeadingSpaces(editor.Document.GetText(line));
                var remove = Math.Min(4, count);
                if (remove > 0)
                {
                    editor.Document.Remove(line.Offset, remove);
                    editor.CaretOffset = Math.Max(line.Offset, editor.CaretOffset - remove);
                }
            }
            else
            {
                var column = editor.CaretOffset - line.Offset;
                var spaces = 4 - column % 4;
                var insertionOffset = editor.CaretOffset;
                editor.Document.Insert(insertionOffset, new string(' ', spaces));
                editor.CaretOffset = Math.Min(insertionOffset + spaces, editor.Document.TextLength);
            }
            return;
        }

        var start = editor.Document.GetLineByOffset(editor.SelectionStart);
        var endOffset = Math.Max(editor.SelectionStart, editor.SelectionStart + editor.SelectionLength - 1);
        var end = editor.Document.GetLineByOffset(endOffset);
        var lines = new List<DocumentLine>();
        for (var line = start; line is not null && line.LineNumber <= end.LineNumber; line = line.NextLine) lines.Add(line);
        foreach (var line in lines.AsEnumerable().Reverse())
        {
            if (!outdent) editor.Document.Insert(line.Offset, "    ");
            else
            {
                var remove = Math.Min(4, CountLeadingSpaces(editor.Document.GetText(line)));
                if (remove > 0) editor.Document.Remove(line.Offset, remove);
            }
        }
    }

    private static void InsertIndentedLine(TextEditor editor)
    {
        var line = editor.Document.GetLineByOffset(editor.CaretOffset);
        var beforeCaret = editor.Document.GetText(line.Offset, editor.CaretOffset - line.Offset);
        var indent = new string(' ', CountLeadingSpaces(beforeCaret));
        if (beforeCaret.TrimEnd().EndsWith(':')) indent += "    ";
        var selectionStart = editor.SelectionStart;
        editor.Document.Replace(selectionStart, editor.SelectionLength, Environment.NewLine + indent);
        editor.CaretOffset = selectionStart + Environment.NewLine.Length + indent.Length;
    }

    private static void ToggleComments(TextEditor editor)
    {
        var start = editor.Document.GetLineByOffset(editor.SelectionStart);
        var endOffset = Math.Max(editor.SelectionStart, editor.SelectionStart + editor.SelectionLength - 1);
        var end = editor.Document.GetLineByOffset(endOffset);
        var lines = new List<DocumentLine>();
        for (var line = start; line is not null && line.LineNumber <= end.LineNumber; line = line.NextLine) lines.Add(line);
        var allCommented = lines.All(line => editor.Document.GetText(line).TrimStart().StartsWith('#'));
        foreach (var line in lines.AsEnumerable().Reverse())
        {
            var text = editor.Document.GetText(line);
            var leading = CountLeadingSpaces(text);
            if (allCommented)
            {
                var marker = text.IndexOf('#', leading);
                if (marker >= 0)
                {
                    var remove = line.Offset + marker + 1 < line.EndOffset && editor.Document.GetCharAt(line.Offset + marker + 1) == ' ' ? 2 : 1;
                    editor.Document.Remove(line.Offset + marker, remove);
                }
            }
            else editor.Document.Insert(line.Offset + leading, "# ");
        }
    }

    private static void ShowCompletion(TextEditor editor, bool explicitInvocation)
    {
        var state = States.GetOrCreateValue(editor);
        state.CompletionWindow?.Close();
        var prefixLength = GetIdentifierPrefixLength(editor);
        var prefix = editor.Document.GetText(editor.CaretOffset - prefixLength, prefixLength);
        var matching = Completions.Where(item => prefixLength == 0 || item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length == 0 && !explicitInvocation) return;
        if (matching.Length == 0) matching = Completions;

        var window = new CompletionWindow(editor.TextArea)
        {
            StartOffset = editor.CaretOffset - prefixLength,
            EndOffset = editor.CaretOffset,
            Width = 320,
            Height = 280,
            Background = Application.Current.TryFindResource("BackgroundLevel1") as Brush,
            Foreground = Application.Current.TryFindResource("DialogueTextBrush") as Brush,
            BorderBrush = Application.Current.TryFindResource("CodeEditorBorderBrush") as Brush
        };
        window.CompletionList.ListBox.Background = Application.Current.TryFindResource("BackgroundLevel1") as Brush;
        window.CompletionList.ListBox.Foreground = Application.Current.TryFindResource("DialogueTextBrush") as Brush;
        window.CompletionList.ListBox.BorderThickness = new Thickness(0);
        window.CompletionList.ListBox.ItemContainerStyle = Application.Current.TryFindResource("FlatListItemStyle") as Style;
        foreach (var definition in matching)
            window.CompletionList.CompletionData.Add(new CompletionItem(definition));
        window.Closed += (_, _) => state.CompletionWindow = null;
        state.CompletionWindow = window;
        window.Show();
    }

    private static int GetIdentifierPrefixLength(TextEditor editor)
    {
        var offset = editor.CaretOffset;
        var start = offset;
        while (start > 0)
        {
            var value = editor.Document.GetCharAt(start - 1);
            if (!char.IsLetterOrDigit(value) && value is not '_' and not '.') break;
            start--;
        }
        return offset - start;
    }

    private static int CountLeadingSpaces(string text)
    {
        var count = 0;
        while (count < text.Length && text[count] == ' ') count++;
        return count;
    }

}
