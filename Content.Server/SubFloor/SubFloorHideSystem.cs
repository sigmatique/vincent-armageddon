using Content.Shared.Construction.Components;
using Content.Shared.SubFloor;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;

namespace Content.Server.SubFloor;

public sealed class SubFloorHideSystem : SharedSubFloorHideSystem
{
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SubFloorHideComponent, AnchorAttemptEvent>(OnAnchorAttempt);
        SubscribeLocalEvent<SubFloorHideComponent, UnanchorAttemptEvent>(OnUnanchorAttempt);
    }

    private void OnAnchorAttempt(EntityUid uid, SubFloorHideComponent component, AnchorAttemptEvent args)
    {
        // No teleporting entities through floor tiles when anchoring them.
        var xform = Transform(uid);

        if (TryComp<MapGridComponent>(xform.GridUid, out var grid)
            && HasFloorCover(grid, _mapSystem.TileIndicesFor(xform.GridUid!.Value, grid, xform.Coordinates)))
        {
            args.Cancel();
        }
    }

    private void OnUnanchorAttempt(EntityUid uid, SubFloorHideComponent component, UnanchorAttemptEvent args)
    {
        // No un-anchoring things under the floor. Only required for something like vents, which are still interactable
        // despite being partially under the floor.
        if (component.IsUnderCover)
            args.Cancel();
    }
}
