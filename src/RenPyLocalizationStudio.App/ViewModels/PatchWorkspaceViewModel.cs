using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.App.ViewModels;

public abstract class PatchDocumentViewModel : ObservableObject
{
    private bool _isDirty;
    protected PatchDocumentViewModel(string id, string title, bool canClose)
    {
        Id = id;
        Title = title;
        CanClose = canClose;
    }
    public string Id { get; }
    public string Title { get; protected set; }
    public bool CanClose { get; }
    public bool IsDirty { get => _isDirty; protected set => SetProperty(ref _isDirty, value); }
}

public sealed class ReplacementRuleRowViewModel : ObservableObject
{
    private bool _enabled = true;
    private int _order;
    private string _source = string.Empty;
    private string _replacement = string.Empty;
    private string? _description;
    public Guid Id { get; } = Guid.NewGuid();
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public int Order { get => _order; set => SetProperty(ref _order, value); }
    public string Source { get => _source; set => SetProperty(ref _source, value); }
    public string Replacement { get => _replacement; set => SetProperty(ref _replacement, value); }
    public string? Description { get => _description; set => SetProperty(ref _description, value); }
    public ReplacementRule ToModel() => new(Id, Source, Replacement, Enabled, Order, Description);
}

public sealed class ReplacementRulesDocumentViewModel : PatchDocumentViewModel
{
    private readonly PatchWorkspaceViewModel _workspace;
    private readonly IReplacementRuleService _service;
    private ReplacementRuleRowViewModel? _selectedRule;
    private string _diagnostics = string.Empty;

    public ReplacementRulesDocumentViewModel(PatchWorkspaceViewModel workspace, IReplacementRuleService service)
        : base("replace", "Replace 规则", false)
    {
        _workspace = workspace;
        _service = service;
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(Remove, () => SelectedRule is not null);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync);
    }

    public ObservableCollection<ReplacementRuleRowViewModel> Rules { get; } = [];
    public IRelayCommand AddCommand { get; }
    public IRelayCommand RemoveCommand { get; }
    public IAsyncRelayCommand PreviewCommand { get; }
    public string Diagnostics { get => _diagnostics; private set => SetProperty(ref _diagnostics, value); }
    public ReplacementRuleRowViewModel? SelectedRule
    {
        get => _selectedRule;
        set { if (SetProperty(ref _selectedRule, value)) RemoveCommand.NotifyCanExecuteChanged(); }
    }

    private void Add()
    {
        var row = new ReplacementRuleRowViewModel { Order = Rules.Count };
        row.PropertyChanged += (_, _) => IsDirty = true;
        Rules.Add(row);
        SelectedRule = row;
        IsDirty = true;
    }

    private void Remove()
    {
        if (SelectedRule is null) return;
        Rules.Remove(SelectedRule);
        SelectedRule = null;
        IsDirty = true;
    }

    private async Task PreviewAsync()
    {
        var indexed = _workspace.Session.Snapshot?.TranslationUnits
            .Select(x => x.OldText ?? x.OriginalStatement ?? string.Empty).Where(x => x.Length > 0).ToArray() ?? [];
        var result = _service.Validate(new ReplacementValidationRequest(Rules.Select(x => x.ToModel()).ToArray(), indexed));
        Diagnostics = string.Join(Environment.NewLine, result.Diagnostics.Select(x => $"[{x.Code}] {x.Message}"));
        if (!result.IsSuccess || result.Value is null) return;
        var code = _service.GenerateRenPyCode(_workspace.Session.Language, result.Value.OrderedRules);
        await _workspace.PreviewModuleAsync("replace", new PatchModule(PatchModuleKind.ReplacementRules, code));
    }
}

public sealed class ZzzDocumentViewModel : PatchDocumentViewModel
{
    private readonly PatchWorkspaceViewModel _workspace;
    private bool _includeDefaultLanguage = true;
    private bool _includePreferences = true;
    private bool _includeFont;
    private string _nativeLanguageName = "简体中文";
    private string _fontPath = string.Empty;
    private string _customCode = string.Empty;

