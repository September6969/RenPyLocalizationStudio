namespace RenPyLocalizationStudio.Core;

public enum MergeChoice { Unresolved, Disk, Local }
public sealed record ExternalMergeRow(string Key, TranslationReadEntry? Local, TranslationReadEntry? Disk,
    string Baseline, MergeChoice DefaultChoice, bool AllowLocal, string Reason);

public static class ExternalTranslationMerge
{
    public static IReadOnlyList<TranslationReadEntry> Capture(ProjectSnapshot snapshot) =>
        TranslationReadSnapshot.Capture(snapshot).Select(row => row.Raw ? row with { Translation = row.Unit!.RawBodyText } : row).ToArray();

    public static IReadOnlyDictionary<string, string> Baselines(ProjectSnapshot snapshot)
    {
        var baseline = new ProjectSnapshot { ProjectRoot = snapshot.ProjectRoot, GameDirectory = snapshot.GameDirectory, Language = snapshot.Language };
        var parser = new TlParser();
        foreach (var document in snapshot.TlDocuments)
        {
            var file = document.File;
            var copy = new Utf8TextFile
            {
                FullPath = file.FullPath,
                Text = string.IsNullOrEmpty(document.BaselineText) ? file.Text : document.BaselineText,
                HasBom = file.HasBom,
                NewLine = file.NewLine,
                HasFinalNewLine = file.HasFinalNewLine,
                Sha256 = document.BaselineSha256
            };
            baseline.TlDocuments.Add(parser.Parse(copy, document.RelativePath, snapshot.Language));
        }
        foreach (var group in baseline.TranslationUnits.Where(u => u.Kind == TranslationUnitKind.String && u.OldText is not null).GroupBy(u => u.OldText!))
        {
            var shared = new SharedStringEntry { Language = snapshot.Language, OldText = group.Key };
            shared.Definitions.AddRange(group);
            shared.LoadTranslation(group.First().TranslationText);
            baseline.SharedStrings.Add(shared);
        }
        return Capture(baseline).GroupBy(TranslationIdentity.Key).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Translation);
    }

    public static IReadOnlyList<ExternalMergeRow> Plan(IReadOnlyList<TranslationReadEntry> local, IReadOnlyList<TranslationReadEntry> disk,
        IReadOnlyDictionary<string, string> baselines)
    {
        var left = local.ToLookup(TranslationIdentity.Key);
        var right = disk.ToLookup(TranslationIdentity.Key);
        if (left.Any(g => g.Count() > 1) || right.Any(g => g.Count() > 1)) throw new InvalidDataException("存在重复条目标识，无法安全对比。请先修复重复定义。");
        var rows = new List<ExternalMergeRow>();
        foreach (var key in left.Select(g => g.Key).Union(right.Select(g => g.Key), StringComparer.Ordinal))
        {
            var l = left[key].SingleOrDefault(); var d = right[key].SingleOrDefault();
            var baseline = baselines.GetValueOrDefault(key, "");
            if (l is not null && d is not null && l.Translation == d.Translation && l.Original == d.Original && l.Conflict == d.Conflict) continue;
            var allow = l is not null && d is not null && l.Original == d.Original && l.Writable && d.Writable && !l.Raw && !d.Raw && !l.Conflict && !d.Conflict
                && new TranslationValidator().Validate(d.Original ?? "", l.Translation).Count == 0;
            var localChanged = l is not null && l.Translation != baseline;
            var diskChanged = d is null || d.Translation != baseline || d.Original != l?.Original;
            var choice = !localChanged ? MergeChoice.Disk : !diskChanged && allow ? MergeChoice.Local : MergeChoice.Unresolved;
            rows.Add(new(key, l, d, baseline, choice, allow,
                d is null ? "磁盘已删除；选择磁盘将放弃该条本地编辑" : l is null ? "磁盘新增" : l.Original != d.Original ? "原文已变化，仅可采用磁盘；本地译文仍显示供复制" :
                !allow ? "复杂块、冲突或结构校验未通过，仅可采用磁盘" : localChanged && diskChanged ? "双方均有修改，请选择保留版本" : "可保留单方修改"));
        }
        return rows;
    }
}
