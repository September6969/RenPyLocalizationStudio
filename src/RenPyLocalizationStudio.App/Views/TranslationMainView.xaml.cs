using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.App.Views;

public partial class TranslationMainView : UserControl
{
    private GridLength _expandedPreviewHeight = new(2, GridUnitType.Star);
    private GridLength _expandedFlowHeight = new(3, GridUnitType.Star);
    private bool _previewCollapsed;

    public TranslationMainView() => InitializeComponent();

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
