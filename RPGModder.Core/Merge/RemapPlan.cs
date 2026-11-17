using RPGModder.Core.Models;

namespace RPGModder.Core.Merge;

/// <summary>
/// What <see cref="IdRemapPlanner.Plan"/> decided. <c>Remaps</c> is every entry to apply this plan: the recorded ones
/// still in force and the new ones. <c>NewEntries</c> is the part lane A appends to the profile when the plan is
/// deployed (GR-R12). <c>Offers</c> are moves the planner did not make because the old id appears in a plugin file
/// the mod ships (GR-R11); lane A passes them to <see cref="Composer.Compose"/> and appends one when the player
/// chooses <see cref="ResolutionKind.Remap"/>. <c>Diagnostics</c> holds <c>rules.remap_target_taken</c>, one per
/// recorded entry whose target vanilla now uses, and <c>rules.remap_stale_reference</c>, one per entry in force whose
/// mod now names the old id where a move cannot follow it; <see cref="Composer.Compose"/> carries them into the plan.
/// A switch or variable two mods share is never moved, only offered: its offers move a whole range together, so lane
/// A appends every entry of <see cref="OffersFor"/> when the player chooses <see cref="ResolutionKind.Remap"/>.
/// </summary>
public sealed record RemapPlan(
    IReadOnlyList<IdRemap> Remaps,
    IReadOnlyList<IdRemap> NewEntries,
    IReadOnlyList<IdRemap> Offers,
    IReadOnlyList<OperationDiagnostic> Diagnostics)
{
    /// <summary>The offer that settles <paramref name="collision"/> by moving <paramref name="modId"/>, if there is one.</summary>
    public IdRemap? OfferFor(Collision collision, string modId)
    {
        ArgumentNullException.ThrowIfNull(collision);
        ArgumentNullException.ThrowIfNull(modId);

        return Offers.FirstOrDefault(offer => offer.ModId == modId && RemapPlaces.Settles(offer, collision));
    }

    /// <summary>
    /// Every entry to record when the player settles <paramref name="collision"/> by moving <paramref name="modId"/>:
    /// for a switch or variable, the whole group the settling offer belongs to (GR-R14); otherwise that offer alone.
    /// Empty when no offer settles it.
    /// </summary>
    public IReadOnlyList<IdRemap> OffersFor(Collision collision, string modId)
    {
        IdRemap? offer = OfferFor(collision, modId);
        if (offer is null)
        {
            return [];
        }

        return OfferGroups.FirstOrDefault(group => group.Contains(offer)) ?? [offer];
    }

    /// <summary>
    /// The switches and variables two or more enabled mods use and vanilla does not, one entry per id (GR-R15), and
    /// the id one mod uses for two meanings when the move of its new id is only offered (GR-R11).
    /// </summary>
    internal IReadOnlyList<SharedStateId> SharedStateIds { get; init; } = [];

    /// <summary>The offers that move together, one group per mod and range of switches or variables (GR-R14).</summary>
    internal IReadOnlyList<IReadOnlyList<IdRemap>> OfferGroups { get; init; } = [];
}

/// <summary>
/// A switch or variable id that the mods <paramref name="ModIds"/>, sorted, all use and vanilla does not; one mod alone
/// when it uses the id for two meanings.
/// </summary>
internal sealed record SharedStateId(IdKind Kind, int Id, IReadOnlyList<string> ModIds);
