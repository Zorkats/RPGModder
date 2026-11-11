using System.Security.Cryptography;

namespace RPGModder.Core.Updates;

/// <summary>
/// Reads <c>SHA256SUMS</c> as <c>sha256sum</c> writes it (<c>&lt;hex&gt;  &lt;name&gt;</c>, an optional
/// <c>*</c> before binary names) and checks a downloaded package against it (TX-13).
/// </summary>
public static class ReleaseChecksums
{
    /// <summary>The upper-case SHA-256 listed for <paramref name="assetName"/>, or null. A name listed twice is refused.</summary>
    public static string? Find(string sums, string assetName)
    {
        string? found = null;
        foreach (string rawLine in sums.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space != 64)
            {
                continue;
            }

            string hash = line[..space];
            string name = line[(space + 1)..].TrimStart(' ').TrimStart('*');
            if (!hash.All(Uri.IsHexDigit) || !string.Equals(name, assetName, StringComparison.Ordinal))
            {
                continue;
            }

            if (found != null)
            {
                throw new UpdateChecksumException($"{ReleaseAssets.Checksums} lists {assetName} twice.");
            }

            found = hash.ToUpperInvariant();
        }

        return found;
    }

    /// <summary>Throws <see cref="UpdateChecksumException"/> unless the file's SHA-256 is the one listed for its asset.</summary>
    public static void Verify(string filePath, string sums, string assetName)
    {
        string expected = Find(sums, assetName)
                          ?? throw new UpdateChecksumException($"{ReleaseAssets.Checksums} has no hash for {assetName}, so the update was not applied.");
        string actual;
        using (FileStream stream = File.OpenRead(filePath))
        {
            actual = Convert.ToHexString(SHA256.HashData(stream));
        }

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new UpdateChecksumException($"The download of {assetName} does not match {ReleaseAssets.Checksums}, so the update was not applied.");
        }
    }
}
