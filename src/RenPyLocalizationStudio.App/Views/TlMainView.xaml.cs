using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;
namespace RenPyLocalizationStudio.App.Views;

public partial class TlMainView : UserControl
{
    public TlMainView() => InitializeComponent();

    private void OpenOfficialSdkPage(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
