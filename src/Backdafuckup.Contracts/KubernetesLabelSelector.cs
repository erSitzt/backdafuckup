using System.Text.RegularExpressions;

namespace Backdafuckup.Contracts;

/// <summary>
/// Label selectors in the syntax of <c>kubectl -l</c>, for jobs that back up the namespaces with certain labels: a job
/// object <c>labels:&lt;selector&gt;</c> stands for every namespace whose labels match at the time of a run (namespace
/// names never contain a colon). Requirements, separated by commas, all have to hold: <c>key</c>, <c>!key</c>,
/// <c>key=value</c> (or <c>==</c>), <c>key!=value</c>, <c>key in (a,b)</c>, <c>key notin (a,b)</c>.
/// </summary>
public static partial class KubernetesLabelSelector
{
    public const string Prefix = "labels:";

    public static bool IsJobObject(string nativeId) => nativeId?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static string JobObject(string selector) => Prefix + (selector ?? string.Empty).Trim();

    /// <summary>The selector of a job object made by <see cref="JobObject"/>.</summary>
    public static string Of(string nativeId) => IsJobObject(nativeId) ? nativeId[Prefix.Length..] : throw new ArgumentException($"'{nativeId}' is no label selector.", nameof(nativeId));

    /// <summary>The name a job object of a selector has in the console.</summary>
    public static string Describe(string selector) => $"Namespaces with the labels {selector}";

    /// <param name="Operator">exists, !exists, =, !=, in, notin</param>
    public sealed record Requirement(string Key, string Operator, IReadOnlyList<string> Values);

    /// <summary>The requirements of <paramref name="selector"/>, or why it is no label selector.</summary>
    public static (IReadOnlyList<Requirement>? Requirements, string? Error) Parse(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return (null, "Give a label selector, e.g. backup=daily.");
        }

        var requirements = new List<Requirement>();
        foreach (var part in SplitTopLevel(selector))
        {
            var text = part.Trim();
            Match match;
            if ((match = SetRequirement().Match(text)).Success)
            {
                var values = match.Groups["values"].Value.Split(',', StringSplitOptions.TrimEntries);
                if (values.Any(v => !ValuePattern().IsMatch(v)) || values.All(v => v.Length == 0))
                {
                    return (null, $"'{text}': the values in parentheses are not label values (letters, digits, -, _ and . , up to 63).");
                }

                requirements.Add(new Requirement(match.Groups["key"].Value, match.Groups["op"].Value, [.. values.Where(v => v.Length > 0)]));
            }
            else if ((match = EqualityRequirement().Match(text)).Success)
            {
                var op = match.Groups["op"].Value == "!=" ? "!=" : "=";
                requirements.Add(new Requirement(match.Groups["key"].Value, op, [match.Groups["value"].Value]));
            }
            else if ((match = ExistsRequirement().Match(text)).Success)
            {
                requirements.Add(new Requirement(match.Groups["key"].Value, match.Groups["not"].Success ? "!exists" : "exists", []));
            }
            else
            {
                return (null, $"'{text}' is no label requirement (key, !key, key=value, key!=value, key in (a,b), key notin (a,b)).");
            }
        }

        return (requirements, null);
    }

    /// <summary>Whether labels satisfy every requirement.</summary>
    public static bool Matches(IReadOnlyList<Requirement> requirements, IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(labels);
        return requirements.All(r =>
        {
            var has = labels.TryGetValue(r.Key, out var value);
            return r.Operator switch
            {
                "exists" => has,
                "!exists" => !has,
                "=" => has && value == r.Values[0],
                "!=" => !has || value != r.Values[0],
                "in" => has && r.Values.Contains(value!),
                "notin" => !has || !r.Values.Contains(value!),
                _ => false,
            };
        });
    }

    /// <summary>Labels as the inventory keeps them (<c>key=value,key2=value2</c>; neither keys nor values contain commas).</summary>
    public static IReadOnlyDictionary<string, string> ParseLabels(string? text) =>
        string.IsNullOrEmpty(text)
            ? new Dictionary<string, string>()
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .GroupBy(pair => pair[0], StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First()[1], StringComparer.Ordinal);

    /// <summary>Labels for the inventory: <c>key=value</c>, sorted, joined with commas.</summary>
    public static string FormatLabels(IEnumerable<KeyValuePair<string, string>>? labels) =>
        labels is null ? string.Empty : string.Join(',', labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}"));

    /// <summary>The requirements: split at commas outside parentheses.</summary>
    private static IEnumerable<string> SplitTopLevel(string selector)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < selector.Length; i++)
        {
            switch (selector[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return selector[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return selector[start..];
    }

    private const string Key = @"(?<key>(?:[a-z0-9](?:[-a-z0-9]*[a-z0-9])?(?:\.[a-z0-9](?:[-a-z0-9]*[a-z0-9])?)*/)?[A-Za-z0-9](?:[-A-Za-z0-9_.]{0,61}[A-Za-z0-9])?)";

    [GeneratedRegex(@"^" + Key + @"\s+(?<op>in|notin)\s*\((?<values>[^()]*)\)$")]
    private static partial Regex SetRequirement();

    [GeneratedRegex(@"^" + Key + @"\s*(?<op>==|=|!=)\s*(?<value>(?:[A-Za-z0-9](?:[-A-Za-z0-9_.]{0,61}[A-Za-z0-9])?)?)$")]
    private static partial Regex EqualityRequirement();

    [GeneratedRegex(@"^(?<not>!)?\s*" + Key + @"$")]
    private static partial Regex ExistsRequirement();

    [GeneratedRegex(@"^(?:[A-Za-z0-9](?:[-A-Za-z0-9_.]{0,61}[A-Za-z0-9])?)?$")]
    private static partial Regex ValuePattern();
}
