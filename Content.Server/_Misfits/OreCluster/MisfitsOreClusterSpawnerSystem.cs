// #Misfits Add - Procedural ore cluster generation system for planet maps.
// Spawns a random-shaped blob of mineable rock entities at round start,
// respecting all existing occupied tiles (walls, trees, structures, etc.).

using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Random;

namespace Content.Server._Misfits.OreCluster;

/// <summary>
/// Handles <see cref="MisfitsOreClusterSpawnerComponent"/> on MapInit.
/// For each spawner entity found:
///   1. Picks a random radius between MinRadius and MaxRadius.
///   2. Iterates every tile in the bounding box.
///   3. Applies a per-tile organic shape roll (circular falloff + random fringe).
///   4. Applies a per-tile density roll (FillChance).
///   5. Skips any tile that already has a hard-physics entity anchored to it.
///   6. Spawns WallEntity at eligible tile centres, then deletes the spawner.
/// </summary>
public sealed class MisfitsOreClusterSpawnerSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MisfitsOreClusterSpawnerComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<MisfitsOreClusterSpawnerComponent> ent, ref MapInitEvent args)
    {
        var xform = Transform(ent);

        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return;

        var physicsQuery = GetEntityQuery<PhysicsComponent>();

        // Derive tile-space centre from the spawner's grid-local position.
        var localPos = xform.LocalPosition;
        var center = new Vector2i((int) MathF.Floor(localPos.X), (int) MathF.Floor(localPos.Y));

        var radius = _random.Next(ent.Comp.MinRadius, ent.Comp.MaxRadius + 1);

        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                // Organic blob shape: hard circle with a per-tile random fringe
                // that makes edges irregular and natural-looking.
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                var shapeThreshold = radius * (0.55f + 0.45f * _random.NextFloat());
                if (dist > shapeThreshold)
                    continue;

                // Internal density — not every in-shape tile gets a rock.
                if (!_random.Prob(ent.Comp.FillChance))
                    continue;

                var tile = center + new Vector2i(dx, dy);

                // Occupancy check: skip tiles that already have any hard-physics
                // entity anchored (walls, trees, furniture, structures, etc.).
                var enumerator = _mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
                var blocked = false;
                while (enumerator.MoveNext(out var anchored))
                {
                    if (physicsQuery.TryGetComponent(anchored, out var physics) &&
                        physics.CanCollide &&
                        physics.Hard)
                    {
                        blocked = true;
                        break;
                    }
                }

                if (blocked)
                    continue;

                // GridTileToLocal returns coordinates centred on the tile.
                Spawn(ent.Comp.WallEntity, _mapSystem.GridTileToLocal(gridUid, grid, tile));
            }
        }

        // Do NOT delete — the Persistent Entity Spawner system needs this entity
        // to remain alive so admins can click-erase it via the placement tool,
        // which triggers DB removal. The entity is invisible and non-collidable;
        // it is destroyed naturally when the round ends and the map is torn down.
        // The persistent system re-creates a fresh instance (fresh MapInitEvent)
        // on the next round start, generating a new layout.
    }
}
