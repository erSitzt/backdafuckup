namespace Backdafuckup.FileLevel;

/// <summary>
/// A path inside a guest volume as name components. Paths from the API use "/" as separator on every file system;
/// "." and ".." never appear, so a path cannot leave the volume or the folder it names.
/// </summary>
public sealed class GuestPath : IEquatable<GuestPath>
{
    private readonly string[] _components;

    private GuestPath(string[] components)
    {
        _components = components;
    }

    public static GuestPath Root { get; } = new([]);

    public IReadOnlyList<string> Components => _components;

    public bool IsRoot => _components.Length == 0;

    public string Name => _components.Length == 0 ? string.Empty : _components[^1];

    public GuestPath? Parent => _components.Length == 0 ? null : new GuestPath(_components[..^1]);

    /// <summary>Parses "a/b/c" (leading, trailing and doubled slashes are ignored). Rejects "." and "..".</summary>
    public static GuestPath Parse(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Root;
        }

        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var component in components)
        {
            Validate(component);
        }

        return new GuestPath(components);
    }

    public GuestPath Append(string name)
    {
        Validate(name);
        return new GuestPath([.. _components, name]);
    }

    /// <summary>True when <paramref name="other"/> is this path or below it.</summary>
    public bool Contains(GuestPath other) =>
        other._components.Length >= _components.Length && _components.AsSpan().SequenceEqual(other._components.AsSpan(0, _components.Length));

    public bool Equals(GuestPath? other) => other is not null && _components.AsSpan().SequenceEqual(other._components);

    public override bool Equals(object? obj) => obj is GuestPath other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var component in _components)
        {
            hash.Add(component, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => "/" + string.Join('/', _components);

    private static void Validate(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
        {
            throw new ArgumentException($"Invalid name in a guest path: '{name}'.", nameof(name));
        }
    }
}
