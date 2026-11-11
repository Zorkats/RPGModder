using RPGModder.Core.Models;

namespace RPGModder.Core.Operations;

/// <summary>
/// Mutual exclusion for one open workspace. One gate exists per session, and every workflow that
/// mutates the game folder or the workspace takes it before it starts. A second caller is refused at
/// once and never waits. While an operation runs, the gate also holds an exclusive lock on
/// <c>.rpgmodder/operation.lock</c>, so a second RPGModder process on the same game is refused too.
/// </summary>
public sealed class OperationGate
{
    private readonly object _sync = new();
    private readonly string _lockFilePath;
    private string? _currentOperation;

    public OperationGate(string lockFilePath)
    {
        _lockFilePath = Path.GetFullPath(lockFilePath);
    }

    public bool IsBusy
    {
        get
        {
            lock (_sync)
            {
                return _currentOperation != null;
            }
        }
    }

    public string? CurrentOperation
    {
        get
        {
            lock (_sync)
            {
                return _currentOperation;
            }
        }
    }

    /// <summary>Raised after the gate is entered or released, on the thread that did it.</summary>
    public event EventHandler? Changed;

    public bool TryEnter(string operationName, out IDisposable? lease)
    {
        return TryEnter(operationName, out lease, out _);
    }

    public bool TryEnter(string operationName, out IDisposable? lease, out OperationDiagnostic? refusal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        lease = null;

        lock (_sync)
        {
            if (_currentOperation != null)
            {
                refusal = new OperationDiagnostic(
                    DiagnosticCodes.OperationBusy,
                    $"Another operation is running: {_currentOperation}.",
                    DiagnosticSeverity.Error,
                    _currentOperation);
                return false;
            }

            FileStream? lockStream = TryAcquireLockFile();
            if (lockStream == null)
            {
                refusal = new OperationDiagnostic(
                    DiagnosticCodes.OperationBusy,
                    "Another RPGModder process is working on this game.",
                    DiagnosticSeverity.Error,
                    _lockFilePath);
                return false;
            }

            _currentOperation = operationName;
            lease = new Lease(this, lockStream);
        }

        refusal = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Creates a refusal result for a workflow whose <see cref="TryEnter(string, out IDisposable?, out OperationDiagnostic?)"/> failed.
    /// </summary>
    public static OperationResult Refused(OperationDiagnostic refusal)
    {
        var result = new OperationResult();
        result.Add(refusal);
        return result;
    }

    private FileStream? TryAcquireLockFile()
    {
        string? directory = Path.GetDirectoryName(_lockFilePath);
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            // FileShare.None is an exclusive lock on Windows and an advisory flock on Unix. The lock
            // dies with the process, so a file left behind by a crash holds no lock: acquiring it here
            // is what clears it. The file is never deleted, because deleting it while another process
            // opens it would let two processes lock two different files at one path.
            return new FileStream(
                _lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void Release(FileStream lockStream)
    {
        lock (_sync)
        {
            lockStream.Dispose();
            _currentOperation = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Lease : IDisposable
    {
        private readonly OperationGate _gate;
        private FileStream? _lockStream;

        public Lease(OperationGate gate, FileStream lockStream)
        {
            _gate = gate;
            _lockStream = lockStream;
        }

        public void Dispose()
        {
            FileStream? lockStream = Interlocked.Exchange(ref _lockStream, null);
            if (lockStream != null)
            {
                _gate.Release(lockStream);
            }
        }
    }
}