    public ZzzDocumentViewModel(PatchWorkspaceViewModel workspace) : base("zzz", "zzz.rpy 向导", false)
    {
        _workspace = workspace;
        PreviewCommand = new AsyncRelayCommand(PreviewAsync);
    }
    public IAsyncRelayCommand PreviewCommand { get; }
    public bool IncludeDefaultLanguage { get => _includeDefaultLanguage; set { if (SetProperty(ref _includeDefaultLanguage, value)) IsDirty = true; } }
    public bool IncludePreferences { get => _includePreferences; set { if (SetProperty(ref _includePreferences, value)) IsDirty = true; } }
    public bool IncludeFont { get => _includeFont; set { if (SetProperty(ref _includeFont, value)) IsDirty = true; } }
    public string NativeLanguageName { get => _nativeLanguageName; set { if (SetProperty(ref _nativeLanguageName, value)) IsDirty = true; } }
    public string FontPath { get => _fontPath; set { if (SetProperty(ref _fontPath, value)) IsDirty = true; } }
    public string CustomCode { get => _customCode; set { if (SetProperty(ref _customCode, value)) IsDirty = true; } }

    private async Task PreviewAsync()
    {
        var modules = new List<PatchModule>();
        var patch = _workspace.PatchService;
        var language = _workspace.Session.Language;
        if (IncludeDefaultLanguage) modules.Add(patch.CreateDefaultLanguageModule(language));
        if (IncludePreferences) modules.Add(patch.CreatePreferencesLanguageModule(language, NativeLanguageName));
        if (IncludeFont && !string.IsNullOrWhiteSpace(FontPath)) modules.Add(patch.CreateFontModule(language, FontPath));
        if (!string.IsNullOrWhiteSpace(CustomCode)) modules.Add(patch.CreateCustomCodeModule(CustomCode));
        await _workspace.PreviewModulesAsync("zzz", modules);
    }
}

public sealed class DiffDocumentViewModel : PatchDocumentViewModel
{
    private readonly PatchWorkspaceViewModel _workspace;
    private ManagedPatchRequest _request;
    private string _originalText;
    private string _updatedText;
    public DiffDocumentViewModel(PatchWorkspaceViewModel workspace, string id, string title, ManagedPatchRequest request, ManagedPatchPreview preview)
        : base(id, title, true)
    {
        _workspace = workspace;
        _request = request;
        _originalText = preview.OriginalText;
        _updatedText = preview.UpdatedText;
        IsDirty = _originalText != _updatedText;
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => IsDirty);
    }
    public string OriginalText { get => _originalText; private set => SetProperty(ref _originalText, value); }
    public string UpdatedText { get => _updatedText; private set => SetProperty(ref _updatedText, value); }
    internal ManagedPatchRequest Request => _request;
    internal ManagedPatchPreview Preview => new(_request.RelativePath, OriginalText, UpdatedText, [], false, Environment.NewLine);
    public IAsyncRelayCommand ApplyCommand { get; }

    public void Update(ManagedPatchRequest request, ManagedPatchPreview preview)
    {
        _request = request;
        OriginalText = preview.OriginalText;
        UpdatedText = preview.UpdatedText;
        IsDirty = OriginalText != UpdatedText;
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private async Task ApplyAsync()
    {
        if (!_workspace.Confirmation.Confirm("应用补丁", $"将安全写入 {_request.RelativePath}，确认继续？")) return;
        await _workspace.Session.Tasks.RunAsync("正在写入受管补丁……", async token =>
        {
            var result = await _workspace.PatchService.ExecuteAsync(new ManagedPatchWriteRequest(_request, true), _workspace.Session.Tasks.CreateProgress(), token);
            if (!result.IsSuccess) _workspace.Confirmation.ShowDiagnostics("补丁写入失败", result.Diagnostics);
            else
            {
                IsDirty = false;
                ApplyCommand.NotifyCanExecuteChanged();
                _workspace.Session.Tasks.StatusMessage = $"{_request.RelativePath} 已安全更新并创建备份。";
            }
        });
    }
}

public sealed class PatchDocumentHostViewModel : WorkspaceMainContentViewModelBase
{
    private readonly IConfirmationService _confirmation;
    private PatchDocumentViewModel? _selectedDocument;
    public PatchDocumentHostViewModel(IConfirmationService confirmation)
    {
        _confirmation = confirmation;
        CloseDocumentCommand = new RelayCommand<PatchDocumentViewModel>(CloseDocument);
    }
    public ObservableCollection<PatchDocumentViewModel> Documents { get; } = [];
    public PatchDocumentViewModel? SelectedDocument { get => _selectedDocument; set => SetProperty(ref _selectedDocument, value); }
    public IRelayCommand<PatchDocumentViewModel> CloseDocumentCommand { get; }

    public void AddOrActivate(PatchDocumentViewModel document)
    {
        var existing = Documents.FirstOrDefault(x => x.Id == document.Id);
        if (existing is DiffDocumentViewModel current && document is DiffDocumentViewModel incoming)
        {
            current.Update(incoming.Request, incoming.Preview);
            SelectedDocument = current;
            return;
        }
        if (existing is null) Documents.Add(document);
        SelectedDocument = existing ?? document;
    }

