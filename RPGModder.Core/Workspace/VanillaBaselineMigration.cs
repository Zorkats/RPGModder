using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Platform;
using System.Diagnostics;
using System.Globalization;

namespace RPGModder.Core.Workspace;

/// <summary>
/// Workspace migration 3 to 4 (GR-V6): turns a 2.0.0 backup and its <c>backup_complete.marker</c>
/// into <c>vanilla.manifest.json</c> without ever declaring it clean on its own word. With the
/// store's list the backup is proven, and a backup that does not prove goes to the rebuild (TX-20).
/// Without it, extras are moved aside, the audit runs, and the manifest says <c>unverified</c>.
/// Each step can run again, so the migration is idempotent. The caller holds the operation gate.
/// </summary>
public sealed class VanillaBaselineMigration
{
    private const string ModData = "_moddata";

    private readonly GameWorkspacePaths _workspace;
    private readonly GameLayout _layout;
    private readonly IFileSystemOps _files;
    private readonly IStoreManifestSource _storeSource;
    private readonly TimeProvider _clock;
    private readonly VanillaStore _vanilla;

    public VanillaBaselineMigration(
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

    /// <summary>Runs the migration when the workspace is below schema 4; otherwise returns an empty outcome.</summary>
    public BaselineOutcome Run()
    {
        var outcome = new BaselineOutcome();
        if (WorkspaceMigrationService.ReadSchemaVersion(_workspace) >= GameWorkspacePaths.CurrentSchemaVersion)
        {
            return outcome;
        }

        if (_vanilla.IsComplete)
        {
            // An earlier run wrote the manifest and stopped before the marker went.
            _files.DeleteFile(_vanilla.MarkerPath);
        }
        else if (!File.Exists(_vanilla.MarkerPath))
        {
            // A backup without its marker is incomplete; the first-run path takes it again.
            _vanilla.Discard();
        }
        else
        {
            outcome = Migrate(ReadBackupDate());
        }

        new WorkspaceMigrationService(_files).WriteSchemaVersion(_workspace, GameWorkspacePaths.CurrentSchemaVersion, _clock.GetUtcNow());
        return outcome;
    }

    private BaselineOutcome Migrate(DateTimeOffset backupDate)
    {
        StoreManifestResult store = _storeSource.Read(_workspace.GameRoot, _layout.ContentRoot);
        return store.IsAvailable ? Prove(store, backupDate) : MigrateUnverified(store, backupDate);
    }

    // With the store's list: unlisted files go aside; a backup whose listed files all prove is confirmed.
    private BaselineOutcome Prove(StoreManifestResult store, DateTimeOffset backupDate)
    {
        var outcome = new BaselineOutcome { AppId = store.App?.AppId, StoreStateFlags = store.App?.StateFlags ?? 0 };
        string backup = _vanilla.Root;
        List<string> unlisted = _vanilla.Files.Where(file => store.Find(file) == null).ToList();
        foreach (string file in unlisted)
        {
            _vanilla.MoveAside(file, _workspace.LegacyBackupExtras);
        }

        ReportMoved(unlisted.Count, outcome.Result);

        List<StoreFileEntry> listed = store.Files
            .Where(file => ManagedContent.IsManaged(file.Path) && !ManagedContent.IsLegacyLibraryEntry(_layout.ContentRoot, file.Path))
            .ToList();
        bool proven = listed.All(file =>
        {
            string path = Path.Combine(backup, file.Path);
            return File.Exists(path) && VanillaBaselineBuilder.Proves(path, file);
        });

        if (!proven)
        {
            // GR-V6: the migration continues as the rebuild, which takes proven files from wherever they sit.
            BaselineOutcome rebuild = new VanillaBaselineBuilder(_workspace, _layout, _files, _storeSource, _clock).Build(BaselineAnswers.None);
            foreach (OperationDiagnostic diagnostic in outcome.Result.Diagnostics)
            {
                rebuild.Result.Add(diagnostic);
            }

            return rebuild;
        }

        var manifest = new VanillaManifest
        {
            CreatedUtc = backupDate,
            SteamAppId = store.App!.AppId,
            SteamBuildId = store.App.BuildId,
            SteamDepots = store.App.Depots.Select(depot => new VanillaDepot { DepotId = depot.DepotId, ManifestId = depot.ManifestId }).ToList(),
            BaselineSource = VanillaBaseline.StoreManifest,
            Baseline = VanillaBaseline.Confirmed,
            Files = _vanilla.Describe(listed.Select(file => file.Path))
        };
        _vanilla.WriteManifest(manifest);
        _files.DeleteFile(_vanilla.MarkerPath);
        outcome.Built = true;
        return outcome;
    }

    // Without the store's list: steps 1 to 4 of GR-V6.
    private BaselineOutcome MigrateUnverified(StoreManifestResult store, DateTimeOffset backupDate)
    {
        var outcome = new BaselineOutcome { AppId = store.App?.AppId };
        outcome.Result.AddInfo(
            DiagnosticCodes.WorkspaceStoreManifestUnavailable,
            $"The store's file list could not be read, so the migrated backup stays unverified: {store.Reason}");

        var extras = new List<string>();
        if (Directory.Exists(Path.Combine(_vanilla.Root, ModData)))
        {
            extras.Add(ModData);
        }

        extras.AddRange(ManagedContent.LegacyLibraryEntries(_vanilla.Root));
        foreach (string extra in extras)
        {
            _vanilla.MoveAside(extra, _workspace.LegacyBackupExtras);
        }

        ReportMoved(extras.Count, outcome.Result);

        IReadOnlyList<string> files = ManagedContent.List(_vanilla.Root);
        BaselineAudit audit = new BaselineAuditor(_workspace.Mods).Audit(_vanilla.Root, files, _clock.GetUtcNow());
        outcome.Audit = audit;
        VanillaBaselineBuilder.AddAuditWarning(audit, outcome.Result);

        var manifest = new VanillaManifest
        {
            CreatedUtc = backupDate,
            SteamAppId = null,
            SteamBuildId = null,
            BaselineSource = VanillaBaseline.Workflow,
            Baseline = VanillaBaseline.Unverified,
            Audit = audit,
            Files = _vanilla.Describe(files)
        };
        _vanilla.WriteManifest(manifest);
        _files.DeleteFile(_vanilla.MarkerPath);
        outcome.Built = true;
        return outcome;
    }

    private static void ReportMoved(int count, OperationResult result)
    {
        if (count > 0)
        {
            result.AddInfo(
                DiagnosticCodes.WorkspaceBackupExtrasMoved,
                $"{count} entries that are not game content were moved out of the vanilla backup to .rpgmodder/legacy/backup-extras.");
        }
    }

    // The marker's BackupDate is when 2.0.0 took the backup; the marker's own time stands in when it cannot be read.
    private DateTimeOffset ReadBackupDate()
    {
        try
        {
            JToken? date = JObject.Parse(File.ReadAllText(_vanilla.MarkerPath))["BackupDate"];
            if (date != null && date.Type == JTokenType.Date)
            {
                object value = ((JValue)date).Value!;
                return value is DateTimeOffset offset ? offset.ToUniversalTime() : new DateTimeOffset(((DateTime)value).ToUniversalTime());
            }

            if (date != null && DateTimeOffset.TryParse(date.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset parsed))
            {
                return parsed.ToUniversalTime();
            }
        }
        catch (JsonException ex)
        {
            Trace.TraceWarning($"backup_complete.marker is not valid JSON; its file time stands in for BackupDate: {ex.Message}");
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(_vanilla.MarkerPath), TimeSpan.Zero);
    }
}
