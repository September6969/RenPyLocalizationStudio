using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace RenPyLocalizationStudio.App.Behaviors;

/// <summary>表格勾选列自带显式系统样式，需要同时替换展示态与编辑态才能统一主题。</summary>
public static class DataGridThemeBehavior
{
    public static readonly DependencyProperty ThemeCheckBoxesProperty = DependencyProperty.RegisterAttached(
        "ThemeCheckBoxes", typeof(bool), typeof(DataGridThemeBehavior), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty RegistrationProperty = DependencyProperty.RegisterAttached(
        "Registration", typeof(Registration), typeof(DataGridThemeBehavior));
    public static bool GetThemeCheckBoxes(DependencyObject target) => (bool)target.GetValue(ThemeCheckBoxesProperty);
    public static void SetThemeCheckBoxes(DependencyObject target, bool value) => target.SetValue(ThemeCheckBoxesProperty, value);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not DataGrid grid) return;
        (grid.GetValue(RegistrationProperty) as Registration)?.Dispose();
        grid.SetValue(RegistrationProperty, (bool)e.NewValue ? new Registration(grid) : null);
    }
    private sealed class Registration : IDisposable
    {
        private readonly DataGrid _grid;
        public Registration(DataGrid grid) { _grid = grid; grid.Columns.CollectionChanged += ColumnsChanged; grid.Loaded += Loaded; Apply(); }
        private void ColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Apply();
        private void Loaded(object sender, RoutedEventArgs e) => Apply();
        private void Apply()
        {
            if (_grid.TryFindResource("GridCheckBoxStyle") is not Style display || _grid.TryFindResource(typeof(CheckBox)) is not Style editing) return;
            foreach (var column in _grid.Columns.OfType<DataGridCheckBoxColumn>())
            {
                column.ElementStyle = display;
                column.EditingElementStyle = editing;
            }
        }
        public void Dispose() { _grid.Columns.CollectionChanged -= ColumnsChanged; _grid.Loaded -= Loaded; }
    }
}
