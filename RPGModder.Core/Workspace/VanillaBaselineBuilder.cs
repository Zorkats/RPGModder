using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Platform;
using System.Diagnostics;

namespace RPGModder.Core.Workspace;

/// <summary>
/// Builds the vanilla backup: the first-run workflow and the game-update rebuild (TX-20), which are
/// the only ways to change what vanilla is. With the store's list (TX-22) every file is proven and
/// taken from the live folder or the old backup (TX-23), and unexplained differences ask the player
/// (TX-24). Without it, the files RPGModder added are removed, the loader check and the audit run on
/// the live folder (TX-21), and the live folder is backed up. The caller holds the operation gate.
/// A build that needs an answer changes nothing and returns the question.
/// </summary>
public sealed class VanillaBaselineBuilder
{
    private readonly GameWorkspacePaths _workspace;
    private readonly GameLayout _layout;
    private readonly IFileSystemOps _files;
    private readonly IStoreManifestSource _storeSource;
    private readonly TimeProvider _clock;
    private readonly VanillaStore _vanilla;

    public VanillaBaselineBuilder(
        GameWorkspacePaths workspace,
        GameLayout layout,
        IFileSystemOps files,
        IStoreManifestSource storeSource,
        TimeProvider clock)
    {
        _workspace = workspace;
        _layout = layout;
        _files = files;
        _storeSource = storeSource;
        _clock = clock;
        _vanilla = new VanillaStore(workspace, layout, files);
    }

    public BaselineOutcome Build(BaselineAnswers answers)
    {
        var outcome = new BaselineOutcome();
        _vanilla.RecoverInterruptedSwap();
        string contentRoot = _layout.ContentRoot;
        StoreManifestResult store = _storeSource.Read(_workspace.GameRoot, contentRoot);
        outcome.AppId = store.App?.AppId;
        outcome.StoreStateFlags = store.App?.StateFlags ?? 0;

        IReadOnlySet<string> targets = ModTargets.Collect(_workspace.Mods, _layout.UsesWww);
        IReadOnlyList<string> live = ManagedContent.List(contentRoot);
        IReadOnlyList<string> added = AddedByRpgModder(live, targets);

        if (store.IsAvailable)
        {
            // A file the store lists is the game's, whoever wrote it last, so it is never removed.
            added = added.Where(file => store.Find(file) == null).ToList();
            BuildFromStore(store, live, added, targets, answers, outcome);
        }
        else
        {
            outcome.Result.AddInfo(
                DiagnosticCodes.WorkspaceStoreManifestUnavailable,
                $"The store's file list could not be read, so vanilla is checked by the weaker path: {store.Reason}");
            BuildFromLiveFolder(store, live, added, answers, outcome);
        }

        return outcome;
    }

    // TX-20 without a store manifest.
    private void BuildFromLiveFolder(
        StoreManifestResult store,
        IReadOnlyList<string> live,
        IReadOnlyList<string> added,
        BaselineAnswers answers,
        BaselineOutcome outcome)
    {
        if (_vanilla.HasRuntimeLoader())
        {
            outcome.Result.AddError(DiagnosticCodes.FhmmInstalledOutside, VanillaStore.DirtyInstallationMessage);
            return;
        }

        var addedSet = new HashSet<string>(added, ManagedContent.PathComparer);
        List<string> candidates = live.Where(file => !addedSet.Contains(file)).ToList();
        BaselineAudit audit = new BaselineAuditor(_workspace.Mods).Audit(_layout.ContentRoot, candidates, _clock.GetUtcNow());
        outcome.Audit = audit;
        AddAuditWarning(audit, outcome.Result);

        if (audit.SameContentAndTime > 0 && !answers.ModMatchesConfirmed)
        {
            outcome.Question = BaselineQuestion.ConfirmModMatches;
            return;
        }

        var manifest = NewManifest(store, VanillaBaseline.Confirmed, VanillaBaseline.Workflow);
        manifest.Audit = audit;
        Commit(added, candidates.Select(file => new BackupSource(file, LivePath(file), null)).ToList(), manifest);
        outcome.Built = true;
    }

