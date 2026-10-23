using System.Windows.Media;
using RenPyLocalizationStudio.App;
using RenPyLocalizationStudio.App.Services;
using RenPyLocalizationStudio.App.ViewModels;
using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.Tests;

public sealed class UiPolishTests
{
    [Theory]
    [InlineData("#D16BA5")]
    [InlineData("#9B59B6")]
    [InlineData("#3498DB")]
    [InlineData("#1ABC9C")]
    [InlineData("#2ECC71")]
    [InlineData("#E67E22")]
    [InlineData("#E74C3C")]
    [InlineData("#F1C40F")]
    public void 预设强调色的主按钮文字满足最低对比度(string value)
    {
        Assert.True(ThemeService.TryParseAccentColor(value, out var background));
        var foreground = ThemeService.AccentForeground(background);
        var a = ThemeService.RelativeLuminance(background); var b = ThemeService.RelativeLuminance(foreground);
        Assert.True((Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05) >= 4.5);
    }
    [Fact]
    public void 自定义强调色网格均有可读前景()
    {
        for (var r = 0; r <= 255; r += 51)
            for (var g = 0; g <= 255; g += 51)
                for (var b = 0; b <= 255; b += 51)
                {
                    var color = Color.FromRgb((byte)r, (byte)g, (byte)b);
                    var lum = ThemeService.RelativeLuminance(color);
                    var fg = ThemeService.RelativeLuminance(ThemeService.AccentForeground(color));
                    Assert.True((Math.Max(lum, fg) + 0.05) / (Math.Min(lum, fg) + 0.05) >= 4.5);
                }
    }
    [Theory]
    [InlineData(720)]
    [InlineData(900)]
    [InlineData(1180)]
    [InlineData(1500)]
    public void 极端首选栏宽仍保留主区且不丢失用户首选值(double width)
    {
        var preferences = new UiLayoutSettings(420, 560);
        var layout = WorkspacePaneLayout.Calculate(width, true, preferences.SidebarWidth, preferences.InspectorWidth);
        Assert.InRange(layout.InspectorWidth, 280, 560);
        Assert.True(layout.SidebarWidth == 0 || layout.SidebarWidth is >= 200 and <= 420);
        Assert.True(width - 48 - (layout.SidebarWidth > 0 ? 6 : 0) - 6 - layout.SidebarWidth - layout.InspectorWidth >= (width < 900 ? 360 : 420));
        Assert.Equal(420, preferences.SidebarWidth); Assert.Equal(560, preferences.InspectorWidth);
    }
    [Fact]
    public void 非法布局数据恢复为有效默认值()
    {
        var normalized = new UiLayoutSettings(double.NaN, double.PositiveInfinity, false, double.NaN).Normalize();
        Assert.Equal(new UiLayoutSettings(260, 360, false, 0.4), normalized);
        normalized = new UiLayoutSettings(-10, 900, true, 3).Normalize();
        Assert.Equal(new UiLayoutSettings(200, 560, true, 0.65), normalized);
    }
}

public sealed partial class ProjectSessionViewModelTests
{
    [Fact]
    public async Task 栏宽预览比例及折叠状态在重启后恢复且兼容旧设置()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        Assert.True(workspace.ImagePreview.IsCollapsed);
        session.UpdateUiLayout(new(330, 480, false, 0.3));
        Assert.False(workspace.ImagePreview.IsCollapsed);
        Assert.True(await session.PersistSettingsAsync());
        var reopened = CreateTranslationSession(project.Root);
        using var restored = new TranslationWorkspaceViewModel(reopened, new EmptyPreviewService());
        await reopened.InitializeAsync();
        Assert.Equal(new UiLayoutSettings(330, 480, false, 0.3), reopened.UiLayout);
        Assert.False(restored.ImagePreview.IsCollapsed);
    }
    [Fact]
    public async Task 筛选计数排除默认范围并发出变更通知()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        var countChanged = 0;
        workspace.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(workspace.ActiveFilterCount)) countChanged++; };
        Assert.Equal(0, workspace.ActiveFilterCount);
        workspace.SearchText = "Hello"; Assert.Equal(0, workspace.ActiveFilterCount);
        workspace.StatusFilter = TranslationStatusFilter.Completed;
        workspace.ReviewFilter = ReviewStatus.Pending;
        Assert.Equal(2, workspace.ActiveFilterCount); Assert.True(workspace.HasScopeFilters); Assert.True(countChanged >= 2);
        workspace.ClearSearchCommand.Execute(null); Assert.Equal(0, workspace.ActiveFilterCount);
    }
    [Fact]
    public async Task 保存提示随编辑和保存状态变化()
    {
        await using var project = await TranslationMemoryTests.CreateProjectAsync();
        var session = CreateTranslationSession(project.Root);
        using var workspace = new TranslationWorkspaceViewModel(session, new EmptyPreviewService());
        await session.AnalyzeAsync();
        var notified = false;
        session.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(session.HasUnsavedChanges)) notified = true; };
        workspace.SelectedItem = workspace.VisibleItems.First(i => i.Unit?.Identifier == "start_1");
        workspace.Inspector.TranslationText = "新译文 [name]";
        Assert.True(session.HasUnsavedChanges); Assert.True(notified);
        await session.SaveAsync(false); Assert.False(session.HasUnsavedChanges);
    }
}
