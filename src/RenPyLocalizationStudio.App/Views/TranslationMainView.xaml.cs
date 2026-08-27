using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.App.Views;

public partial class TranslationMainView : UserControl
{
    public TranslationMainView() => InitializeComponent();

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
