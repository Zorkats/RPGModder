namespace RPGModder.Core.Updates;

/// <summary>
/// The release asset names, a contract between the release workflow, <c>packaging/linux/package.sh</c>
/// and the updater (CI-11, TST-14). The pattern is <c>RPGModder-&lt;version&gt;-&lt;runtime&gt;.&lt;ext&gt;</c>,
/// plus <c>SHA256SUMS</c>. Exactly one name contains <c>win</c>, and the checksum file contains
/// neither <c>win</c> nor <c>linux</c>, so the 2.0.0 updater still picks a package (CI-17).
/// </summary>
public static class ReleaseAssets
{
    public const string Product = "RPGModder";
    public const string Checksums = "SHA256SUMS";
    public const string WindowsRuntime = "win-x64";
    public const string LinuxRuntime = "linux-x64";

    public static string WindowsZip(string version) => $"{Product}-{version}-{WindowsRuntime}.zip";

    public static string LinuxTarGz(string version) => $"{Product}-{version}-{LinuxRuntime}.tar.gz";

    public static string LinuxZip(string version) => $"{Product}-{version}-{LinuxRuntime}.zip";

    /// <summary>Every asset of one release, the checksum file last.</summary>
    public static IReadOnlyList<string> All(string version)
    {
        return new[] { WindowsZip(version), LinuxTarGz(version), LinuxZip(version), Checksums };
    }

    /// <summary>The packages the updater accepts on a platform, in order of preference.</summary>
    public static IReadOnlyList<string> PackagesFor(bool linux, string version)
    {
        return linux ? new[] { LinuxTarGz(version), LinuxZip(version) } : new[] { WindowsZip(version) };
    }
}