    private void CloseDocument(PatchDocumentViewModel? document)
    {
        if (document is null || !document.CanClose) return;
        if (document.IsDirty && !_confirmation.Confirm("关闭预览", "此 Diff 尚未应用，仍要关闭吗？")) return;
        Documents.Remove(document);
        if (ReferenceEquals(SelectedDocument, document)) SelectedDocument = Documents.LastOrDefault();
    }
}

public sealed class PatchInspectorViewModel : WorkspaceInspectorViewModelBase
{
    private PatchDocumentViewModel? _document;
    public PatchDocumentViewModel? Document { get => _document; set => SetProperty(ref _document, value); }
}

public sealed class PatchWorkspaceViewModel : WorkspaceViewModelBase
{
    private readonly IFileSystemService _fileSystem;
    public PatchWorkspaceViewModel(ProjectSessionViewModel session, IFileSystemService fileSystem, IManagedPatchService patch, IReplacementRuleService replacement, IConfirmationService confirmation)
        : base("patch", "补丁", "\uE70F")
    {
        Session = session;
        _fileSystem = fileSystem;
        PatchService = patch;
        Confirmation = confirmation;
        Sidebar = new SimpleSidebarViewModel { Title = "补丁", Subtitle = "Replace、zzz.rpy 与差异预览" };
        Sidebar.Items.Add(new SidebarOption("replace", "Replace 规则", "\uE8D7"));
        Sidebar.Items.Add(new SidebarOption("zzz", "zzz.rpy 向导", "\uE943"));
        Sidebar.SelectedItem = Sidebar.Items[0];
        Main = new PatchDocumentHostViewModel(confirmation);
        Inspector = new PatchInspectorViewModel();
        Main.Documents.Add(new ReplacementRulesDocumentViewModel(this, replacement));
        Main.Documents.Add(new ZzzDocumentViewModel(this));
        Main.SelectedDocument = Main.Documents[0];
        Sidebar.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(SimpleSidebarViewModel.SelectedItem) || Sidebar.SelectedItem is null) return;
            Main.SelectedDocument = Main.Documents.FirstOrDefault(x => x.Id == Sidebar.SelectedItem.Id) ?? Main.SelectedDocument;
        };
        Main.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(PatchDocumentHostViewModel.SelectedDocument)) return;
            Inspector.Document = Main.SelectedDocument;
            var matching = Sidebar.Items.FirstOrDefault(x => x.Id == Main.SelectedDocument?.Id);
            if (matching is not null && !ReferenceEquals(Sidebar.SelectedItem, matching)) Sidebar.SelectedItem = matching;
        };
    }

    public ProjectSessionViewModel Session { get; }
    public IManagedPatchService PatchService { get; }
    public IConfirmationService Confirmation { get; }
    public SimpleSidebarViewModel Sidebar { get; }
    public PatchDocumentHostViewModel Main { get; }
    public PatchInspectorViewModel Inspector { get; }
    public override WorkspaceSidebarViewModelBase SidebarContent => Sidebar;
    public override WorkspaceMainContentViewModelBase MainContent => Main;
    public override WorkspaceInspectorViewModelBase InspectorContent => Inspector;

    public Task PreviewModuleAsync(string sourceId, PatchModule module) => PreviewModulesAsync(sourceId, [module]);

    public async Task PreviewModulesAsync(string sourceId, IReadOnlyList<PatchModule> modules)
    {
        var root = _fileSystem.ValidateProjectRoot(Session.ProjectPath);
        if (!root.IsSuccess || root.Value is null) { Confirmation.ShowDiagnostics("项目路径无效", root.Diagnostics); return; }
        var relative = $"game/tl/{Session.Language}/zzz.rpy";
        var request = new ManagedPatchRequest(root.Value, relative, modules);
        await Session.Tasks.RunAsync("正在生成补丁差异……", async token =>
        {
            var preview = await PatchService.PreviewAsync(request, Session.Tasks.CreateProgress(), token);
            if (!preview.IsSuccess || preview.Value is null) { Confirmation.ShowDiagnostics("补丁预览失败", preview.Diagnostics); return; }
            var document = new DiffDocumentViewModel(this, $"diff:{sourceId}:{relative}", $"Diff · {sourceId}", request, preview.Value);
            Main.AddOrActivate(document);
            Inspector.Document = document;
            Session.Tasks.StatusMessage = preview.Value.OriginalText == preview.Value.UpdatedText ? "补丁已是最新状态。" : "差异已生成，请审阅后应用。";
        });
    }
}
