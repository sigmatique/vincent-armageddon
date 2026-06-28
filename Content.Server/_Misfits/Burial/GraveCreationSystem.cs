using Content.Shared._Misfits.Burial;
using Content.Shared._Misfits.Burial.Components;
using Content.Shared._Misfits.SandDigging;
using Content.Shared.Buckle.Components;
using Content.Shared.Burial.Components;
using Content.Shared.Coordinates.Helpers;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Audio.Systems;

namespace Content.Server._Misfits.Burial;

/// <summary>
/// Allows shovel items bearing <see cref="GraveCreatorComponent"/> to dig a brand-new
/// grave on empty ground. The spawned grave is immediately opened (pre-dug) so a body
/// can be dragged/laid inside; closing and re-opening afterwards uses the normal
/// BurialSystem flow unchanged.
/// </summary>
public sealed class GraveCreationSystem : EntitySystem
{
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedEntityStorageSystem _storage = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GraveCreatorComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<GraveCreatorComponent, GraveCreationDoAfterEvent>(OnDoAfter);
    }

    private void OnAfterInteract(EntityUid uid, GraveCreatorComponent component, AfterInteractEvent args)
    {
        // Only dig a new grave when there is no entity target — i.e. clicking on empty ground.
        // Interactions with existing graves are handled by BurialSystem via InteractUsingEvent.
        if (args.Handled || args.Target != null || !args.CanReach || component.IsDigging)
            return;

        // Can't dig while buckled (riding, sitting in a chair, etc) — the doAfter's
        // BreakOnMove doesn't detect movement because local coords are relative to the parent.
        if (TryComp<BuckleComponent>(args.User, out var buckle) && buckle.Buckled)
        {
            _popup.PopupClient(Loc.GetString("grave-creation-buckled"), args.User, args.User);
            return;
        }

        var doAfterArgs = new DoAfterArgs(
            EntityManager,
            args.User,
            component.DigDelay,
            new GraveCreationDoAfterEvent(GetNetCoordinates(args.ClickLocation)),
            uid,
            used: uid)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
            BreakOnHandChange = true,
        };

        if (!_doAfter.TryStartDoAfter(doAfterArgs))
            return;

        component.Stream ??= _audio.PlayPredicted(component.DigSound, uid, args.User)?.Entity;
        component.IsDigging = true;

        var selfMsg = Loc.GetString("grave-creation-start-user");
        var othersMsg = Loc.GetString("grave-creation-start-others", ("user", args.User));
        _popup.PopupPredicted(selfMsg, othersMsg, args.User, args.User);

        args.Handled = true;
    }

    private void OnDoAfter(EntityUid uid, GraveCreatorComponent component, GraveCreationDoAfterEvent args)
    {
        component.IsDigging = false;
        component.Stream = _audio.Stop(component.Stream);

        if (args.Cancelled || args.Handled)
            return;

        // Snap the spawn point to the nearest tile centre for clean placement.
        // Convert NetCoordinates back to EntityCoordinates for spawn/engine APIs.
        var spawnCoords = GetCoordinates(args.SpawnCoordinates).SnapToGrid(EntityManager);
        var graveEnt = Spawn(component.GravePrototype, spawnCoords);

        // Pre-open the freshly-dug grave — the creation action IS the initial dig.
        // BurialSystem's StorageOpenAttemptEvent will pass because DiggingComplete = true,
        // then reset it to false via StorageAfterOpenEvent as normal.
        if (TryComp<GraveComponent>(graveEnt, out var graveComp))
        {
            graveComp.DiggingComplete = true;
            _storage.TryOpenStorage(args.User, graveEnt);
        }

        // Grave digging should always yield sand for shovels that support sand digging,
        // regardless of tile type, so users always receive material from digging work.
        if (TryComp<SandDiggerComponent>(uid, out var sandDigger))
            Spawn(sandDigger.SandPrototype, spawnCoords);

        _popup.PopupEntity(Loc.GetString("grave-creation-complete"), graveEnt, args.User);
    }
}
