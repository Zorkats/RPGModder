using RPGModder.Core.Platform;

namespace RPGModder.Tests.Support;

/// <summary>
/// Runs every call on the real file system and throws <see cref="IOException"/> instead of the Nth
/// destructive call, counting from 1 (TST-9). With no failure set it only counts, so a test can
/// first measure how many calls a workflow makes and then fail each one in turn.
/// </summary>
public sealed class FaultInjectingFileOps : IFileSystemOps
{
    private readonly IFileSystemOps _inner = new FileSystemOps();
    private readonly int? _failAt;

    public FaultInjectingFileOps(int? failAt = null)
    {
        _failAt = failAt;
    }

    /// <summary>The destructive calls seen so far, the failed one included.</summary>
    public int Calls { get; private set; }

    /// <summary>The operation and path of the call that failed, or null while none has.</summary>
    public string? FailedCall { get; private set; }

    public void CopyFile(string source, string destination)
    {
        Step(nameof(CopyFile), destination);
        _inner.CopyFile(source, destination);
    }

    public void MoveFile(string source, string destination)
    {
        Step(nameof(MoveFile), destination);
        _inner.MoveFile(source, destination);
    }

    public void DeleteFile(string path)
    {
        Step(nameof(DeleteFile), path);
        _inner.DeleteFile(path);
    }

    public void CopyDirectory(string source, string destination)
    {
        Step(nameof(CopyDirectory), destination);
        _inner.CopyDirectory(source, destination);
    }

    public void MoveDirectory(string source, string destination)
    {
        Step(nameof(MoveDirectory), destination);
        _inner.MoveDirectory(source, destination);
    }

    public void DeleteDirectory(string path)
    {
        Step(nameof(DeleteDirectory), path);
        _inner.DeleteDirectory(path);
    }

    public void ReplaceDirectory(string source, string destination)
    {
        Step(nameof(ReplaceDirectory), destination);
        _inner.ReplaceDirectory(source, destination);
    }

    public void WriteAllTextAtomic(string path, string content)
    {
        Step(nameof(WriteAllTextAtomic), path);
        _inner.WriteAllTextAtomic(path, content);
    }

    private void Step(string operation, string path)
    {
        Calls++;
        if (Calls == _failAt)
        {
            FailedCall = operation + " " + path;
            throw new IOException($"Injected failure at call {Calls}: {FailedCall}");
        }
    }
}
