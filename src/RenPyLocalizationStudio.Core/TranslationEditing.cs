using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RenPyLocalizationStudio.Core;

public enum ReviewStatus { Pending, Reviewed, Discussion }
public sealed record ReviewRecord(string Original, string Translation, ReviewStatus Status, string Note);
public sealed record GlossaryTerm(string Source, string Preferred, string Forbidden = "", bool CaseSensitive = false);
public enum TranslationSearchField { All, Original, Translation, Location }
public sealed record TranslationSearchOptions(TranslationSearchField Field = TranslationSearchField.All, bool CaseSensitive = false, bool RegularExpression = false);

public static class TranslationIdentity
{
    public static string Original(TranslationUnit unit) => TranslationMemoryIndex.GetOriginal(unit) ?? unit.OriginalStatement ?? "";
    public static string Key(TranslationReadEntry row) => row.Shared is not null ? "s:" + Hash(row.Original ?? "")
        : $"d:{row.Path}:{row.Unit?.Identifier ?? row.Line?.ToString()}";
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static ReviewStatus Status(ReviewRecord? record, string original, string translation) =>
        record is not null && record.Original == original && record.Translation == translation ? record.Status : ReviewStatus.Pending;
}

public sealed class TranslationSearch
{
    private readonly string _query;
    private readonly TranslationSearchOptions _options;
    private readonly Regex? _regex;
    public TranslationSearch(string query, TranslationSearchOptions options)
    {
        _query = query;
        _options = options;
        if (query.Length > 2000) throw new ArgumentException("搜索表达式过长，请缩短到 2000 字符以内。");
        if (options.RegularExpression && query.Length > 0)
            _regex = new Regex(query, RegexOptions.CultureInvariant | (options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(100));
    }
    public bool Matches(string all, string original, string translation, string location)
    {
        var value = _options.Field switch { TranslationSearchField.Original => original, TranslationSearchField.Translation => translation, TranslationSearchField.Location => location, _ => all };
        return _query.Length == 0 || (_regex?.IsMatch(value) ?? value.Contains(_query, _options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
    }
    public string Replace(string translation, string replacement)
    {
        if (_query.Length == 0) throw new ArgumentException("替换前请输入查找内容。");
        return _regex is not null ? _regex.Replace(translation, replacement) : translation.Replace(_query, replacement,
            _options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }
}

public static class GlossaryChecker
{
    public static IReadOnlyList<Diagnostic> Check(string original, string translation, IEnumerable<GlossaryTerm> terms)
    {
        if (string.IsNullOrWhiteSpace(translation)) return [];
        var diagnostics = new List<Diagnostic>();
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term.Source) || string.IsNullOrWhiteSpace(term.Preferred)) continue;
            var comparison = term.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            // 英文词边界避免 Ann 命中 Annette；中文术语按连续文本匹配。
            var pattern = (char.IsAsciiLetterOrDigit(term.Source[0]) ? @"(?<![\p{L}\p{N}_])" : "") + Regex.Escape(term.Source)
                + (char.IsAsciiLetterOrDigit(term.Source[^1]) ? @"(?![\p{L}\p{N}_])" : "");
            if (!Regex.IsMatch(original, pattern, RegexOptions.CultureInvariant | (term.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(100))) continue;
            if (!translation.Contains(term.Preferred, comparison))
                diagnostics.Add(new(DiagnosticSeverity.Warning, "GLOSSARY_PREFERRED", $"术语 {term.Source} 推荐译为「{term.Preferred}」。", Category: DiagnosticCategory.Translation));
            foreach (var forbidden in term.Forbidden.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (translation.Contains(forbidden, comparison))
                    diagnostics.Add(new(DiagnosticSeverity.Warning, "GLOSSARY_FORBIDDEN", $"术语 {term.Source} 使用了禁用译名「{forbidden}」。", Category: DiagnosticCategory.Translation));
        }
        return diagnostics;
    }
}

public sealed record ReviewExchangeRow(string Language, string Key, string Original, string Baseline, string Translation, string Status, string Note);

/// <summary>带基线的 UTF-8 校对表，支持引号、逗号及单元格内换行；编码文本列以防表格公式执行。</summary>
public static class ReviewCsv
{
    private static readonly string[] Header = ["Language", "Key", "Original", "Baseline", "Translation", "Status", "Note"];
    public static string Write(IEnumerable<ReviewExchangeRow> rows)
    {
        var builder = new StringBuilder(string.Join(',', Header) + "\r\n");
        foreach (var row in rows)
        {
            var fields = new[] { row.Language, row.Key, row.Original, row.Baseline, row.Translation, row.Status, row.Note };
            builder.AppendLine(string.Join(',', fields.Select(value => "\"" + ("'" + value).Replace("\"", "\"\"") + "\"")));
        }
        return builder.ToString();
    }
    public static IReadOnlyList<ReviewExchangeRow> Read(string text)
    {
        var records = new List<string[]>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closedQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; closedQuote = true; }
                }
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0 && !closedQuote) { quoted = true; continue; }
            if (c == ',' || c == '\r' || c == '\n')
            {
                record.Add(field.ToString()); field.Clear(); closedQuote = false;
                if (c != ',')
                {
                    records.Add(record.ToArray()); record.Clear();
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                }
            }
            else if (closedQuote) throw new InvalidDataException("CSV 引号后存在额外字符。");
            else field.Append(c);
        }
        if (quoted) throw new InvalidDataException("CSV 引号未闭合。");
        if (field.Length > 0 || record.Count > 0 || closedQuote) { record.Add(field.ToString()); records.Add(record.ToArray()); }
        if (records.Count == 0 || !records[0].Select(s => s.TrimStart('\uFEFF')).SequenceEqual(Header)) throw new InvalidDataException("不是本工具导出的校对表，表头不匹配。");
        return records.Skip(1).Select(values =>
        {
            if (values.Length != 7) throw new InvalidDataException("CSV 列数不正确，需要 7 列。");
            var v = values.Select(value => value.StartsWith('\'') ? value[1..] : value).ToArray();
            return new ReviewExchangeRow(v[0], v[1], v[2], v[3], v[4], v[5], v[6]);
        }).ToArray();
    }
}

