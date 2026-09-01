using System.Text;
using RenPyLocalizationStudio.Core;
using RenPyLocalizationStudio.Core.Services;

namespace RenPyLocalizationStudio.Tests;

public sealed class ServiceContractTests
{
    [Fact]
    public void OperationResult_成功工厂遇到错误诊断时返回失败()
    {
        var result = OperationResult<string>.Success("partial", [
            new Diagnostic(DiagnosticSeverity.Error, "TEST_ERROR", "测试错误")
        ]);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Equal("partial", result.Value);
    }

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
    public async Task AtomicWrite_预期文件被删除时拒绝写入()
    {
        using var project = new TemporaryProject();
        var service = new FileSystemService();
        var root = service.ValidateProjectRoot(project.Root).Value!;
        var target = service.ValidateProjectPath(root, "game\\tl\\schinese\\zzz.rpy").Value!;
        Directory.CreateDirectory(Path.GetDirectoryName(target.FullPath)!);
        await File.WriteAllTextAsync(target.FullPath, "old", new UTF8Encoding(false));
        var original = await Utf8TextFile.ReadAsync(target.FullPath);
        File.Delete(target.FullPath);

        var result = await service.AtomicWriteAsync(
            new AtomicWriteRequest(target, Encoding.UTF8.GetBytes("new"), original.Sha256),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, x => x.Code == "EXTERNAL_MODIFICATION");
        Assert.False(File.Exists(target.FullPath));
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
    public void ReplacementRules_检测三节点循环链式替换与占位符变化()
    {
        var rules = new[]
        {
            new ReplacementRule(Guid.NewGuid(), "A [name]", "B [other]", true, 0),
            new ReplacementRule(Guid.NewGuid(), "B", "C", true, 1),
            new ReplacementRule(Guid.NewGuid(), "C", "A [name]", true, 2)
        };

        var result = new ReplacementRuleService().Validate(new ReplacementValidationRequest(rules, ["A [name]"]));

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "REPLACE_CYCLE");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "REPLACE_CHAIN");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "REPLACE_INTERPOLATION_MISMATCH");
    }

    [Fact]
    public void ReplacementRules_生成代码会串联项目已有ReplaceHook()
    {
        var service = new ReplacementRuleService();
        var code = service.GenerateRenPyCode("schinese", [
            new ReplacementRule(Guid.NewGuid(), "A", "B", true, 0)
        ]);

        Assert.Contains("_rls_previous_replace_text = config.replace_text", code);
        Assert.Contains("s = previous(s)", code);
        Assert.EndsWith("config.replace_text = rls_replace_text", code);
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
    public async Task ManagedPatch_预览后外部修改会阻止应用()
    {
        using var project = new TemporaryProject();
        var fs = new FileSystemService();
        var service = new ManagedPatchService(fs);
        var root = fs.ValidateProjectRoot(project.Root).Value!;
        var relative = "game\\tl\\schinese\\zzz.rpy";
        var full = Path.Combine(project.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, "# 原始内容\n", new UTF8Encoding(false));

        var preview = await service.PreviewAsync(new ManagedPatchRequest(root, relative, [service.CreateDefaultLanguageModule("schinese")]),
            new Progress<ToolOperationProgress>(), CancellationToken.None);
        Assert.True(preview.IsSuccess);
        await File.WriteAllTextAsync(full, "# 外部修改\n", new UTF8Encoding(false));

        var request = new ManagedPatchRequest(root, relative, [service.CreateDefaultLanguageModule("schinese")], preview.Value!.OriginalSha256);
        var result = await service.ExecuteAsync(new ManagedPatchWriteRequest(request, true),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, x => x.Code == "EXTERNAL_MODIFICATION");
        Assert.Equal("# 外部修改\n", await File.ReadAllTextAsync(full));
    }

    [Fact]
    public async Task ManagedPatch_禁用模块只删除对应受管区块()
    {
        using var project = new TemporaryProject();
        var fs = new FileSystemService();
        var service = new ManagedPatchService(fs);
        var root = fs.ValidateProjectRoot(project.Root).Value!;
        var relative = "game\\tl\\schinese\\zzz.rpy";
        var full = Path.Combine(project.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full,
            "# 用户代码\n# RLS-ZZZ-BEGIN DEFAULTLANGUAGE\ndefine config.default_language = \"schinese\"\n# RLS-ZZZ-END DEFAULTLANGUAGE\n# RLS-ZZZ-BEGIN CUSTOMCODE\ncustom\n# RLS-ZZZ-END CUSTOMCODE\n# 末尾注释\n",
            new UTF8Encoding(false));

        var request = new ManagedPatchRequest(root, relative, [
            new PatchModule(PatchModuleKind.DefaultLanguage, "ignored", Enabled: false)
        ]);
        var result = await service.ExecuteAsync(new ManagedPatchWriteRequest(request, true),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var text = await File.ReadAllTextAsync(full);
        Assert.DoesNotContain("DEFAULTLANGUAGE", text);
        Assert.Contains("CUSTOMCODE", text);
        Assert.Contains("# 用户代码", text);
    }

    [Theory]
    [InlineData("# RLS-ZZZ-BEGIN CUSTOMCODE\ncode\n", "PATCH_ORPHAN_BEGIN_MARKER")]
    [InlineData("# RLS-ZZZ-END CUSTOMCODE\n", "PATCH_ORPHAN_END_MARKER")]
    [InlineData("# RLS-ZZZ-BEGIN CUSTOMCODE\n# RLS-ZZZ-END DEFAULTLANGUAGE\n", "PATCH_MISMATCHED_MARKER")]
    public async Task ManagedPatch_孤立或错配标记阻断写入(string content, string code)
    {
        using var project = new TemporaryProject();
        var fs = new FileSystemService();
        var service = new ManagedPatchService(fs);
        var root = fs.ValidateProjectRoot(project.Root).Value!;
        var relative = "game\\tl\\schinese\\zzz.rpy";
        var full = Path.Combine(project.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, new UTF8Encoding(false));

        var preview = await service.PreviewAsync(new ManagedPatchRequest(root, relative, [service.CreateCustomCodeModule("new")]),
            new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, preview.Status);
        Assert.Contains(preview.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.Equal(content, await File.ReadAllTextAsync(full));
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

    [Fact]
    public void FlowProjection_StoryPath按控制流边遍历而非文件行号排序()
    {
        var snapshot = new ProjectSnapshot { ProjectRoot = "X", GameDirectory = "X/game", Language = "schinese" };
        var start = new FlowNode { Id = "start", Kind = FlowNodeKind.Label, DisplayText = "start", Region = new SourceRegion("game/z.rpy", 10, 10), LabelName = "start" };
        var jump = new FlowNode { Id = "jump", Kind = FlowNodeKind.Jump, DisplayText = "jump target", Region = new SourceRegion("game/z.rpy", 11, 11), Target = "target", ParentId = "start" };
        var target = new FlowNode { Id = "target", Kind = FlowNodeKind.Label, DisplayText = "target", Region = new SourceRegion("game/a.rpy", 1, 1), LabelName = "target" };
        snapshot.Graph.Nodes.AddRange([target, start, jump]);
        snapshot.Graph.Labels["start"] = start;
        snapshot.Graph.Labels["target"] = target;
        snapshot.Graph.Edges.Add(new FlowEdge(start.Id, jump.Id, FlowEdgeKind.Sequence));
        snapshot.Graph.Edges.Add(new FlowEdge(jump.Id, target.Id, FlowEdgeKind.Jump));

        var rows = FlowProjectionService.Project(snapshot, FlowGroupingMode.StoryPath);

        Assert.Equal(["start", "jump", "target"], rows.Select(row => row.Key));
    }

    [Fact]
    public async Task ExtraText_空New或缺失New不算已翻译()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddText("game/script.rpy", "define e = Character(\"Eileen\")\n");
        fileSystem.AddText("game/tl/schinese/strings.rpy", "translate schinese strings:\n    old \"Eileen\"\n    new \"\"\n");
        var root = fileSystem.ValidateProjectRoot("C:\\project").Value!;

        var result = await new ExtraTextScanService(fileSystem).ExecuteAsync(
            new ExtraTextScanRequest(root, "schinese"), new Progress<ToolOperationProgress>(), CancellationToken.None);

        var candidate = Assert.Single(result.Value!.Candidates, item => item.Text == "Eileen");
        Assert.False(candidate.AlreadyTranslated);
    }

    [Fact]
    public async Task ExtraText_直接选择Game目录时支持单引号和多行调用()
    {
        var fileSystem = new InMemoryFileSystem("C:\\game");
        fileSystem.AddText("script.rpy", "define e = Character(\n    'Eileen'\n)\n$ renpy.notify('Ready')\n");
        fileSystem.AddText("tl/schinese/strings.rpy", "translate schinese strings:\n    old \"Ready\"\n    new \"就绪\"\n");
        var root = fileSystem.ValidateProjectRoot("C:\\game").Value!;

        var result = await new ExtraTextScanService(fileSystem).ExecuteAsync(
            new ExtraTextScanRequest(root, "schinese"), new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value!.Candidates, item => item.Text == "Eileen" && !item.AlreadyTranslated);
        Assert.Contains(result.Value.Candidates, item => item.Text == "Ready" && item.AlreadyTranslated);
    }

    [Fact]
    public async Task ExtraText_扫描Python静态字符串并标记动态拼接()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.AddText("game/script.rpy", "python:\n    title = 'Static title'\n$ message = f'Hello [name]' + suffix\n");
        var root = fileSystem.ValidateProjectRoot("C:\\project").Value!;

        var result = await new ExtraTextScanService(fileSystem).ExecuteAsync(
            new ExtraTextScanRequest(root, "schinese"), new Progress<ToolOperationProgress>(), CancellationToken.None);

        Assert.Contains(result.Value!.Candidates, item => item.Text == "Static title" && item.Kind == ExtraTextKind.Python);
        Assert.Contains(result.Value.Candidates, item => item.Text == "Hello [name]" && item.Kind == ExtraTextKind.Dynamic);
    }

    private sealed class TemporaryProject : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rls-tests", Guid.NewGuid().ToString("N"));
        public TemporaryProject() { Directory.CreateDirectory(Path.Combine(Root, "game")); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
