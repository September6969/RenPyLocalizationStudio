using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace RenPyLocalizationStudio.App.ViewModels;

public sealed partial class TranslationWorkspaceViewModel
{
    public Services.UiLayoutSettings UiLayoutPreferences => _session.UiLayout;
    public void UpdatePreviewPreferences(bool collapsed, double fraction) => _session.UpdateUiLayout(_session.UiLayout with { PreviewCollapsed = collapsed, PreviewFraction = fraction });
    public int ActiveFilterCount => (ReviewFilter is null ? 0 : 1) + (StatusFilter == TranslationStatusFilter.All ? 0 : 1) + (SelectedScope == "all" ? 0 : 1);
    public bool HasScopeFilters => ActiveFilterCount > 0;
    public string InfoMessage => !string.IsNullOrEmpty(SearchError) ? SearchError : HasRecoveryMessage ? RecoveryMessage :
        IsComputing ? "正在计算，完成后更新结果…" : NeedsResultsRefresh ? "译文已变化，点击「刷新结果」更新列表。" : IsQualityView ? QualitySummary : "";
    public bool HasInfoMessage => InfoMessage.Length > 0;
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(ReviewFilter) or nameof(StatusFilter) or nameof(SelectedScope) or nameof(HasActiveFilters))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ActiveFilterCount)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasScopeFilters)));
        }
        if (e.PropertyName is nameof(SearchError) or nameof(RecoveryMessage) or nameof(IsComputing) or nameof(NeedsResultsRefresh) or nameof(IsQualityView) or nameof(QualitySummary))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(InfoMessage)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasInfoMessage)));
        }
    }
}

public sealed partial class TranslationInspectorViewModel
{
    public IRelayCommand? PreviousTranslationCommand { get; internal set; }
    public IRelayCommand? NextTranslationCommand { get; internal set; }
    public ICommand? SaveTranslationCommand { get; internal set; }
    public string CharacterSummary => SourceText.Length == 0 ? $"译文 {TranslationText.Length} 字" : $"译文 {TranslationText.Length} / 原文 {SourceText.Length}（{TranslationText.Length * 100d / SourceText.Length:0}%）";
    public bool IsTranslationLong => SourceText.Length > 0 && TranslationText.Length > SourceText.Length * 1.5;
    public bool HasImpact => !string.IsNullOrWhiteSpace(Impact);
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(SourceText) or nameof(TranslationText))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CharacterSummary))); base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsTranslationLong)));
        }
        if (e.PropertyName == nameof(Impact)) base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasImpact)));
        if (e.PropertyName == nameof(ValidationMessage)) base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasValidationMessage)));
    }
}
