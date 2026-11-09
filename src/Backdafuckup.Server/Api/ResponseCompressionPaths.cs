namespace Backdafuckup.Server.Api;

/// <summary>The API paths whose responses are never compressed, because they can carry a secret (BREACH).</summary>
public static class ResponseCompressionPaths
{
    private static readonly PathString[] Secret = [new("/api/setup"), new("/api/auth"), new("/api/tokens")];

    public static bool CarriesSecrets(PathString path) => Secret.Any(s => path.StartsWithSegments(s, StringComparison.OrdinalIgnoreCase));
}
