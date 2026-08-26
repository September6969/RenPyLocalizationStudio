using System.Windows.Controls;
using System.Windows.Input;
using RenPyLocalizationStudio.App.ViewModels;
namespace RenPyLocalizationStudio.App.Views;
public partial class DiagnosticsMainView : UserControl
{
    public DiagnosticsMainView() => InitializeComponent();
    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (DataContext is DiagnosticsMainContentViewModel vm) vm.Workspace.OpenSourceCommand.Execute(null); }
    private void List_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && DataContext is DiagnosticsMainContentViewModel vm) { e.Handled = true; vm.Workspace.OpenSourceCommand.Execute(null); } }
}
