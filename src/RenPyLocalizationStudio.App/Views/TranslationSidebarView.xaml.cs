using System.Windows.Controls;
using System.Windows.Input;
using RenPyLocalizationStudio.App.ViewModels;

namespace RenPyLocalizationStudio.App.Views;

public partial class TranslationSidebarView : UserControl
{
    public TranslationSidebarView() => InitializeComponent();

    private void LabelList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TranslationSidebarViewModel viewModel && viewModel.Workspace.NavigateLabelCommand.CanExecute(null))
            viewModel.Workspace.NavigateLabelCommand.Execute(null);
    }

    private void LabelList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not TranslationSidebarViewModel viewModel) return;
        e.Handled = true;
        viewModel.Workspace.NavigateLabelCommand.Execute(null);
    }
}
