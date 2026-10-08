using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.App.Views;

public partial class TranslationToolsWindow : Window
{
    private readonly TranslationToolsViewModel _model;
    public TranslationToolsWindow(TranslationWorkspaceViewModel workspace, int tab = 0)
    {
        InitializeComponent(); DataContext = _model = new(workspace); Tabs.SelectedIndex = tab;
        Closed += (_, _) => _model.Dispose();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void SaveTerms_Click(object sender, RoutedEventArgs e)
    {
        TermsGrid.CommitEdit(DataGridEditingUnit.Cell, true); TermsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        await _model.SaveTermsCommand.ExecuteAsync(null);
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "校对表 CSV|*.csv", FileName = "translation-review.csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var text = ReviewCsv.Write(_model.Workspace.ExportReviewRows());
            await File.WriteAllTextAsync(dialog.FileName, text, new UTF8Encoding(true));
            _model.Message = "已导出当前语言。文本列带单引号保护，回填时自动去除；请保留标识、原文和基线列。";
        }
        catch (Exception ex) { _model.Message = ex.Message; }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "校对表 CSV|*.csv", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var file = await Utf8TextFile.ReadAsync(dialog.FileName);
            _model.LoadCsv(dialog.FileName, file.Text);
        }
        catch (Exception ex) { _model.Message = ex.Message; }
    }
}
