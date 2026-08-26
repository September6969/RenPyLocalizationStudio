using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RenPyLocalizationStudio.App.Behaviors;
namespace RenPyLocalizationStudio.App.Views;

public partial class ZzzDocumentView : UserControl
{
    public ZzzDocumentView() => InitializeComponent();

    private void CompletionButton_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            CodeEditor.Focus();
            CodeEditor.TextArea.Focus();
            RenPyCodeEditorBehavior.OpenCompletion(CodeEditor);
        });
    }
}
