using System.Globalization;
using System.Xml.Linq;

namespace Backdafuckup.VSphere.Vim;

/// <summary>A reference to a managed object of vCenter: its type and its id ("VirtualMachine", "vm-123").</summary>
public sealed record ManagedObjectReference(string Type, string Value)
{
    public override string ToString() => $"{Type}:{Value}";
}

/// <summary>
/// Building blocks of vim25 SOAP requests. Requests are written by hand: every data object lists its elements in the order
/// of its XSD sequence (vCenter refuses another order), and derived types carry <c>xsi:type</c>.
/// </summary>
public static class VimXml
{
    public static readonly XNamespace Ns = "urn:vim25";

    public static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    /// <summary>An element in the vim25 namespace; null content is left out, so optional elements can be passed as null.</summary>
    public static XElement E(string name, params object?[] content) => new(Ns + name, Flatten(content));

    /// <summary>A data object of a derived type: <c>&lt;name xsi:type="type"&gt;</c>.</summary>
    public static XElement Typed(string name, string type, params object?[] content) =>
        new(Ns + name, new XAttribute(Xsi + "type", type), Flatten(content));

    /// <summary>The managed object a method is called on.</summary>
    public static XElement This(ManagedObjectReference target) => Ref("_this", target);

    /// <summary>A managed object reference as a parameter or property: <c>&lt;name type="VirtualMachine"&gt;vm-1&lt;/name&gt;</c>.</summary>
    public static XElement Ref(string name, ManagedObjectReference target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new XElement(Ns + name, new XAttribute("type", target.Type), target.Value);
    }

    /// <summary>An optional simple value: null leaves the element out.</summary>
    public static XElement? Opt(string name, object? value) => value is null ? null : E(name, Text(value));

    /// <summary>A simple value as vim25 writes it (lower-case booleans, invariant numbers).</summary>
    public static string Text(object value) => value switch
    {
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>A string value of an <c>anyType</c> field (e.g. <c>OptionValue.value</c>), which needs its XSD type.</summary>
    public static XElement AnyString(string name, string value) =>
        new(Ns + name, new XAttribute(Xsi + "type", "xsd:string"), new XAttribute(XNamespace.Xmlns + "xsd", Xsd.NamespaceName), value);

    private static IEnumerable<object> Flatten(object?[] content)
    {
        foreach (var item in content)
        {
            switch (item)
            {
                case null:
                    break;
                case string s:
                    yield return s;
                    break;
                case System.Collections.IEnumerable many and not XElement:
                    foreach (var inner in many)
                    {
                        if (inner is not null)
                        {
                            yield return inner;
                        }
                    }

                    break;
                default:
                    yield return item;
                    break;
            }
        }
    }
}
