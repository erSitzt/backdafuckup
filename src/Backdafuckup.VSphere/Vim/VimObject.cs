using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Backdafuckup.VSphere.Vim;

/// <summary>
/// A data object or value from a vim25 response, read by element name (like <c>XapiRecord</c> for XAPI). Missing elements
/// read as null; the caller decides what is required.
/// </summary>
public sealed class VimObject(XElement element)
{
    public XElement Element { get; } = element ?? throw new ArgumentNullException(nameof(element));

    /// <summary>The data object's <c>xsi:type</c> without prefix ("VirtualDisk"), or null.</summary>
    public string? Type => Element.Attribute(VimXml.Xsi + "type")?.Value is { } type ? type[(type.IndexOf(':', StringComparison.Ordinal) + 1)..] : null;

    /// <summary>The text of the element itself (a simple value).</summary>
    public string Value => Element.Value;

    public VimObject? Child(string name) => Element.Element(VimXml.Ns + name) is { } child ? new VimObject(child) : null;

    public IEnumerable<VimObject> Children(string name) => Element.Elements(VimXml.Ns + name).Select(e => new VimObject(e));

    /// <summary>All child elements, for arrays (<c>ArrayOfVirtualDevice</c> and the like).</summary>
    public IEnumerable<VimObject> Items => Element.Elements().Select(e => new VimObject(e));

    public string? Str(string name) => Element.Element(VimXml.Ns + name)?.Value;

    public long? Long(string name) => Str(name) is { } text ? long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) : null;

    public int? Int(string name) => Str(name) is { } text ? int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) : null;

    public bool? Bool(string name) => Str(name) is { } text ? XmlConvert.ToBoolean(text) : null;

    public DateTimeOffset? Time(string name) => Str(name) is { } text ? XmlConvert.ToDateTimeOffset(text) : null;

    /// <summary>A managed object reference child (<c>&lt;name type="…"&gt;id&lt;/name&gt;</c>).</summary>
    public ManagedObjectReference? Ref(string name) => Element.Element(VimXml.Ns + name) is { } child ? AsRef(child) : null;

    public IEnumerable<ManagedObjectReference> Refs(string name) => Element.Elements(VimXml.Ns + name).Select(AsRef);

    /// <summary>This element as a managed object reference.</summary>
    public ManagedObjectReference AsRef() => AsRef(Element);

    /// <summary>
    /// A byte array: vim25 sends <c>xsd:byte[]</c> as one signed <c>&lt;byte&gt;</c> element per byte (e.g.
    /// <c>HostSystem.config.certificate</c>), not base64.
    /// </summary>
    public byte[] Bytes() =>
        [.. Element.Elements().Select(b => unchecked((byte)sbyte.Parse(b.Value, NumberStyles.Integer, CultureInfo.InvariantCulture)))];

    public override string ToString() => Element.ToString(SaveOptions.DisableFormatting);

    private static ManagedObjectReference AsRef(XElement element) =>
        new(element.Attribute("type")?.Value ?? throw new FormatException($"<{element.Name.LocalName}> is no managed object reference (no type)."), element.Value);
}
