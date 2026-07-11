namespace RPGModder.Core.Services;

public static class FileTreeService
{
    public static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        var sourceInfo = new DirectoryInfo(sourceDirectory);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Refusing to traverse a reparse point: {sourceDirectory}");
        }

        Directory.CreateDirectory(destinationDirectory);

        foreach (string file in Directory.GetFiles(sourceDirectory))
        {
            var fileInfo = new FileInfo(file);
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Refusing to copy a reparse point: {file}");
            }

            File.Copy(file, Path.Combine(destinationDirectory, fileInfo.Name), true);
        }

        foreach (string directory in Directory.GetDirectories(sourceDirectory))
        {
            string name = Path.GetFileName(directory);
            CopyDirectory(directory, Path.Combine(destinationDirectory, name));
        }
    }

    public static void ReplaceDirectory(string sourceDirectory, string destinationDirectory)
    {
        if (Directory.Exists(destinationDirectory))
        {
            Directory.Delete(destinationDirectory, true);
        }

        if (Directory.Exists(sourceDirectory))
        {
            CopyDirectory(sourceDirectory, destinationDirectory);
        }
    }

    public static void WriteAllTextAtomic(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path, true);
    }
}

