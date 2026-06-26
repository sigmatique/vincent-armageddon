// #Misfits Add - Restricts marked storage to opening only from hand or ground.

using Content.Shared._Misfits.Storage.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Storage;
using Robust.Shared.Containers;

namespace Content.Shared._Misfits.Storage;

public sealed class HandOrGroundStorageSystem : EntitySystem
{
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<HandOrGroundStorageComponent, StorageInteractAttemptEvent>(OnStorageInteractAttempt);
    }

    private void OnStorageInteractAttempt(Entity<HandOrGroundStorageComponent> ent, ref StorageInteractAttemptEvent args)
    {
        if (args.Cancelled)
            return;

        if (!_container.TryGetContainingContainer((ent.Owner, null, null), out _))
            return;

        if (args.User is { } user && _hands.IsHolding(user, ent.Owner, out _))
            return;

        args.Cancelled = true;
    }
}
