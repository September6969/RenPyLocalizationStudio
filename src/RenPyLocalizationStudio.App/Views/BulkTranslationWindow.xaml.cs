using System.Windows;
using RenPyLocalizationStudio.App.ViewModels;

namespace RenPyLocalizationStudio.App.Views;

public partial class BulkTranslationWindow : Window
{
    private readonly BulkTranslationViewModel _model;
    public BulkTranslationWindow(TranslationWorkspaceViewModel workspace)
    {
        InitializeComponent();
        DataContext = _model = new(workspace);
        Loaded += async (_, _) => await _model.RefreshCommand.ExecuteAsync(null);
        Closed += (_, _) => _model.Dispose();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
