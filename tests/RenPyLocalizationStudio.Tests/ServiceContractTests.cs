using System.Text;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.Tests;

public sealed class ServiceContractTests
{
    [Fact]
    public void FileSystem_RejectsDirectoryTraversal()
    {
        using var project = new TemporaryProject();
        var service = new FileSystemService();
        var root = service.ValidateProjectRoot(project.Root);

        var result = service.ValidateProjectPath(root.Value!, "..\\outside.rpy");

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, x => x.Code == "PATH_TRAVERSAL");
    }

    [Fact]
    public async Task AtomicWrite_PreservesTargetAndCreatesBackup()
    {
        using var project = new TemporaryProject();
        var service = new FileSystemService();
        var root = service.ValidateProjectRoot(project.Root).Value!;
        var target = service.ValidateProjectPath(root, "game\\tl\\schinese\\zzz.rpy").Value!;
        Directory.CreateDirectory(Path.GetDirectoryName(target.FullPath)!);
        await File.WriteAllTextAsync(target.FullPath, "old", new UTF8Encoding(false));
        var original = await Utf8TextFile.ReadAsync(target.FullPath);

        var result = await service.AtomicWriteAsync(
            new AtomicWriteRequest(target, Encoding.UTF8.GetBytes("new"), original.Sha256),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("new", await File.ReadAllTextAsync(target.FullPath));
        Assert.Equal("old", await File.ReadAllTextAsync(target.FullPath + ".rls.bak"));
    }

    [Fact]
    public void ReplacementRules_ReportDuplicateAndCycle()
    {
        var service = new ReplacementRuleService();
        var rules = new[]
        {
            new ReplacementRule(Guid.NewGuid(), "a", "b", true, 0),
            new ReplacementRule(Guid.NewGuid(), "b", "a", true, 1),
            new ReplacementRule(Guid.NewGuid(), "a", "c", true, 2)
        };

        var result = service.Validate(new ReplacementValidationRequest(rules, ["a b"]));

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, x => x.Code == "REPLACE_DUPLICATE");
        Assert.Contains(result.Diagnostics, x => x.Code == "REPLACE_CYCLE");
    }

    [Fact]
    public async Task ManagedPatch_ReplacesOnlyOwnedBlock()
    {
        using var project = new TemporaryProject();
        var fs = new FileSystemService();
        var service = new ManagedPatchService(fs);
        var root = fs.ValidateProjectRoot(project.Root).Value!;
        var relative = "game\\tl\\schinese\\zzz.rpy";
        var full = Path.Combine(project.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, "# 用户注释\n# RLS-ZZZ-BEGIN DEFAULTLANGUAGE\ndefine config.default_language = \"old\"\n# RLS-ZZZ-END DEFAULTLANGUAGE\n# 保留", new UTF8Encoding(false));

        var preview = await service.PreviewAsync(new ManagedPatchRequest(root, relative, [service.CreateDefaultLanguageModule("schinese")]),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.True(preview.IsSuccess);
        Assert.Contains("# 用户注释", preview.Value!.UpdatedText);
        Assert.Contains("# 保留", preview.Value.UpdatedText);
        Assert.Contains("config.default_language = \"schinese\"", preview.Value.UpdatedText);
    }

    [Fact]
    public void FlowProjection_GroupsByFileAndLabel()
    {
        var snapshot = new ProjectSnapshot { ProjectRoot = "X", GameDirectory = "X/game", Language = "schinese" };
        var label = new FlowNode { Id = "l", Kind = FlowNodeKind.Label, DisplayText = "label start", Region = new SourceRegion("game/a.rpy", 1, 1), LabelName = "start", Indent = 0 };
        var dialogue = new FlowNode { Id = "d", Kind = FlowNodeKind.Dialogue, DisplayText = "hello", Region = new SourceRegion("game/a.rpy", 2, 2), ParentId = "l", Indent = 4 };
        snapshot.Graph.Nodes.AddRange([label, dialogue]); snapshot.Graph.Labels["start"] = label;

        Assert.All(FlowProjectionService.Project(snapshot, FlowGroupingMode.SourceFile), x => Assert.Equal("game/a.rpy", x.GroupKey));
        Assert.All(FlowProjectionService.Project(snapshot, FlowGroupingMode.Label), x => Assert.Equal("start", x.GroupKey));
    }

    [Fact]
    public void FlowProjection_文件与Label模式生成可见分组()
    {
        var snapshot = new ProjectSnapshot { ProjectRoot = "X", GameDirectory = "X/game", Language = "schinese" };
        var firstLabel = new FlowNode { Id = "l1", Kind = FlowNodeKind.Label, DisplayText = "label start", Region = new SourceRegion("game/a.rpy", 1, 1), LabelName = "start", Indent = 0 };
        var firstDialogue = new FlowNode { Id = "d1", Kind = FlowNodeKind.Dialogue, DisplayText = "hello", Region = new SourceRegion("game/a.rpy", 2, 2), ParentId = "l1", Indent = 4 };
        var secondLabel = new FlowNode { Id = "l2", Kind = FlowNodeKind.Label, DisplayText = "label room", Region = new SourceRegion("game/b.rpy", 1, 1), LabelName = "room", Indent = 0 };
        snapshot.Graph.Nodes.AddRange([firstLabel, firstDialogue, secondLabel]);
        snapshot.Graph.Labels["start"] = firstLabel;
        snapshot.Graph.Labels["room"] = secondLabel;

        var fileGroups = FlowProjectionService.ProjectGroups(snapshot, FlowGroupingMode.SourceFile);
        var labelGroups = FlowProjectionService.ProjectGroups(snapshot, FlowGroupingMode.Label);

        Assert.Equal(["game/a.rpy", "game/b.rpy"], fileGroups.Select(x => x.Title));
        Assert.Equal(["start", "room"], labelGroups.Select(x => x.Title));
    }

    private sealed class TemporaryProject : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rls-tests", Guid.NewGuid().ToString("N"));
        public TemporaryProject() { Directory.CreateDirectory(Path.Combine(Root, "game")); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