    // TX-23 and TX-24 with a store manifest.
    private void BuildFromStore(
        StoreManifestResult store,
        IReadOnlyList<string> live,
        IReadOnlyList<string> added,
        IReadOnlySet<string> targets,
        BaselineAnswers answers,
        BaselineOutcome outcome)
    {
        string contentRoot = _layout.ContentRoot;
        List<StoreFileEntry> listed = store.Files
            .Where(file => ManagedContent.IsManaged(file.Path) && !ManagedContent.IsLegacyLibraryEntry(contentRoot, file.Path))
            .ToList();

        var differences = new BaselineCounts();
        var sources = new List<BackupSource>();
        int toFetch = 0;
        foreach (StoreFileEntry file in listed)
        {
            string livePath = LivePath(file.Path);
            bool liveExists = File.Exists(livePath);
            if (liveExists && Proves(livePath, file))
            {
                sources.Add(new BackupSource(file.Path, livePath, file.Sha1));
                continue;
            }

            if (!liveExists)
            {
                differences.Missing++;
            }
            else if (!targets.Contains(file.Path))
            {
                differences.Changed++;
            }

            string backupPath = Path.Combine(_vanilla.Root, file.Path);
            if (File.Exists(backupPath) && Proves(backupPath, file))
            {
                sources.Add(new BackupSource(file.Path, backupPath, file.Sha1));
            }
            else
            {
                toFetch++;
            }
        }

        var addedSet = new HashSet<string>(added, ManagedContent.PathComparer);
        differences.NotListed = live.Count(file => store.Find(file) == null && !targets.Contains(file) && !addedSet.Contains(file));
        outcome.Differences = differences;

        if (differences.Any && answers.Base == null)
        {
            outcome.Question = BaselineQuestion.ChooseBase;
            return;
        }

        if (answers.Base == BaseChoice.KeepMyFiles)
        {
            var kept = NewManifest(store, VanillaBaseline.PlayerModified, VanillaBaseline.Workflow);
            kept.BaseDifferences = differences;
            List<BackupSource> liveSources = live
                .Where(file => !addedSet.Contains(file))
                .Select(file => new BackupSource(file, LivePath(file), null))
                .ToList();
            Commit(added, liveSources, kept);
            outcome.Built = true;
            outcome.Result.AddWarning(
                DiagnosticCodes.WorkspaceBaselineUnverified,
                $"The vanilla backup is your own base and differs from the store's files: {differences}.");
            return;
        }

        if (toFetch > 0)
        {
            outcome.Question = BaselineQuestion.StoreFetch;
            outcome.FetchCount = toFetch;
            outcome.Result.AddInfo(
                DiagnosticCodes.WorkspaceStoreFetchNeeded,
                $"{toFetch} game files exist nowhere in a proven copy; Steam must fetch them.",
                toFetch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

        var manifest = NewManifest(store, VanillaBaseline.Confirmed, VanillaBaseline.StoreManifest);
        manifest.SteamDepots = store.App!.Depots
            .Select(depot => new VanillaDepot { DepotId = depot.DepotId, ManifestId = depot.ManifestId })
            .ToList();
        Commit(added, sources, manifest);
        outcome.Built = true;
    }

    // The deployed record no longer describes the folder once vanilla changes, so it goes first; then
    // the files of earlier deploys go, then the backup is replaced (TX-20, GR-V7).
    private void Commit(IReadOnlyList<string> added, IReadOnlyList<BackupSource> sources, VanillaManifest manifest)
    {
        new DeployedStateStore(_workspace, _files).Forget();
        foreach (string file in added)
        {
            _files.DeleteFile(LivePath(file));
        }

        _vanilla.Replace(sources, manifest);
    }

    // With the old manifest gone or unreadable nothing is known to be RPGModder's, so nothing is removed.
    private IReadOnlyList<string> AddedByRpgModder(IReadOnlyList<string> live, IReadOnlySet<string> targets)
    {
        VanillaManifest? previous;
        try
        {
            previous = _vanilla.ReadManifest();
        }
        catch (InvalidDataException ex)
        {
            Trace.TraceWarning($"The old vanilla manifest is unreadable, so no added file is removed: {ex.Message}");
            return Array.Empty<string>();
        }

        if (previous == null)
        {
            return Array.Empty<string>();
        }

        var known = new HashSet<string>(previous.Files.Select(file => file.Path), ManagedContent.PathComparer);
        return live.Where(file => !known.Contains(file) && targets.Contains(file)).ToList();
    }

    private VanillaManifest NewManifest(StoreManifestResult store, string baseline, string source)
    {
        return new VanillaManifest
        {
            CreatedUtc = _clock.GetUtcNow(),
            SteamAppId = store.App?.AppId,
            SteamBuildId = store.App?.BuildId,
            Baseline = baseline,
            BaselineSource = source
        };
    }

    private string LivePath(string relativePath)
    {
        return Path.Combine(_layout.ContentRoot, relativePath);
    }

    /// <summary>A file is the store's when its size and SHA-1 equal the entry; no shortcut (TX-22).</summary>
    internal static bool Proves(string path, StoreFileEntry entry)
    {
        return new FileInfo(path).Length == entry.Size && VanillaStore.Sha1Of(path) == entry.Sha1;
    }

    internal static void AddAuditWarning(BaselineAudit audit, OperationResult result)
    {
        if (audit.HasMatches)
        {
            result.AddWarning(
                DiagnosticCodes.WorkspaceBackupMatchesMod,
                $"{audit.SameContent + audit.SameContentAndTime} vanilla files equal files of installed mods " +
                $"({audit.SameContentAndTime} also in time, so they were copied from the mod).",
                audit.Describe());
        }
    }
}
