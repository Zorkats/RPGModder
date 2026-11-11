using RPGModder.Core.Application;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Workspace;
using RPGModder.UI.Dialogs;
using System.Linq;
using System.Threading.Tasks;

namespace RPGModder.UI;

/// <summary>
/// The vanilla baseline choices (GR-V6, TX-20, TX-21, TX-23, TX-24): after a game opens or a deploy is
/// refused, the player chooses to rebuild the backup, keep an unverified one, or decide later. A rebuild
/// that needs an answer asks it here and runs again with the answer.
/// </summary>
public partial class MainWindow
{
    private const string VerifyStep =
        "In Steam: right-click the game, Properties, Installed Files, \"Verify integrity of game files\".";

    /// <summary>After the game opens: finishes a rebuild that waits for an answer, or offers the choice for a backup that blocks deploys.</summary>
    private async Task ReviewBaselineAfterOpenAsync()
    {
        if (_gameSession == null || _engine == null)
        {
            return;
        }

        BaselineOutcome? opened = _engine.OpenBaseline;
        if (opened != null && opened.Question != BaselineQuestion.None)
        {
            await RunBaselineRebuildAsync(opened, BaselineAnswers.None);
            return;
        }

        if (!GameSessionService.CheckBaseline(_gameSession).Success)
        {
            await OfferBaselineChoiceAsync();
        }
    }

    /// <summary>Whether a deploy was stopped by the baseline check, in which case the choice is offered.</summary>
    private async Task<bool> HandleBaselineRefusalAsync(OperationResult result)
    {
        bool refused = result.Diagnostics.Any(item =>
            item.Code == DiagnosticCodes.WorkspaceBaselineUnverified && item.Severity == DiagnosticSeverity.Error);
        if (refused)
        {
            SetStatus($"Cannot deploy: {FirstErrorMessage(result)}", false);
            await OfferBaselineChoiceAsync();
        }

        return refused;
    }

    private async Task OfferBaselineChoiceAsync()
    {
        if (_gameSession == null)
        {
            return;
        }

        string message = GameSessionService.CheckBaseline(_gameSession).Diagnostics.FirstOrDefault()?.Message
                         ?? "The vanilla backup cannot be proven to be the original game.";
        VanillaManifest? manifest = TryReadManifest();
        if (manifest?.Audit is { HasMatches: true } audit)
        {
            message += $"\n\n{audit.SameContent + audit.SameContentAndTime} backed-up files equal files of your installed mods, " +
                       $"{audit.SameContentAndTime} of them also in time, so they were most likely copied from a mod.";
        }

        message += "\n\nRebuild (recommended) takes a new backup and proves it against Steam's file list when it can. " +
                   "Keep this backup lets deploys run again, each with a warning. Later keeps deploys stopped.";

        ConfirmChoice choice = await new ConfirmDialog(
            "Vanilla backup not proven",
            message,
            "Rebuild",
            "Keep this backup",
            "Later").AskAsync(this);

        if (choice == ConfirmChoice.Confirm)
        {
            await StartBaselineRebuildAsync();
        }
        else if (choice == ConfirmChoice.Alternate)
        {
            OperationResult kept = await _vanillaBackup.KeepAsync(_gameSession);
            SetStatus(
                kept.Success ? "Vanilla backup kept without proof. Every deploy carries a warning until a rebuild." : $"Could not keep the backup: {FirstErrorMessage(kept)}",
                kept.Success);
        }
    }

    /// <summary>The rebuild from its start: the player is told first how to get the original files back.</summary>
    private async Task StartBaselineRebuildAsync()
    {
        if (_gameSession == null)
        {
            return;
        }

        ConfirmChoice choice = await new ConfirmDialog(
            "Rebuild vanilla backup",
            "RPGModder removes the files its deploys added, then takes a new vanilla backup. With Steam's file list every " +
            "file is proven against it. Without it, the game folder is taken as it is, so restore the original files first:\n\n" +
            VerifyStep + "\n\nProfiles, mods and save backups are kept.",
            "Rebuild").AskAsync(this);
        if (choice == ConfirmChoice.Confirm)
        {
            await RunBaselineRebuildAsync(null, BaselineAnswers.None);
        }
    }

