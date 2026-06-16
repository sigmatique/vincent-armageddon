using Robust.Shared.Serialization;
using Robust.Shared.Map;

namespace Content.Shared.Pointing;

// TODO just make pointing properly predicted?
// So true
/// <summary>
///     Event raised when someone runs the client-side pointing verb.
/// </summary>
[Serializable, NetSerializable]
public sealed class PointingAttemptEvent : EntityEventArgs
{
    public NetEntity Target;

    public PointingAttemptEvent(NetEntity target)
    {
        Target = target;
    }
}

/// <summary>
/// [Changed by MisfitsCrew/Operator] Allows remote-vision systems to provide the visual
/// point source and rotation behavior used by the server-side pointing action.
/// </summary>
[ByRefEvent]
public record struct GetPointingSourceEvent(EntityUid Pointer, EntityCoordinates Coordinates, EntityUid Pointed)
{
    public readonly EntityUid Pointer = Pointer;
    public readonly EntityCoordinates Coordinates = Coordinates;
    public readonly EntityUid Pointed = Pointed;

    public EntityUid Source = Pointer;
    public bool RotateSource = true;
    public bool Handled;
    public bool Cancelled;
}

/// <summary>
/// Raised on the entity who is pointing after they point at something.
/// </summary>
/// <param name="Pointed"></param>
[ByRefEvent]
public readonly record struct AfterPointedAtEvent(EntityUid Pointed);

/// <summary>
/// Raised on an entity after they are pointed at by another entity.
/// </summary>
/// <param name="Pointer"></param>
[ByRefEvent]
public readonly record struct AfterGotPointedAtEvent(EntityUid Pointer);
