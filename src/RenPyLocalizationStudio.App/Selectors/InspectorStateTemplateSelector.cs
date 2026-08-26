using System.Windows;
using System.Windows.Controls;
using RenPyLocalizationStudio.App.ViewModels;

namespace RenPyLocalizationStudio.App.Selectors;

/// <summary>根据检查器状态选择轻量模板，避免在空状态下创建编辑器。</summary>
public sealed class InspectorStateTemplateSelector : DataTemplateSelector
{
    public DataTemplate? EmptyTemplate { get; set; }
    public DataTemplate? FlowTemplate { get; set; }
    public DataTemplate? TranslationEditorTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is InspectorStateViewModel state
            ? state.Mode switch
            {
                InspectorMode.Flow => FlowTemplate,
                InspectorMode.TranslationEditor => TranslationEditorTemplate,
                _ => EmptyTemplate
            }
            : EmptyTemplate;
}
