namespace RPGModder.Core.Services;

public static class SafePathService
{
    public static string ResolveContainedPath(string root, string relativePath, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A containment root is required.", nameof(root));
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException($"{fieldName} cannot be empty.");
        }

        if (relativePath.IndexOf('\0') >= 0 || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"{fieldName} must be a relative path.");
        }

        string fullRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        string rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootPrefix, PathComparison()))
        {
            throw new InvalidDataException($"{fieldName} escapes its allowed root: {relativePath}");
        }

        return candidate;
    }

    public static string ValidateDirectoryName(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{fieldName} cannot be empty.");
        }

        string trimmed = value.Trim();
        if (trimmed is "." or ".." ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmed.Contains(Path.DirectorySeparatorChar) ||
            trimmed.Contains(Path.AltDirectorySeparatorChar) ||
            !string.Equals(trimmed, Path.GetFileName(trimmed), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{fieldName} is not a valid directory name: {value}");
        }

        return trimmed;
    }

    private static StringComparison PathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}

