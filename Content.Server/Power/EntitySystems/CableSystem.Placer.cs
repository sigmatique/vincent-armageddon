using Content.Server.Administration.Logs;
using Content.Server.Power.Components;
using Content.Shared.Database;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;

namespace Content.Server.Power.EntitySystems;

public sealed partial class CableSystem
{
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;

    private void InitializeCablePlacer()
    {
        SubscribeLocalEvent<CablePlacerComponent, AfterInteractEvent>(OnCablePlacerAfterInteract);
    }

    private void OnCablePlacerAfterInteract(Entity<CablePlacerComponent> placer, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach)
            return;

        var component = placer.Comp;
        if (component.CablePrototypeId == null)
            return;

        var gridUid = args.ClickLocation.GetGridUid(EntityManager);
        if(!TryComp<MapGridComponent>(gridUid, out var grid))
            return;

        var snapPos = _mapSystem.TileIndicesFor(gridUid!.Value, grid, args.ClickLocation);
        var tileDef = (ContentTileDefinition) _tileManager[_mapSystem.GetTileRef(gridUid.Value, grid, snapPos).Tile.TypeId];

        if (!tileDef.IsSubFloor || !tileDef.Sturdy)
            return;

        foreach (var anchored in _mapSystem.GetAnchoredEntities(gridUid.Value, grid, snapPos))
        {
            if (TryComp<CableComponent>(anchored, out var wire) && wire.CableType == component.BlockingCableType)
                return;
        }

        if (TryComp<StackComponent>(placer, out var stack) && !_stack.Use(placer, 1, stack))
            return;

        var newCable = EntityManager.SpawnEntity(component.CablePrototypeId, _mapSystem.GridTileToLocal(gridUid.Value, grid, snapPos));
        _adminLogger.Add(LogType.Construction, LogImpact.Low,
            $"{ToPrettyString(args.User):player} placed {ToPrettyString(newCable):cable} at {Transform(newCable).Coordinates}");
        args.Handled = true;
    }
}
