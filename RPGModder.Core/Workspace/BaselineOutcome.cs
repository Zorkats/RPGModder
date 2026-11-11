using RPGModder.Core.Models;

namespace RPGModder.Core.Workspace;

/// <summary>
/// The result of a vanilla build or migration: its diagnostics, whether a backup was written, and
/// what the player must answer when the build stopped to ask (<see cref="BaselineQuestion"/>).
/// </summary>
public sealed class BaselineOutcome
{
    public OperationResult Result { get; } = new();

    /// <summary>Whether a new manifest was written.</summary>
    public bool Built { get; set; }

    public BaselineQuestion Question { get; set; } = BaselineQuestion.None;

    /// <summary>The audit of the files about to be backed up, when one ran (TX-21).</summary>
    public BaselineAudit? Audit { get; set; }

    /// <summary>The unexplained differences from the store's files, for <see cref="BaselineQuestion.ChooseBase"/> (TX-24).</summary>
    public BaselineCounts? Differences { get; set; }

    /// <summary>How many files the store client must fetch, for <see cref="BaselineQuestion.StoreFetch"/>.</summary>
    public int FetchCount { get; set; }

    /// <summary>The store's app id, when the game is a store install.</summary>
    public string? AppId { get; set; }

    /// <summary>The store's install state when the list was read; a fetch is finished only at <c>StoreApp.FullyInstalled</c>.</summary>
    public int StoreStateFlags { get; set; }
}
