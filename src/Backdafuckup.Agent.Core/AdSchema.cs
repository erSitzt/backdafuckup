using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Backdafuckup.Agent.Core;

/// <summary>What the schema says about an attribute ([MS-ADTS] 3.1.1.2.3, attributeSchema).</summary>
public sealed class AdAttributeInfo
{
    public string Name { get; set; } = string.Empty;

    /// <summary>attributeSyntax, e.g. <c>2.5.5.1</c> (DN) or <c>2.5.5.12</c> (Unicode string).</summary>
    public string Syntax { get; set; } = string.Empty;

    public bool SingleValued { get; set; }

    public bool SystemOnly { get; set; }

    public int SystemFlags { get; set; }

    /// <summary>0: not linked; even: forward link; odd: back link.</summary>
    public int LinkId { get; set; }

    public int SearchFlags { get; set; }

    public bool Constructed => (SystemFlags & 0x4) != 0;

    public bool BackLink => LinkId != 0 && LinkId % 2 == 1;

    public bool ForwardLink => LinkId != 0 && LinkId % 2 == 0;

    /// <summary>The value syntax for display: <c>dn</c>, <c>int</c>, <c>bool</c>, <c>time</c>, <c>sid</c>, <c>binary</c>, <c>string</c>.</summary>
    public string DisplaySyntax => Syntax switch
    {
        "2.5.5.1" or "2.5.5.7" or "2.5.5.14" => "dn",
        "2.5.5.8" => "bool",
        "2.5.5.9" or "2.5.5.16" => "int",
        "2.5.5.11" => "time",
        "2.5.5.17" => "sid",
        "2.5.5.10" or "2.5.5.15" => "binary",
        _ => "string",
    };
}

/// <summary>The live directory's schema, read once per mount from the schema naming context.</summary>
public sealed class AdSchema
{
    private static readonly string[] Wanted = { "lDAPDisplayName", "attributeSyntax", "isSingleValued", "systemOnly", "systemFlags", "linkID", "searchFlags" };

    private readonly Dictionary<string, AdAttributeInfo> _attributes;

    public AdSchema(IEnumerable<AdAttributeInfo> attributes) =>
        _attributes = attributes.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public int Count => _attributes.Count;

    /// <summary>The attribute, or null for a name the schema does not know (e.g. a range-limited name).</summary>
    public AdAttributeInfo? Find(string name) => _attributes.TryGetValue(name, out var info) ? info : null;

    public static AdSchema Load(IAdDirectory directory)
    {
        var schemaDn = directory.RootDse().Text("schemaNamingContext")
            ?? throw new AdDirectoryException($"The rootDSE of {directory.Endpoint} names no schema naming context.");
        var attributes = new List<AdAttributeInfo>();
        byte[]? cookie = null;
        do
        {
            var page = directory.Search(schemaDn, "(objectClass=attributeSchema)", AdScope.OneLevel, Wanted, 1000, cookie);
            foreach (var record in page.Records)
            {
                attributes.Add(new AdAttributeInfo
                {
                    Name = record.Text("lDAPDisplayName") ?? string.Empty,
                    Syntax = record.Text("attributeSyntax") ?? string.Empty,
                    SingleValued = record.Flag("isSingleValued"),
                    SystemOnly = record.Flag("systemOnly"),
                    SystemFlags = Int(record.Text("systemFlags")),
                    LinkId = Int(record.Text("linkID")),
                    SearchFlags = Int(record.Text("searchFlags")),
                });
            }

            cookie = page.Cookie;
        }
        while (cookie is { Length: > 0 });

        return new AdSchema(attributes.Where(a => a.Name.Length > 0));
    }

    private static int Int(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
