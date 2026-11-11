namespace RPGModder.Core.Platform;

/// <summary>The real file system behind <see cref="IFileSystemOps"/>.</summary>
public sealed class FileSystemOps : IFileSystemOps
{
    public void CopyFile(string source, string destination)
    {
        CreateParent(destination);
        var existing = new FileInfo(destination);
        if (existing.Exists && (existing.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Overwriting a link would write through it into the file it points at.
            existing.Delete();
        }

        File.Copy(source, destination, true);
    }

    public void MoveFile(string source, string destination)
    {
        CreateParent(destination);
        File.Move(source, destination, true);
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void CopyDirectory(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Refusing to traverse a reparse point: {source}");
        }

        Directory.CreateDirectory(destination);

        foreach (string file in Directory.GetFiles(source))
        {
            var fileInfo = new FileInfo(file);
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Refusing to copy a reparse point: {file}");
            }

            File.Copy(file, Path.Combine(destination, fileInfo.Name), true);
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    public void MoveDirectory(string source, string destination)
    {
        CreateParent(destination);
        Directory.Move(source, destination);
    }

    public void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    public void ReplaceDirectory(string source, string destination)
    {
        DeleteDirectory(destination);
        if (Directory.Exists(source))
        {
            CopyDirectory(source, destination);
        }
    }

    public void WriteAllTextAtomic(string path, string content)
    {
        CreateParent(path);
        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path, true);
    }

    private static void CreateParent(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }
}
