namespace RPGModder.Core.Platform;

/// <summary>
/// The destructive file primitives (OWN-12). Every write, move and delete in <c>Workspace</c>,
/// <c>Deployment</c> and <c>Updates</c> goes through this seam, so a test can fail the Nth call
/// (TST-9). Reads do not go through it.
/// </summary>
public interface IFileSystemOps
{
    /// <summary>Copies a file over the destination, creating the parent folder. A link at the destination is replaced, not followed.</summary>
    void CopyFile(string source, string destination);

    /// <summary>Moves a file over the destination, creating the parent folder.</summary>
    void MoveFile(string source, string destination);

    /// <summary>Deletes a file. A missing file is not an error.</summary>
    void DeleteFile(string path);

    /// <summary>Copies a folder tree. Refuses links, so a copy never leaves the source tree.</summary>
    void CopyDirectory(string source, string destination);

    /// <summary>Moves a folder. The destination must not exist.</summary>
    void MoveDirectory(string source, string destination);

    /// <summary>Deletes a folder and everything in it. A missing folder is not an error.</summary>
    void DeleteDirectory(string path);

    /// <summary>Deletes the destination folder, then copies the source tree into its place.</summary>
    void ReplaceDirectory(string source, string destination);

    /// <summary>Writes text to a temporary file next to the target, then moves it over the target.</summary>
    void WriteAllTextAtomic(string path, string content);
}
