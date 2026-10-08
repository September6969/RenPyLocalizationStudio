using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.App.Views;

public partial class TranslationInspectorView : UserControl
{
    public TranslationInspectorView() => InitializeComponent();

    private void Placeholder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextEditor editor, DataContext: string token })
            InsertPlaceholder(editor, token);
    }

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextEditor editor, DataContext: TranslationSuggestion suggestion })
            ApplySuggestion(editor, suggestion);
    }

    internal static void ApplySuggestion(TextEditor editor, TranslationSuggestion suggestion)
    {
        if (editor.IsReadOnly) return;
        // 复用也走编辑器的正常变更链，保留撤销、占位符校验、脏状态和自动保存。
        editor.Document.Replace(0, editor.Document.TextLength, suggestion.Translation);
        editor.Select(editor.Document.TextLength, 0);
        editor.TextArea.Focus();
    }

    internal static void InsertPlaceholder(TextEditor editor, string token)
    {
        if (editor.IsReadOnly || string.IsNullOrEmpty(token)) return;
        var start = editor.SelectionStart;
        // 一次替换对应一次撤销，不重建文档，也不丢失原有光标位置。
        editor.Document.Replace(start, editor.SelectionLength, token);
        editor.Select(start + token.Length, 0);
        editor.TextArea.Focus();
    }
}
