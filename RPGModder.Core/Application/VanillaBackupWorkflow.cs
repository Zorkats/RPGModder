using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Workspace;

namespace RPGModder.Core.Application;

/// <summary>
/// The game-update workflow (TX-20), which is also "Rebuild" for an unverified backup (GR-V6), and
/// "Keep this backup". Both run under the operation gate with the game stopped.
/// </summary>
public sealed class VanillaBackupWorkflow
{
    private const string RebuildOperation = "Rebuild vanilla backup";
    private const string KeepOperation = "Keep vanilla backup";

    /// <summary>
    /// Rebuilds the vanilla backup with the answers given so far. When the build needs another
    /// answer it changes nothing and returns the question; run it again with the answer.
    /// </summary>
    public async Task<BaselineOutcome> RebuildAsync(GameSession session, BaselineAnswers answers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkflowStart.TryBegin(session, RebuildOperation, true, out IDisposable? lease, out OperationResult refusal))
        {
            return Refused(refusal);
        }

        using (lease)
        {
            return await Task.Run(() => Rebuild(session, answers), CancellationToken.None);
        }
    }

    /// <summary>The rebuild with no answers, for callers that cannot ask; a needed answer is reported as a failure.</summary>
    public async Task<OperationResult> ResetAsync(GameSession session, CancellationToken cancellationToken = default)
    {
        BaselineOutcome outcome = await RebuildAsync(session, BaselineAnswers.None, cancellationToken);
        if (outcome.Question != BaselineQuestion.None)
        {
            outcome.Result.AddError(
                DiagnosticCodes.WorkspaceBackupResetFailed,
                "The vanilla backup was not rebuilt because the rebuild needs your answer.",
                outcome.Question.ToString());
        }

        return outcome.Result;
    }

    /// <summary>"Keep this backup" for an unverified backup: deploys run again, each with a warning (GR-V6).</summary>
    public async Task<OperationResult> KeepAsync(GameSession session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkflowStart.TryBegin(session, KeepOperation, false, out IDisposable? lease, out OperationResult refusal))
        {
            return refusal;
        }

        using (lease)
        {
            return await Task.Run(() => Keep(session), CancellationToken.None);
        }
    }

    /// <summary>Asks the player's own store client to verify the game and fetch missing files (TX-23).</summary>
    public bool RequestStoreFetch(GameSession session, string appId)
    {
        return session.Store.RequestFetch(appId);
    }

    private static BaselineOutcome Rebuild(GameSession session, BaselineAnswers answers)
    {
        var builder = new VanillaBaselineBuilder(session.Workspace, session.Layout, session.Files, session.Store, session.Clock);
        try
        {
            return builder.Build(answers);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var failed = new BaselineOutcome();
            bool trusted = new VanillaStore(session.Workspace, session.Layout, session.Files).IsComplete;
            failed.Result.AddError(
                DiagnosticCodes.WorkspaceBackupResetFailed,
                trusted
                    ? $"The vanilla backup could not be rebuilt and is unchanged: {ex.Message}"
                    : $"The vanilla backup could not be rebuilt and is no longer trusted; it is taken again when the game is opened: {ex.Message}",
                session.Workspace.CleanBackup);
            return failed;
        }
    }

    private static OperationResult Keep(GameSession session)
    {
        var result = new OperationResult();
        try
        {
            VanillaManifest manifest = new VanillaStore(session.Workspace, session.Layout, session.Files).Accept(session.Clock.GetUtcNow());
            result.AddWarning(
                DiagnosticCodes.WorkspaceBaselineUnverified,
                "The vanilla backup was kept without proof. Every deploy carries this warning until a rebuild.",
                manifest.Audit == null ? null : $"audit: {manifest.Audit.SameContent} same content, {manifest.Audit.SameContentAndTime} same content and time");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            result.AddError(DiagnosticCodes.WorkspaceBaselineUnverified, $"The choice could not be recorded: {ex.Message}", session.Workspace.VanillaManifest);
        }

        return result;
    }

    private static BaselineOutcome Refused(OperationResult refusal)
    {
        var outcome = new BaselineOutcome();
        foreach (OperationDiagnostic diagnostic in refusal.Diagnostics)
        {
            outcome.Result.Add(diagnostic);
        }

        return outcome;
    }
}
