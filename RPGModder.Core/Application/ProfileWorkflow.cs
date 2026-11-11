using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Services;

namespace RPGModder.Core.Application;

/// <summary>
/// Creates, switches and deletes profiles under the session's gate, off the caller's thread.
/// </summary>
public sealed class ProfileWorkflow
{
    private const string CreateOperation = "Create profile";
    private const string SwitchOperation = "Switch profile";
    private const string DeleteOperation = "Delete profile";

    private readonly IProfileRepository _profiles;

    public ProfileWorkflow()
        : this(new ProfileRepository())
    {
    }

    public ProfileWorkflow(IProfileRepository profiles)
    {
        _profiles = profiles;
    }

    public async Task<OperationResult> CreateAsync(
        GameSession session,
        string profileName,
        ModProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkflowStart.TryBegin(session, CreateOperation, false, out IDisposable? lease, out OperationResult refusal))
        {
            return refusal;
        }

        using (lease)
        {
            return await Task.Run(() => WriteProfile(session, profileName, profile), CancellationToken.None);
        }
    }

    /// <summary>
    /// Swaps the live saves to the new profile's set and makes it the active profile. The game folder
    /// still holds the old profile's deploy, so the deployed state no longer matches the active profile
    /// and the UI shows its changes as pending.
    /// </summary>
    public async Task<OperationResult> SwitchAsync(
        GameSession session,
        string fromProfileName,
        string toProfileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkflowStart.TryBegin(session, SwitchOperation, true, out IDisposable? lease, out OperationResult refusal))
        {
            return refusal;
        }

        using (lease)
        {
            return await Task.Run(() => Switch(session, fromProfileName, toProfileName), CancellationToken.None);
        }
    }

    public async Task<OperationResult> DeleteAsync(
        GameSession session,
        string profileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkflowStart.TryBegin(session, DeleteOperation, false, out IDisposable? lease, out OperationResult refusal))
        {
            return refusal;
        }

        using (lease)
        {
            return await Task.Run(() => Delete(session, profileName), CancellationToken.None);
        }
    }

    private OperationResult WriteProfile(GameSession session, string profileName, ModProfile profile)
    {
        var result = new OperationResult();
        try
        {
            _profiles.Save(session.Workspace, profileName, profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result.AddError(DiagnosticCodes.ProfileWriteFailed, ex.Message, profileName);
        }

        return result;
    }

    private OperationResult Switch(GameSession session, string fromProfileName, string toProfileName)
    {
        var result = new OperationResult();
        try
        {
            session.Engine.SwapSaveFiles(fromProfileName, toProfileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result.AddError(DiagnosticCodes.ProfileSwitchFailed, ex.Message, toProfileName);
            return result;
        }

        try
        {
            _profiles.WriteActive(session.Workspace, toProfileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The live saves must belong to the profile the marker names, or the next switch would
            // store them under the wrong profile. So the saves go back to the old profile.
            result.AddError(DiagnosticCodes.ProfileSwitchFailed, ex.Message, toProfileName);
            SwapBack(session, fromProfileName, toProfileName, result);
        }

        return result;
    }

    private static void SwapBack(GameSession session, string fromProfileName, string toProfileName, OperationResult result)
    {
        try
        {
            session.Engine.SwapSaveFiles(toProfileName, fromProfileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result.AddError(
                DiagnosticCodes.ProfileSwitchFailed,
                $"The saves could not be switched back and belong to profile {toProfileName}: {ex.Message}",
                fromProfileName);
        }
    }

    private OperationResult Delete(GameSession session, string profileName)
    {
        var result = new OperationResult();
        try
        {
            _profiles.Delete(session.Workspace, profileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            result.AddError(DiagnosticCodes.ProfileWriteFailed, ex.Message, profileName);
        }

        return result;
    }
}
