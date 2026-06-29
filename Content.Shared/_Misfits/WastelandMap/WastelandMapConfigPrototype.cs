// #Misfits Add - Wasteland map rendering configuration per game map.
// Maps a game map ID (e.g. "Wendover", "Vault") to its pre-rendered
// tactical image path and world-space bounds. Used by WastelandMapSystem
// to auto-detect map texture and bounds when WastelandMapComponent
// doesn't have them hardcoded (via MapConfigId field).
using Robust.Shared.Prototypes;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.Shared._Misfits.WastelandMap;

[Prototype("wastelandMapConfig")]
public sealed partial class WastelandMapConfigPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    /// <summary>
    /// Path to the rendered map texture, relative to Resources/Textures/.
    /// </summary>
    [DataField(required: true)]
    public ResPath MapTexturePath = default!;

    /// <summary>
    /// World-space tile bounds (left,bottom,right,top) that the rendered image covers.
    /// </summary>
    [DataField(required: true)]
    public Box2 WorldBounds;
}
