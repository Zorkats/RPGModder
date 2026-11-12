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
}
