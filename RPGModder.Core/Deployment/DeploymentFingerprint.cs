using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RPGModder.Core.Models;
using RPGModder.Core.Services;

namespace RPGModder.Core.Deployment;

/// <summary>
/// Hashes the inputs of a deploy: the enabled mods in load order and the files of each mod by path,
/// size and last write time. Two profiles with the same hash deploy the same files, so the hash tells
/// the UI whether the selected profile is what the game folder holds.
/// </summary>
public static class DeploymentFingerprint
{
    public static string Compute(GameWorkspacePaths workspace, ModProfile profile)
    {
        var builder = new StringBuilder();
        foreach (string modFolderName in profile.EnabledMods)
        {
            builder.Append("mod\t").Append(modFolderName).Append('\n');
            AppendModFiles(builder, workspace.Mods, modFolderName);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendModFiles(StringBuilder builder, string modsRoot, string modFolderName)
    {
        string modPath;
        try
        {
            modPath = SafePathService.ResolveContainedPath(
                modsRoot,
                SafePathService.ValidateDirectoryName(modFolderName, "Enabled mod name"),
                "Enabled mod path");
        }
        catch (InvalidDataException)
        {
            builder.Append("invalid\n");
            return;
        }

        if (!Directory.Exists(modPath))
        {
            builder.Append("missing\n");
            return;
        }

        IEnumerable<string> lines = Directory
            .EnumerateFiles(modPath, "*", SearchOption.AllDirectories)
            .Select(file => DescribeFile(modPath, file))
            .OrderBy(line => line, StringComparer.Ordinal);
        foreach (string line in lines)
        {
            builder.Append(line).Append('\n');
        }
    }

    private static string DescribeFile(string modPath, string file)
    {
        var info = new FileInfo(file);
        string relative = Path.GetRelativePath(modPath, file).Replace('\\', '/');
        return string.Create(
            CultureInfo.InvariantCulture,
            $"file\t{relative}\t{info.Length}\t{info.LastWriteTimeUtc.Ticks}");
    }
}
