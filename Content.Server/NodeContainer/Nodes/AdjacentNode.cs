using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.GameObjects;

namespace Content.Server.NodeContainer.Nodes
{
    /// <summary>
    ///     A <see cref="Node"/> that can reach other <see cref="AdjacentNode"/>s that are directly adjacent to it.
    /// </summary>
    [DataDefinition]
    public sealed partial class AdjacentNode : Node
    {
        public override IEnumerable<Node> GetReachableNodes(TransformComponent xform,
            EntityQuery<NodeContainerComponent> nodeQuery,
            EntityQuery<TransformComponent> xformQuery,
            MapGridComponent? grid,
            IEntityManager entMan)
        {
            if (!xform.Anchored || grid == null)
                yield break;

            var gridIndex = entMan.System<SharedMapSystem>().TileIndicesFor(xform.GridUid!.Value, grid, xform.Coordinates);

            foreach (var (_, node) in NodeHelpers.GetCardinalNeighborNodes(nodeQuery, xform.GridUid.Value, grid, gridIndex))
            {
                if (node != this)
                    yield return node;
            }
        }
    }
}
