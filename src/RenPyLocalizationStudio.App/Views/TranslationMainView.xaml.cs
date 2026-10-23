using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.App.Views;

public partial class TranslationMainView : UserControl
{
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
    private void Toolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewSegments is null || CompactViewSelector is null || CoveragePanel is null) return;
        var wide = e.NewSize.Width >= 760;
        ViewSegments.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        CompactViewSelector.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        InlineSearchOptions.Visibility = e.NewSize.Width >= 560 ? Visibility.Visible : Visibility.Collapsed;
        CoveragePanel.Visibility = e.NewSize.Width >= 680 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var label in new[] { BulkLabel, EditorialLabel, FilterLabel }) label.Visibility = e.NewSize.Width >= 520 ? Visibility.Visible : Visibility.Collapsed;
        NextLabel.Visibility = e.NewSize.Width >= 360 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Editorial_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TranslationMainContentViewModel content) return;
        var tab = int.TryParse((sender as FrameworkElement)?.Tag?.ToString(), out var value) ? value : 0;
        new TranslationToolsWindow(content.Workspace, tab) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void Bulk_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TranslationMainContentViewModel content) return;
        new BulkTranslationWindow(content.Workspace) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private GridLength _expandedPreviewHeight = new(2, GridUnitType.Star);
    private GridLength _expandedFlowHeight = new(3, GridUnitType.Star);
    private bool _previewCollapsed;

    private TranslationWorkspaceViewModel? _workspace;
    private bool _restoringPreview;
    public TranslationMainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindPreviewPreferences();
        Loaded += (_, _) => BindPreviewPreferences();
        Unloaded += (_, _) => { if (_workspace is not null) _workspace.PropertyChanged -= WorkspaceLayoutChanged; _workspace = null; };
    }
    private void BindPreviewPreferences()
    {
        if (_workspace is not null) _workspace.PropertyChanged -= WorkspaceLayoutChanged;
        _workspace = (DataContext as TranslationMainContentViewModel)?.Workspace;
        if (_workspace is null) return;
        _workspace.PropertyChanged += WorkspaceLayoutChanged;
        RestorePreviewPreferences();
    }
    private void WorkspaceLayoutChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranslationWorkspaceViewModel.UiLayoutPreferences)) RestorePreviewPreferences();
    }
    private void RestorePreviewPreferences()
    {
        if (_workspace is null) return;
        _restoringPreview = true;
        try
        {
            var saved = _workspace.UiLayoutPreferences;
            _expandedPreviewHeight = new GridLength(saved.PreviewFraction, GridUnitType.Star);
            _expandedFlowHeight = new GridLength(1 - saved.PreviewFraction, GridUnitType.Star);
            if (!_previewCollapsed) { PreviewRow.Height = _expandedPreviewHeight; FlowRow.Height = _expandedFlowHeight; }
        }
        finally { _restoringPreview = false; }
    }
    private void PreviewSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => SavePreviewPreferences();
    private void SavePreviewPreferences()
    {
        if (_restoringPreview || DataContext is not TranslationMainContentViewModel content) return;
        var total = PreviewRow.ActualHeight + FlowRow.ActualHeight;
        var fraction = !_previewCollapsed && total > 0 ? PreviewRow.ActualHeight / total : content.Workspace.UiLayoutPreferences.PreviewFraction;
        content.Workspace.UpdatePreviewPreferences(_previewCollapsed, fraction);
    }

    internal void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not TranslationMainContentViewModel viewModel) return;
        // 清除延迟绑定中的输入，避免旧关键词稍后重新出现。
        SearchBox.SetCurrentValue(TextBox.TextProperty, string.Empty);
        SearchBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        viewModel.Workspace.SearchText = string.Empty;
        e.Handled = true;
    }

    private void PreviewCollapse_Changed(object sender, RoutedEventArgs e)
    {
        var collapsed = PreviewCollapseToggle.IsChecked == true;
        if (_previewCollapsed == collapsed) return;
        var workspace = (DataContext as TranslationMainContentViewModel)?.Workspace;
        var fraction = workspace?.UiLayoutPreferences.PreviewFraction ?? 0.4;
        if (collapsed && !_previewCollapsed)
        {
            var total = PreviewRow.ActualHeight + FlowRow.ActualHeight;
            if (total > 0) fraction = PreviewRow.ActualHeight / total;
        }
        _previewCollapsed = collapsed;
        if (collapsed)
        {
            // 保留用户拖动后的分栏比例，展开时恢复。
            _expandedPreviewHeight = PreviewRow.Height;
            _expandedFlowHeight = FlowRow.Height;
            PreviewRow.MinHeight = 36;
            PreviewRow.Height = new GridLength(36);
            FlowRow.Height = new GridLength(1, GridUnitType.Star);
            PreviewSplitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            PreviewRow.MinHeight = 110;
            PreviewRow.Height = _expandedPreviewHeight;
            FlowRow.Height = _expandedFlowHeight;
            PreviewSplitter.Visibility = Visibility.Visible;
        }
        if (!_restoringPreview) workspace?.UpdatePreviewPreferences(collapsed, fraction);
    }

    private void FlowList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not TranslationMainContentViewModel viewModel) return;
        if (ItemsControl.ContainerFromElement(FlowList, e.OriginalSource as DependencyObject) is not ListViewItem container ||
            container.DataContext is not ContentItem item) return;

        FlowList.SelectedItem = item;
        viewModel.Workspace.SelectedItem = item;
        if (viewModel.Workspace.ViewMode == TranslationViewMode.Bookmarks)
        {
            viewModel.Workspace.NavigateBookmarkCommand.Execute(item);
            e.Handled = true;
            return;
        }

        if (item.Node?.Kind is FlowNodeKind.Jump or FlowNodeKind.Call)
            viewModel.Workspace.OpenSelectedCommand.Execute(null);
    }

    private void FlowList_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not TranslationMainContentViewModel viewModel ||
            viewModel.Workspace.ViewMode != TranslationViewMode.Bookmarks ||
            FlowList.SelectedItem is not ContentItem item) return;

        viewModel.Workspace.NavigateBookmarkCommand.Execute(item);
        e.Handled = true;
    }

    private void FlowList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not TranslationMainContentViewModel viewModel) return;
        if (ItemsControl.ContainerFromElement(FlowList, e.OriginalSource as DependencyObject) is not ListViewItem container ||
            container.DataContext is not ContentItem item) return;

        FlowList.SelectedItem = item;
        viewModel.Workspace.SelectedItem = item;
    }

    private void FlowList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not TranslationMainContentViewModel viewModel) return;
        e.Handled = true;
        if (viewModel.Workspace.ViewMode == TranslationViewMode.Bookmarks && FlowList.SelectedItem is ContentItem item)
            viewModel.Workspace.NavigateBookmarkCommand.Execute(item);
        else
            viewModel.Workspace.OpenSelectedCommand.Execute(null);
    }
}
