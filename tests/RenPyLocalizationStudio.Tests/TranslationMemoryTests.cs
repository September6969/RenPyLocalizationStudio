using RenPyLocalizationStudio.Core;

namespace RenPyLocalizationStudio.Tests;

public sealed class TranslationMemoryTests
{
    [Fact]
    public async Task 同原文保留不同译法并按出现次数排序且排除当前条目()
    {
        await using var project = await CreateProjectAsync();
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var target = snapshot.TranslationUnits.Single(unit => unit.Identifier == "start_4");
        var index = new TranslationMemoryIndex(snapshot);

        var suggestions = index.Find(target);

        Assert.Equal(["你好 [name]", "您好 [name]"], suggestions.Select(item => item.Translation));
        Assert.Equal([2, 1], suggestions.Select(item => item.Occurrences));
        Assert.All(suggestions, item => Assert.StartsWith("tl/schinese/script.rpy:", item.ExampleLocation));
        target.TranslationText = "仅当前条目的译文 [name]";
        Assert.DoesNotContain(index.Find(target), item => item.Translation == target.TranslationText);
    }

    [Fact]
    public async Task 索引读取未保存的新译文并排除空白和占位符错误()
    {
        await using var project = await CreateProjectAsync();
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var index = new TranslationMemoryIndex(snapshot);
        var target = snapshot.TranslationUnits.Single(unit => unit.Identifier == "start_4");
        var donors = snapshot.TranslationUnits.Where(unit => unit.Identifier != "start_4").ToArray();
        donors[0].TranslationText = "刚修改的译文 [name]";
        donors[1].TranslationText = "缺失插值";
        donors[2].TranslationText = "   ";

        var suggestion = Assert.Single(index.Find(target));
        Assert.Equal("刚修改的译文 [name]", suggestion.Translation);
    }

    [Fact]
    public async Task 原文精确匹配且不跨语言或快照复用()
    {
        await using var project = await CreateProjectAsync();
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var target = snapshot.TranslationUnits.Last();
        var differentCase = CloneTarget(target, "hello [name]", "schinese");
        var differentWhitespace = CloneTarget(target, "Hello [name] ", "schinese");
        var differentLanguage = CloneTarget(target, "Hello [name]", "english");
        snapshot.TlDocuments[0].Units.AddRange([differentCase, differentWhitespace, differentLanguage]);
        var index = new TranslationMemoryIndex(snapshot);
        Assert.Empty(index.Find(differentCase));
        Assert.Empty(index.Find(differentWhitespace));
        Assert.Empty(index.Find(differentLanguage));
        Assert.Empty(index.Find(CloneTarget(target, "Hello [name]", "schinese")));
        var empty = new ProjectSnapshot { ProjectRoot = "Other", GameDirectory = "Other/game", Language = "schinese" };
        Assert.Empty(new TranslationMemoryIndex(empty).Find(target));
    }

    [Fact]
    public async Task 共享字符串冲突必须先统一才可以复用且不重复推荐自己()
    {
        await using var project = await CreateProjectAsync();
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var target = snapshot.TranslationUnits.Last();
        foreach (var unit in snapshot.TranslationUnits) unit.TranslationText = string.Empty;
        var shared = new SharedStringEntry { Language = "schinese", OldText = "Hello [name]", HasConflict = true };
        shared.LoadTranslation("共享译文 [name]");
        snapshot.SharedStrings.Add(shared);
        var index = new TranslationMemoryIndex(snapshot);
        Assert.Empty(index.Find(target));
        shared.Unify("统一译文 [name]");
        Assert.Equal("统一译文 [name]", Assert.Single(index.Find(target)).Translation);
        Assert.Empty(index.Find(null, shared));
    }

    [Fact]
    public async Task 复杂原始块既不作为来源也不作为复用目标()
    {
        await using var project = await CreateProjectAsync();
        var snapshot = await TestFiles.AnalyzeAsync(project.Root);
        var raw = new TranslationUnit
        {
            Kind = TranslationUnitKind.Dialogue,
            Language = "schinese",
            FilePath = project.TlPath,
            RelativeTlPath = "raw.rpy",
            BlockSpan = new TextSpan(0, 1),
            HeaderLine = 1,
            OriginalStatement = "e \"Hello [name]\"",
            RawBodySpan = new TextSpan(0, 1),
            RawBodyText = "复杂代码"
        };
        snapshot.TlDocuments[0].Units.Add(raw);
        var index = new TranslationMemoryIndex(snapshot);
        Assert.Empty(index.Find(raw));
        Assert.DoesNotContain(index.Find(snapshot.TranslationUnits.First()), suggestion => suggestion.Translation == "复杂代码");
    }

    private static TranslationUnit CloneTarget(TranslationUnit target, string original, string language) => new()
    {
        Kind = TranslationUnitKind.Dialogue,
        Language = language,
        FilePath = target.FilePath,
        RelativeTlPath = target.RelativeTlPath,
        BlockSpan = target.BlockSpan,
        HeaderLine = 100,
        OriginalStatement = $"e \"{original}\"",
        TranslationValueSpan = new TextSpan(0, 0)
    };

    internal static Task<TemporaryProject> CreateProjectAsync() => TestFiles.CreateProjectAsync("""
label start:
    e "Hello [name]"
    e "Hello [name]"
    e "Hello [name]"
    e "Hello [name]"
""", """
# game/script.rpy:2
translate schinese start_1:
    # e "Hello [name]"
    e "你好 [name]"
# game/script.rpy:3
translate schinese start_2:
    # e "Hello [name]"
    e "你好 [name]"
# game/script.rpy:4
translate schinese start_3:
    # e "Hello [name]"
    e "您好 [name]"
# game/script.rpy:5
translate schinese start_4:
    # e "Hello [name]"
    e ""
""", bom: true, newLine: "\r\n");
}
