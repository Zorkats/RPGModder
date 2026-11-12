namespace RPGModder.Core.Operations;

/// <summary>
/// Every diagnostic code RPGModder emits. A code is <c>area.reason</c> and is stable once released,
/// so a constant is added here and never renamed. No code literal appears anywhere else.
/// </summary>
public static class DiagnosticCodes
{
    public const string ManifestMissing = "manifest.missing";
    public const string ManifestInvalid = "manifest.invalid";
    public const string ManifestSchemaUnsupported = "manifest.schema_unsupported";

    public const string HistoryWriteFailed = "history.write_failed";

    public const string DeploymentFailed = "deployment.failed";
    public const string DeployUnresolvedCollisions = "deploy.unresolved_collisions";

    public const string OperationBusy = "operation.busy";
    public const string OperationCancelled = "operation.cancelled";

    public const string GameRunning = "game.running";

    public const string WorkspaceExternalChange = "workspace.external_change";
    public const string WorkspaceGameUpdated = "workspace.game_updated";
    public const string WorkspaceRuntimeChange = "workspace.runtime_change";
    public const string WorkspaceForeignFile = "workspace.foreign_file";
    public const string WorkspaceBaselineUnverified = "workspace.baseline_unverified";
    public const string WorkspaceStoreManifestUnavailable = "workspace.store_manifest_unavailable";
    public const string WorkspaceStoreFetchNeeded = "workspace.store_fetch_needed";
    public const string WorkspaceBackupMatchesMod = "workspace.backup_matches_mod";
    public const string WorkspaceBackupExtrasMoved = "workspace.backup_extras_moved";
    public const string WorkspaceBackupResetFailed = "workspace.backup_reset_failed";
    public const string WorkspaceDeployedStateWriteFailed = "workspace.deployed_state_write_failed";

    public const string FhmmInstalledOutside = "fhmm.installed_outside";
    public const string FhmmRuntimePatch = "fhmm.runtime_patch";
    public const string FhmmToggleInGame = "fhmm.toggle_in_game";

    public const string MergeParseFailed = "merge.parse_failed";
    public const string MergeBaseParseFailed = "merge.base_parse_failed";
    public const string MergePatchTargetMissing = "merge.patch_target_missing";
    public const string MergePatchShapeMismatch = "merge.patch_shape_mismatch";
    public const string MergeRootShapeMismatch = "merge.root_shape_mismatch";
    public const string MergeConfigSaveUnsupported = "merge.config_save_unsupported";

    public const string RulesResolutionReused = "rules.resolution_reused";
    public const string RulesScopeReduced = "rules.scope_reduced";
    public const string RulesRemapApplied = "rules.remap_applied";
    public const string RulesRemapUnchecked = "rules.remap_unchecked";
    public const string RulesSwitchBandMoved = "rules.switch_band_moved";
    public const string RulesSwitchBandExhausted = "rules.switch_band_exhausted";

    public const string ProfileSchemaUnsupported = "profile.schema_unsupported";
    public const string ProfileWriteFailed = "profile.write_failed";
    public const string ProfileSwitchFailed = "profile.switch_failed";

    public const string InstallFailed = "install.failed";
    public const string InstallRemoveFailed = "install.remove_failed";
    public const string InstallTrashNotPurged = "install.trash_not_purged";

    public const string SaveBackupFailed = "save.backup_failed";
    public const string SaveRestoreFailed = "save.restore_failed";

    public const string UpdateChecksumFailed = "update.checksum_failed";

    public const string InternalUnexpected = "internal.unexpected";
}
