using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backdafuckup.Xen.Xapi;

/// <summary>Tolerant accessors for XAPI records (numbers may arrive as JSON numbers or strings).</summary>
public static class XapiRecord
{
    public const string NullRef = "OpaqueRef:NULL";

    public static string Str(this JsonObject record, string field) =>
        record[field] switch
        {
            null => string.Empty,
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            var node => node.ToJsonString(),
        };

    public static long Long(this JsonObject record, string field)
    {
        var node = record[field];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var l))
            {
                return l;
            }

            if (value.TryGetValue<double>(out var d))
            {
                return (long)d;
            }

            if (value.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    public static double Double(this JsonObject record, string field)
    {
        var node = record[field];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d))
            {
                return d;
            }

            if (value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    public static bool Bool(this JsonObject record, string field)
    {
        var node = record[field];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b))
            {
                return b;
            }

            if (value.TryGetValue<string>(out var s))
            {
                return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    public static IReadOnlyList<string> Set(this JsonObject record, string field) =>
        record[field] is JsonArray array ? array.Select(n => n?.ToString() ?? string.Empty).ToList() : [];

    public static IReadOnlyDictionary<string, string> Map(this JsonObject record, string field) =>
        record[field] is JsonObject map ? map.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty) : new Dictionary<string, string>();

    public static bool IsNullRef(string? reference) => string.IsNullOrEmpty(reference) || reference == NullRef;
}
