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
        if (DataContext is TranslationMainContentViewModel { Workspace.SelectedItem.Node.Kind: FlowNodeKind.Jump or FlowNodeKind.Call } viewModel)
            viewModel.Workspace.OpenSelectedCommand.Execute(null);
    }

    private void FlowList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not TranslationMainContentViewModel viewModel) return;
        e.Handled = true;
        viewModel.Workspace.OpenSelectedCommand.Execute(null);
    }
}
