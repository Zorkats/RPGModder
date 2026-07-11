using Newtonsoft.Json;

namespace RPGModder.Core.Services;

public sealed class DeploymentTransaction : IDisposable
{
    private const string JournalFileName = "transaction.json";
    private readonly string _contentRoot;
    private readonly string _transactionDirectory;
    private readonly string _snapshotDirectory;
    private readonly IReadOnlyList<string> _managedFolders;
    private readonly IReadOnlyList<string> _managedFiles;
    private bool _completed;

    private DeploymentTransaction(
        string contentRoot,
        string transactionDirectory,
        IReadOnlyList<string> managedFolders,
        IReadOnlyList<string> managedFiles)
    {
        _contentRoot = contentRoot;
        _transactionDirectory = transactionDirectory;
        _snapshotDirectory = Path.Combine(transactionDirectory, "snapshot");
        _managedFolders = managedFolders;
        _managedFiles = managedFiles;
    }

    public string Id => Path.GetFileName(_transactionDirectory);

    public static DeploymentTransaction Begin(
        string contentRoot,
        string transactionRoot,
        IReadOnlyList<string> managedFolders,
        IReadOnlyList<string> managedFiles)
    {
        Directory.CreateDirectory(transactionRoot);
        string directory = Path.Combine(transactionRoot, $"deployment-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var transaction = new DeploymentTransaction(
            Path.GetFullPath(contentRoot),
            directory,
            managedFolders,
            managedFiles);
        try
        {
            transaction.WriteJournal("preparing");
            transaction.CreateSnapshot();
            transaction.WriteJournal("applying");
            return transaction;
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    public static IReadOnlyList<string> RecoverInterrupted(
        string contentRoot,
        string transactionRoot,
        IReadOnlyList<string> managedFolders,
        IReadOnlyList<string> managedFiles)
    {
        var recovered = new List<string>();
        if (!Directory.Exists(transactionRoot))
        {
            return recovered;
        }

        foreach (string directory in Directory.GetDirectories(transactionRoot, "deployment-*"))
        {
            string journalPath = Path.Combine(directory, JournalFileName);
            if (!File.Exists(journalPath))
            {
                continue;
            }

            var journal = JsonConvert.DeserializeObject<TransactionJournal>(File.ReadAllText(journalPath));
            if (journal?.State is "committed" or "rolled-back")
            {
                TryDelete(directory);
                continue;
            }

            if (journal?.State == "preparing")
            {
                TryDelete(directory);
                continue;
            }

            var transaction = new DeploymentTransaction(
                Path.GetFullPath(contentRoot),
                directory,
                managedFolders,
                managedFiles);
            transaction.Rollback();
            recovered.Add(transaction.Id);
        }

        return recovered;
    }

    public void Commit()
    {
        WriteJournal("committed");
        _completed = true;
        TryDelete(_transactionDirectory);
    }

    public void Rollback()
    {
        RestoreSnapshot();
        WriteJournal("rolled-back");
        _completed = true;
        TryDelete(_transactionDirectory);
    }

    public void Dispose()
    {
        if (!_completed)
        {
            Rollback();
        }
    }

    private void CreateSnapshot()
    {
        Directory.CreateDirectory(_snapshotDirectory);

        foreach (string folder in _managedFolders)
        {
            string source = SafePathService.ResolveContainedPath(_contentRoot, folder, "Managed folder");
            string destination = SafePathService.ResolveContainedPath(_snapshotDirectory, folder, "Snapshot folder");
            if (Directory.Exists(source))
            {
                FileTreeService.CopyDirectory(source, destination);
            }
        }

        foreach (string file in _managedFiles)
        {
            string source = SafePathService.ResolveContainedPath(_contentRoot, file, "Managed file");
            string destination = SafePathService.ResolveContainedPath(_snapshotDirectory, file, "Snapshot file");
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, true);
            }
        }
    }

    private void RestoreSnapshot()
    {
        foreach (string folder in _managedFolders)
        {
            string live = SafePathService.ResolveContainedPath(_contentRoot, folder, "Managed folder");
            string snapshot = SafePathService.ResolveContainedPath(_snapshotDirectory, folder, "Snapshot folder");
            FileTreeService.ReplaceDirectory(snapshot, live);
        }

        foreach (string file in _managedFiles)
        {
            string live = SafePathService.ResolveContainedPath(_contentRoot, file, "Managed file");
            string snapshot = SafePathService.ResolveContainedPath(_snapshotDirectory, file, "Snapshot file");
            if (File.Exists(snapshot))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(live)!);
                File.Copy(snapshot, live, true);
            }
            else if (File.Exists(live))
            {
                File.Delete(live);
            }
        }
    }

    private void WriteJournal(string state)
    {
        var journal = new TransactionJournal
        {
            Id = Id,
            State = state,
            UpdatedUtc = DateTime.UtcNow
        };
        FileTreeService.WriteAllTextAtomic(
            Path.Combine(_transactionDirectory, JournalFileName),
            JsonConvert.SerializeObject(journal, Formatting.Indented));
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch
        {
        }
    }

    private sealed class TransactionJournal
    {
        public string Id { get; set; } = "";
        public string State { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
    }
}
