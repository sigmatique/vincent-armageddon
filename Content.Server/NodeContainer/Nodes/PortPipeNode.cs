using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.GameObjects;

namespace Content.Server.NodeContainer.Nodes
{
    [DataDefinition]
    public sealed partial class PortPipeNode : PipeNode
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

            foreach (var node in NodeHelpers.GetNodesInTile(nodeQuery, xform.GridUid.Value, grid, gridIndex))
            {
                if (node is PortablePipeNode)
                    yield return node;
            }

            foreach (var node in base.GetReachableNodes(xform, nodeQuery, xformQuery, grid, entMan))
            {
                yield return node;
            }
        }
    }
}
