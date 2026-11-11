using RPGModder.Core.Platform;

namespace RPGModder.Core.Services;

/// <summary>
/// Static access to the real file primitives for code outside the injected seam (settings, NXM
/// registration, the legacy engine). New code in Workspace, Deployment and Updates takes an
/// <see cref="IFileSystemOps"/> instead (OWN-12).
/// </summary>
public static class FileTreeService
{
    private static readonly FileSystemOps Files = new();

    public static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Files.CopyDirectory(sourceDirectory, destinationDirectory);
    }

    public static void ReplaceDirectory(string sourceDirectory, string destinationDirectory)
    {
        Files.ReplaceDirectory(sourceDirectory, destinationDirectory);
    }

    public static void WriteAllTextAtomic(string path, string content)
    {
        Files.WriteAllTextAtomic(path, content);
    }
}
