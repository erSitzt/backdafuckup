// backdafuckup: hand-written equivalent of the registration that upstream generates at build time.
namespace DiscUtils.Xfs;

/// <summary>Registers only the formats and providers implemented by this assembly.</summary>
public static class Formats
{
    /// <summary>Explicitly registers formats once, without reflection.</summary>
    public static void Register() =>
        DiscUtils.Setup.SetupHelper.RegisterAssembly(typeof(Formats).Assembly, () => FileSystemManager.RegisterFileSystems(new FileSystemFactory()));
}
