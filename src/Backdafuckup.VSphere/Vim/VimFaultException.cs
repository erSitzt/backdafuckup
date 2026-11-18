using System.Xml.Linq;

namespace Backdafuckup.VSphere.Vim;

/// <summary>
/// A SOAP fault from vCenter: the fault type ("NotAuthenticated", "InvalidLogin", "FileNotFound", …), its message and the
/// method that caused it.
/// </summary>
public sealed class VimFaultException(string faultType, string message, string method, string endpoint, XElement? detail = null)
    : Exception($"vCenter {endpoint}: {method} failed: {message}" + (string.IsNullOrEmpty(faultType) ? string.Empty : $" ({faultType})"))
{
    public string FaultType { get; } = faultType;

    public string FaultMessage { get; } = message;

    public string Method { get; } = method;

    public string Endpoint { get; } = endpoint;

    public XElement? Detail { get; } = detail;
}

/// <summary>A vCenter task that ended in error: its description, the object it ran on and the fault.</summary>
public sealed class VimTaskException(string task, string? entity, string faultType, string message)
    : Exception($"{task}{(entity is null ? string.Empty : $" on {entity}")} failed: {message}" + (string.IsNullOrEmpty(faultType) ? string.Empty : $" ({faultType})"))
{
    public string Task { get; } = task;

    public string? Entity { get; } = entity;

    public string FaultType { get; } = faultType;

    public string FaultMessage { get; } = message;
}
