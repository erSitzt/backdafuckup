using System.Xml.Linq;

namespace Backdafuckup.Testing.Vim;

/// <summary>
/// Checks a vim25 request against the element order of the public XSD (<see cref="VimSchemaOrder"/>): vCenter refuses
/// elements that are unknown or out of sequence ("Unexpected element tag … seen while parsing serialized DataObject").
/// Methods and types the table does not know are not checked.
/// </summary>
public static class VimSchemaCheck
{
    public static void Check(XElement request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (VimSchemaOrder.Methods.TryGetValue(request.Name.LocalName, out var type))
        {
            CheckElement(request, type, $"call information for method {request.Name.LocalName}");
        }
    }

    private static void CheckElement(XElement element, string declaredType, string where)
    {
        var type = element.Attribute(FakeVcenter.Xsi + "type")?.Value is { } typed ? typed[(typed.IndexOf(':', StringComparison.Ordinal) + 1)..] : declaredType;
        if (!VimSchemaOrder.Types.ContainsKey(type))
        {
            return;
        }

        var fields = Fields(type);
        var position = 0;
        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;
            var index = fields.FindIndex(position, f => f.Name == name);
            if (index < 0)
            {
                throw new VimFault("InvalidRequest", $"Unexpected element tag \"{name}\" seen while parsing serialized DataObject of type {type} ({where}).");
            }

            position = index;
            CheckElement(child, fields[index].Type, $"{type}.{name}");
        }
    }

    private static List<(string Name, string Type)> Fields(string type)
    {
        var (baseType, elements) = VimSchemaOrder.Types[type];
        var fields = baseType is not null && VimSchemaOrder.Types.ContainsKey(baseType) ? Fields(baseType) : [];
        fields.AddRange(elements);
        return fields;
    }
}
