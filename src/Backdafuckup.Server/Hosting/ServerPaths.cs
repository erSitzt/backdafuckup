namespace Backdafuckup.Server.Hosting;

/// <summary>
/// Well-known directories of a backdafuckup server installation.
/// Linux: /etc/backdafuckup (config), /var/lib/backdafuckup (data), /var/log/backdafuckup (logs).
/// Windows: %ProgramData%\backdafuckup for all three.
/// Development: ./.data below the content root.
/// Override with the configuration keys Paths:ConfigDirectory / Paths:DataDirectory / Paths:LogDirectory
/// (environment variables BDFU_Paths__DataDirectory etc.).
/// </summary>
public sealed record ServerPaths(string ConfigDirectory, string DataDirectory, string LogDirectory)
{
    public string KeyRingDirectory => Path.Combine(DataDirectory, "keys");

    public string CertificatePath => Path.Combine(DataDirectory, "server-certificate.pfx");

    public string SetupTokenPath => Path.Combine(DataDirectory, "setup-token");

    public static ServerPaths Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        string configDir, dataDir, logDir;

        if (environment.IsDevelopment())
        {
            var root = Path.Combine(Directory.GetCurrentDirectory(), ".data");
            configDir = root;
            dataDir = root;
            logDir = Path.Combine(root, "logs");
        }
        else if (OperatingSystem.IsWindows())
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "backdafuckup");
            configDir = root;
            dataDir = root;
            logDir = Path.Combine(root, "logs");
        }
        else
        {
            configDir = "/etc/backdafuckup";
            dataDir = "/var/lib/backdafuckup";
            logDir = "/var/log/backdafuckup";
        }

        var paths = new ServerPaths(
            configuration["Paths:ConfigDirectory"] ?? configDir,
            configuration["Paths:DataDirectory"] ?? dataDir,
            configuration["Paths:LogDirectory"] ?? logDir);

        Directory.CreateDirectory(paths.DataDirectory);
        Directory.CreateDirectory(paths.LogDirectory);
        return paths;
    }
}

public static class ServerConfigurationExtensions
{
    /// <summary>Adds &lt;config dir&gt;/appsettings.json and BDFU_* environment variables on top of the defaults.</summary>
    public static IConfigurationBuilder AddServerConfigFiles(this IConfigurationBuilder builder, ServerPaths paths)
    {
        builder.AddJsonFile(Path.Combine(paths.ConfigDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
        builder.AddEnvironmentVariables("BDFU_");
        return builder;
    }
}
