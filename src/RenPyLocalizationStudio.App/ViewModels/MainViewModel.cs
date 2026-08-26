using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using RenPyLocalizationStudio.App.Services;
using System.Windows.Media;

namespace RenPyLocalizationStudio.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IRecipient<NavigateToSourceRequestMessage>
{
    private readonly IThemeService _theme;
    private WorkspaceViewModelBase? _currentWorkspace;
    private string _accentColor;
    private Brush _accentPreviewBrush;

    public MainViewModel(
        ProjectSessionViewModel session,
        TaskCenterViewModel tasks,
        IThemeService theme,
        IMessenger messenger,
        TranslationWorkspaceViewModel translation,
        TlWorkspaceViewModel tl,
        ExtraTextWorkspaceViewModel extra,
        PatchWorkspaceViewModel patch,
        ArchiveWorkspaceViewModel archive,
        DiagnosticsWorkspaceViewModel diagnostics)
    {
        Session = session;
        Tasks = tasks;
        _theme = theme;
        TranslationWorkspace = translation;
        Workspaces = [translation, tl, extra, patch, archive, diagnostics];
        _currentWorkspace = translation;
        _accentColor = theme.AccentColor;
        _accentPreviewBrush = CreateAccentBrush(_accentColor);
        ApplyAccentCommand = new AsyncRelayCommand<string?>(ApplyAccentAsync);
        MovePreviousTranslationCommand = translation.MovePreviousCommand;
        MoveNextTranslationCommand = translation.MoveNextCommand;
        messenger.Register(this);
        session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProjectSessionViewModel.ProjectName)) OnPropertyChanged(nameof(WindowTitle));
        };
    }

    public ProjectSessionViewModel Session { get; }
    public TaskCenterViewModel Tasks { get; }
    public TranslationWorkspaceViewModel TranslationWorkspace { get; }
    public ObservableCollection<WorkspaceViewModelBase> Workspaces { get; }
    public IAsyncRelayCommand<string?> ApplyAccentCommand { get; }
    public IRelayCommand MovePreviousTranslationCommand { get; }
    public IRelayCommand MoveNextTranslationCommand { get; }

    public WorkspaceViewModelBase? CurrentWorkspace
    {
        get => _currentWorkspace;
        set
        {
            if (value is null || ReferenceEquals(_currentWorkspace, value)) return;
            var previous = _currentWorkspace;
            if (!SetProperty(ref _currentWorkspace, value)) return;
            _ = ChangeWorkspaceAsync(previous, value);
        }
    }

    public string AccentColor
    {
        get => _accentColor;
        set
        {
            if (!SetProperty(ref _accentColor, value)) return;
            if (TryCreateAccentBrush(value, out var brush)) AccentPreviewBrush = brush;
        }
    }
    public Brush AccentPreviewBrush { get => _accentPreviewBrush; private set => SetProperty(ref _accentPreviewBrush, value); }
    public string WindowTitle => $"RenPy Localization Studio · {Session.ProjectName}";

    public async Task InitializeAsync()
    {
        await Session.InitializeAsync();
        AccentColor = _theme.AccentColor;
        OnPropertyChanged(nameof(WindowTitle));
    }

    public void Receive(NavigateToSourceRequestMessage message)
    {
        CurrentWorkspace = TranslationWorkspace;
        if (!TranslationWorkspace.Navigate(message.Value)) Tasks.StatusMessage = "未能在当前剧情流中定位该诊断来源。";
    }

    private static async Task ChangeWorkspaceAsync(WorkspaceViewModelBase? previous, WorkspaceViewModelBase current)
    {
        if (previous is not null) await previous.DeactivateAsync(CancellationToken.None);
        await current.ActivateAsync(CancellationToken.None);
    }

    private async Task ApplyAccentAsync(string? color)
    {
        color = string.IsNullOrWhiteSpace(color) ? AccentColor : color;
        if (!_theme.TryApplyAccent(color!, out var error))
        {
            Tasks.StatusMessage = error ?? "强调色无效。";
            return;
        }
        AccentColor = _theme.AccentColor;
        await Session.PersistSettingsAsync();
    }

    private static Brush CreateAccentBrush(string color) =>
        TryCreateAccentBrush(color, out var brush) ? brush : Brushes.Transparent;

    private static bool TryCreateAccentBrush(string? color, out Brush brush)
    {
        brush = Brushes.Transparent;
        try
        {
            if (ColorConverter.ConvertFromString(color) is not Color parsed) return false;
            var result = new SolidColorBrush(parsed);
            result.Freeze();
            brush = result;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
