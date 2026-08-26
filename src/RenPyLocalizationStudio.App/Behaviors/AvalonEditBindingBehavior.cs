using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using RenPyLocalizationStudio.App.ViewModels;

namespace RenPyLocalizationStudio.App.Behaviors;

public static class AvalonEditBindingBehavior
{
    private sealed class EditorState
    {
        public bool IsUpdating;
        public bool IsAttached;
        public string? DocumentKey;
    }

    private static readonly ConditionalWeakTable<TextEditor, EditorState> States = new();

    public static readonly DependencyProperty BindableTextProperty = DependencyProperty.RegisterAttached(
        "BindableText", typeof(string), typeof(AvalonEditBindingBehavior),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBindableTextChanged));

    public static readonly DependencyProperty DocumentKeyProperty = DependencyProperty.RegisterAttached(
        "DocumentKey", typeof(string), typeof(AvalonEditBindingBehavior), new PropertyMetadata(null, OnDocumentKeyChanged));

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.RegisterAttached(
        "IsReadOnly", typeof(bool), typeof(AvalonEditBindingBehavior), new PropertyMetadata(false, OnIsReadOnlyChanged));

    public static readonly DependencyProperty FocusRequestProperty = DependencyProperty.RegisterAttached(
        "FocusRequest", typeof(long), typeof(AvalonEditBindingBehavior), new PropertyMetadata(0L, OnFocusRequestChanged));

    public static readonly DependencyProperty SelectionRequestProperty = DependencyProperty.RegisterAttached(
        "SelectionRequest", typeof(TextSelectionRequest), typeof(AvalonEditBindingBehavior), new PropertyMetadata(null, OnSelectionRequestChanged));

    public static void SetBindableText(DependencyObject element, string value) => element.SetValue(BindableTextProperty, value);
    public static string GetBindableText(DependencyObject element) => (string)element.GetValue(BindableTextProperty);
    public static void SetDocumentKey(DependencyObject element, string value) => element.SetValue(DocumentKeyProperty, value);
    public static string GetDocumentKey(DependencyObject element) => (string)element.GetValue(DocumentKeyProperty);
    public static void SetIsReadOnly(DependencyObject element, bool value) => element.SetValue(IsReadOnlyProperty, value);
    public static bool GetIsReadOnly(DependencyObject element) => (bool)element.GetValue(IsReadOnlyProperty);
    public static void SetFocusRequest(DependencyObject element, long value) => element.SetValue(FocusRequestProperty, value);
    public static long GetFocusRequest(DependencyObject element) => (long)element.GetValue(FocusRequestProperty);
    public static void SetSelectionRequest(DependencyObject element, TextSelectionRequest? value) => element.SetValue(SelectionRequestProperty, value);
    public static TextSelectionRequest? GetSelectionRequest(DependencyObject element) => (TextSelectionRequest?)element.GetValue(SelectionRequestProperty);

    private static void EnsureAttached(TextEditor editor)
    {
        var state = States.GetOrCreateValue(editor);
        if (state.IsAttached) return;
        editor.Loaded += EditorLoaded;
        editor.Unloaded += EditorUnloaded;
        if (editor.IsLoaded) AttachTextChanged(editor, state);
        state.IsAttached = true;
    }

    private static void EditorLoaded(object sender, RoutedEventArgs e)
    {
        var editor = (TextEditor)sender;
        AttachTextChanged(editor, States.GetOrCreateValue(editor));
        SyncFromViewModel(editor, GetBindableText(editor), clearUndo: true);
    }

    private static void EditorUnloaded(object sender, RoutedEventArgs e)
    {
        var editor = (TextEditor)sender;
        editor.TextChanged -= EditorTextChanged;
    }

    private static void AttachTextChanged(TextEditor editor, EditorState state)
    {
        editor.TextChanged -= EditorTextChanged;
        editor.TextChanged += EditorTextChanged;
    }

    private static void EditorTextChanged(object? sender, EventArgs e)
    {
        var editor = (TextEditor)sender!;
        var state = States.GetOrCreateValue(editor);
        if (state.IsUpdating) return;
        state.IsUpdating = true;
        editor.SetCurrentValue(BindableTextProperty, editor.Text);
        state.IsUpdating = false;
    }

    private static void OnBindableTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor) return;
        EnsureAttached(editor);
        var state = States.GetOrCreateValue(editor);
        if (!state.IsUpdating) SyncFromViewModel(editor, e.NewValue as string ?? string.Empty, clearUndo: false);
    }

    private static void OnDocumentKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor) return;
        EnsureAttached(editor);
        var state = States.GetOrCreateValue(editor);
        var key = e.NewValue as string;
        if (string.Equals(state.DocumentKey, key, StringComparison.Ordinal)) return;
        state.DocumentKey = key;
        SyncFromViewModel(editor, GetBindableText(editor), clearUndo: true);
    }

    private static void SyncFromViewModel(TextEditor editor, string text, bool clearUndo)
    {
        var state = States.GetOrCreateValue(editor);
        if (state.IsUpdating || string.Equals(editor.Text, text, StringComparison.Ordinal)) return;
        var caret = Math.Min(editor.CaretOffset, text.Length);
        state.IsUpdating = true;
        editor.Document.Text = text;
        editor.CaretOffset = caret;
        if (clearUndo) editor.Document.UndoStack.ClearAll();
        state.IsUpdating = false;
    }

    private static void OnIsReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextEditor editor) editor.IsReadOnly = (bool)e.NewValue;
    }

    private static void OnFocusRequestChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor || Equals(e.OldValue, e.NewValue)) return;
        editor.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            editor.Focus();
            editor.CaretOffset = editor.Text.Length;
        });
    }

    private static void OnSelectionRequestChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor || e.NewValue is not TextSelectionRequest request) return;
        editor.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var start = Math.Clamp(request.Start, 0, editor.Text.Length);
            var length = Math.Clamp(request.Length, 0, editor.Text.Length - start);
            editor.Select(start, length);
            editor.TextArea.Caret.Offset = start + length;
            editor.Focus();
        });
    }
}
