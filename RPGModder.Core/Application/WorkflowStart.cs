using RPGModder.Core.Models;
using RPGModder.Core.Operations;

namespace RPGModder.Core.Application;

/// <summary>
/// The first step of every mutating workflow: take the session's gate, and for the workflows that
/// touch the game folder or the live saves, refuse while the game is running.
/// </summary>
internal static class WorkflowStart
{
    public static bool TryBegin(
        GameSession session,
        string operationName,
        bool requiresGameStopped,
        out IDisposable? lease,
        out OperationResult refusal)
    {
        refusal = new OperationResult();
        if (!session.Gate.TryEnter(operationName, out lease, out OperationDiagnostic? busy))
        {
            refusal.Add(busy!);
            return false;
        }

        if (requiresGameStopped && session.GameProcess.IsRunning(session.ExecutablePath))
        {
            lease!.Dispose();
            lease = null;
            refusal.AddError(
                DiagnosticCodes.GameRunning,
                "The game is running.",
                Path.GetFileName(session.ExecutablePath));
            return false;
        }

        return true;
    }
}
