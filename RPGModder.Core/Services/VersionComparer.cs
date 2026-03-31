using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace RPGModder.Core.Services;

public static class VersionComparer
{
    public static bool IsNewerVersion(string localVersion, string liveVersion)
    {
        if (string.IsNullOrWhiteSpace(localVersion) || string.IsNullOrWhiteSpace(liveVersion)) return false;
        if (localVersion.Equals(liveVersion, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            var localParts = ExtractVersionParts(localVersion);
            var liveParts = ExtractVersionParts(liveVersion);

            int maxLength = Math.Max(localParts.Length, liveParts.Length);

            for (int i = 0; i < maxLength; i++)
            {
                int local = i < localParts.Length ? localParts[i] : 0;
                int live = i < liveParts.Length ? liveParts[i] : 0;

                if (live > local) return true;
                if (local > live) return false;
            }

            return false; // They are exactly equal numerically
        }
        catch
        {
            // Fallback to strict string check if regex parsing utterly fails
            return !localVersion.Equals(liveVersion, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static int[] ExtractVersionParts(string versionStr)
    {
        // Strip out "v", "ver", "alpha" and grab just the numbers and dots
        var numericPart = Regex.Match(versionStr, @"\d+(\.\d+)*").Value;
        if (string.IsNullOrEmpty(numericPart)) return new[] { 0 };

        return numericPart.Split('.').Select(int.Parse).ToArray();
    }
}