public static class TranslationEditPlanner
{
    public static IReadOnlyList<BulkTranslationProposal> Replace(IReadOnlyList<TranslationReadEntry> entries, TranslationSearch search, string replacement, CancellationToken token)
        => entries.Select(row =>
        {
            token.ThrowIfCancellationRequested();
            if (!row.Writable || row.Raw || row.Conflict) return new BulkTranslationProposal(row, null, "替换", "复杂块或共享冲突");
            return Proposal(row, search.Replace(row.Translation, replacement), "查找替换");
        }).ToArray();

    public static IReadOnlyList<BulkTranslationProposal> Import(IReadOnlyList<TranslationReadEntry> entries, IReadOnlyList<ReviewExchangeRow> csv, string language)
    {
        var incoming = csv.ToLookup(row => row.Key, StringComparer.Ordinal);
        var duplicates = entries.GroupBy(TranslationIdentity.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return entries.Select(row =>
        {
            var matches = incoming[TranslationIdentity.Key(row)].ToArray();
            string? reason = duplicates.Contains(TranslationIdentity.Key(row)) ? "项目条目标识重复" : matches.Length != 1 ? (matches.Length == 0 ? "表中无对应条目" : "表中条目标识重复") : null;
            var match = matches.FirstOrDefault();
            reason ??= match!.Language != language ? "目标语言不一致" : match.Original != row.Original ? "原文已变化" :
                match.Baseline != row.Translation ? "当前译文与导出基线不一致" : row.Raw || !row.Writable || row.Conflict ? "复杂块或共享冲突" : null;
            if (reason is not null) return new BulkTranslationProposal(row, null, "校对表回填", reason);
            var source = $"校对表回填 · 状态：{match!.Status} · 备注：{match.Note}";
            return row.Translation == match.Translation ? new(row, match.Translation, source, null) : Proposal(row, match.Translation, source);
        }).ToArray();
    }

    public static IReadOnlyList<BulkTranslationProposal> Migrate(IReadOnlyList<TranslationReadEntry> entries, IReadOnlyList<ReviewExchangeRow> csv, string language, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var valid = new TranslationValidator();
        var donors = csv.Where(r => { token.ThrowIfCancellationRequested(); return r.Language == language && !string.IsNullOrWhiteSpace(r.Translation) && valid.Validate(r.Original, r.Translation).Count == 0; })
            .DistinctBy(r => (r.Original, r.Translation)).ToArray();
        static HashSet<string> Grams(string text) => Enumerable.Range(0, Math.Max(0, text.Length - 1))
            .Select(i => text.Substring(i, 2)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var grams = donors.Select(d => Grams(d.Original)).ToArray();
        var inverted = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < donors.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            foreach (var gram in grams[i])
            {
                if (!inverted.TryGetValue(gram, out var ids)) inverted[gram] = ids = [];
                ids.Add(i);
            }
        }
        var exact = donors.Select((d, i) => (d.Original, i)).ToLookup(d => d.Original, d => d.i, StringComparer.Ordinal);
        var cache = new Dictionary<string, (ReviewExchangeRow Donor, double Score)[]>(StringComparer.Ordinal);
        var result = new List<BulkTranslationProposal>();
        foreach (var row in entries.Where(r => r.Writable && !r.Raw && !r.Conflict && string.IsNullOrWhiteSpace(r.Translation)))
        {
            token.ThrowIfCancellationRequested();
            var original = row.Original ?? "";
            if (!cache.TryGetValue(original, out var candidates))
            {
                var query = Grams(original);
                // 用稀有二元组检索有界候选，避免旧版和新版全量逐对比较。
                var shortlist = exact[original].Concat(query.Where(inverted.ContainsKey).OrderBy(g => inverted[g].Count).Take(6)
                    .SelectMany(g => inverted[g])).Distinct().Take(1000).ToArray();
                candidates = shortlist.Select(i => (Donor: donors[i], Score: original == donors[i].Original ? 1d :
                        query.Count + grams[i].Count == 0 ? 0 : 2d * query.Count(grams[i].Contains) / (query.Count + grams[i].Count)))
                    .Where(d => d.Score >= 0.65).OrderByDescending(d => d.Score).ThenBy(d => d.Donor.Original, StringComparer.Ordinal).Take(3).ToArray();
                cache[original] = candidates;
            }
            if (candidates.Length == 0) result.Add(new(row, null, "旧版校对表", "无相似候选"));
            foreach (var candidate in candidates)
                result.Add(Proposal(row, candidate.Donor.Translation, $"相似度 {candidate.Score:P0} · 旧原文：{candidate.Donor.Original}"));
        }
        return result;
    }

    public static BulkTranslationProposal Proposal(TranslationReadEntry row, string translation, string source)
    {
        var diagnostics = new TranslationValidator().Validate(row.Original ?? "", translation);
        var reason = row.Translation == translation ? "内容未变化" : diagnostics.Count > 0 ? string.Join("；", diagnostics.Select(d => d.Message)) : null;
        return new(row, translation, source, reason);
    }

    public static double Similarity(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length < 2 || b.Length < 2) return 0;
        // 使用字符二元组，避免长句之间的平方级编辑距离计算。
        var left = Enumerable.Range(0, a.Length - 1).Select(i => a.Substring(i, 2)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var right = Enumerable.Range(0, b.Length - 1).Select(i => b.Substring(i, 2)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return 2d * left.Count(right.Contains) / (left.Count + right.Count);
    }
}