    /// <summary>Runs the rebuild, asking each question it returns, until it is built or the player stops.</summary>
    private async Task RunBaselineRebuildAsync(BaselineOutcome? pending, BaselineAnswers answers)
    {
        if (_gameSession == null)
        {
            return;
        }

        BaselineOutcome outcome = pending ?? await RebuildBaselineAsync(answers);
        while (true)
        {
            switch (outcome.Question)
            {
                case BaselineQuestion.None:
                    await RefreshPendingChangesAsync();
                    ReportBaselineOutcome(outcome);
                    return;

                case BaselineQuestion.ConfirmModMatches:
                {
                    ConfirmChoice choice = await new ConfirmDialog(
                        "Files equal to mod files",
                        $"{outcome.Audit?.SameContentAndTime ?? 0} files in the game folder equal files of your installed mods in content and time, " +
                        "so they were most likely copied from a mod. Continue only if Steam's verification has run since those mods were deployed:\n\n" +
                        VerifyStep,
                        "They are original").AskAsync(this);
                    if (choice != ConfirmChoice.Confirm)
                    {
                        SetStatus("Rebuild stopped. The vanilla backup is unchanged.", false);
                        return;
                    }

                    answers = answers with { ModMatchesConfirmed = true };
                    break;
                }

                case BaselineQuestion.ChooseBase:
                {
                    ConfirmChoice choice = await new ConfirmDialog(
                        "Game folder differs from Steam's files",
                        $"Your game folder differs from Steam's files: {outcome.Differences}.\n\n" +
                        "Use the store's files (recommended) builds the backup from Steam's files only, which also restores " +
                        "the encrypted game files. Keep my files makes your folder the base; every deploy then carries a warning.",
                        "Use the store's files",
                        "Keep my files").AskAsync(this);
                    if (choice == ConfirmChoice.Cancel)
                    {
                        SetStatus("Rebuild stopped. The vanilla backup is unchanged.", false);
                        return;
                    }

                    answers = answers with { Base = choice == ConfirmChoice.Confirm ? BaseChoice.UseStoreFiles : BaseChoice.KeepMyFiles };
                    break;
                }

                case BaselineQuestion.StoreFetch:
                {
                    ConfirmChoice choice = await new ConfirmDialog(
                        "Steam must fetch game files",
                        $"Steam is fetching {outcome.FetchCount} game files that exist nowhere in a proven copy. " +
                        "Ask Steam to verify the game, wait until it finishes, then check again. If Steam does not open:\n\n" + VerifyStep,
                        "Check again",
                        "Ask Steam to verify").AskAsync(this);
                    if (choice == ConfirmChoice.Cancel)
                    {
                        SetStatus("Rebuild waits for Steam. Deploys stay stopped until the backup is rebuilt.", false);
                        return;
                    }

                    if (choice == ConfirmChoice.Alternate)
                    {
                        bool opened = outcome.AppId != null && _vanillaBackup.RequestStoreFetch(_gameSession, outcome.AppId);
                        SetStatus(opened ? "Steam is verifying the game. Check again when it finishes." : "Steam could not be opened. " + VerifyStep, opened);
                        continue;
                    }

                    break;
                }
            }

            outcome = await RebuildBaselineAsync(answers);
        }
    }

    private async Task<BaselineOutcome> RebuildBaselineAsync(BaselineAnswers answers)
    {
        SetStatus("Rebuilding vanilla backup...", true);
        return await _vanillaBackup.RebuildAsync(_gameSession!, answers);
    }

    private void ReportBaselineOutcome(BaselineOutcome outcome)
    {
        if (!outcome.Result.Success)
        {
            SetStatus($"Vanilla backup not rebuilt: {FirstErrorMessage(outcome.Result)}", false);
            return;
        }

        OperationDiagnostic? warning = outcome.Result.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Warning);
        SetStatus(
            warning == null ? "Vanilla backup rebuilt." : $"Vanilla backup rebuilt. Warning: {warning.Message}",
            warning == null);
    }

    private VanillaManifest? TryReadManifest()
    {
        try
        {
            return new VanillaStore(_gameSession!.Workspace, _gameSession.Layout, _gameSession.Files).ReadManifest();
        }
        catch (System.IO.InvalidDataException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"The vanilla manifest is unreadable: {ex.Message}");
            return null;
        }
    }
}
