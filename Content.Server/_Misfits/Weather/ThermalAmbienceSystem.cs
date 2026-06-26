// #Misfits Change /Add/
// Wires the DayNightCycleComponent to atmospheric temperature and sends private
// flavor-text messages to players based on how hot or cold their body is.
//
// Design intent:
//   • The outdoor air temperature tracks the day/night cycle realistically
//     (hot desert midday, cold desert night — Wendover, Utah salt flats feel).
//   • Temperature thresholds are deliberately kept inside safe ranges so the
//     existing TemperatureSystem never triggers damage (damage starts at ~360 K
//     heat / ~260 K cold; our peak is 313 K hot and floor is ~273 K cold).
//   • Flavor text is PURELY cosmetic — no stats, no damage, no debuffs.
//     It is delivered privately via SendPrivateDoMessage (the same mechanism as
//     hunger/thirst flavor text in NeedFlavorTextSystem).

using Content.Server.Atmos.EntitySystems;
using Content.Server.Chat.Systems;
using Content.Server.Temperature.Components;
using Content.Server.Weather;
using Content.Shared._NC14.DayNightCycle;
using Content.Shared.Atmos;
using Content.Shared.Ghost;
using Content.Shared.Light.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Misfits.Weather;

/// <summary>
/// Drives outdoor atmospheric temperature from the <see cref="DayNightCycleComponent"/>
/// and delivers private thermal flavor messages to player-controlled entities.
/// </summary>
public sealed partial class ThermalAmbienceSystem : EntitySystem
{
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly WeatherSystem _weather = default!;

    // ── Temperature tier for each map so we only call SetMapGasMixture on change ──
    private readonly Dictionary<EntityUid, TemperatureTier> _lastMapTier = new();

    // ── Per-player body-state anti-flap cooldowns (sweating / shivering) ──
    private readonly Dictionary<EntityUid, TimeSpan> _nextAmbientAt = new();

    // ── Per-player outdoor exposure cooldowns (prevents door-jitter spam) ──
    private readonly Dictionary<EntityUid, TimeSpan> _nextOutdoorFlavorAt = new();

    // ── Per-player outdoor dwell timers (must stay outside before severe flavor can fire) ──
    private readonly Dictionary<EntityUid, TimeSpan> _outdoorEligibleAt = new();

    // ── Per-player body-state tracking (sweating / shivering) ──
    private readonly Dictionary<EntityUid, BodyThermalState> _lastBodyState = new();

    // ── Per-player outdoor exposure tracking (only message on outdoor/tier changes) ──
    private readonly Dictionary<EntityUid, TemperatureTier> _lastOutdoorFlavorTier = new();
    private readonly HashSet<EntityUid> _outdoorExposed = new();

    /// <summary>
    /// Accumulator for throttling Update to once every ~10 seconds.
    /// Flavor text and map temp tier changes are not time-critical.
    /// </summary>
    // #Misfits Fix: Doubled from 5 s — temperature tiers change on a minute timescale; 10 s polling is fine.
    private const float UpdateInterval = 10f;
    private float _updateTimer;

    // ──────────────────────────────────────────────────────────────────────────
    // Temperature tier band edges (Kelvin)
    // These map normalized cycle-time windows to outdoor temperatures.
    //
    // Wendover, UT averages:
    //   Summer midday high: ~38–42 °C (311–315 K)
    //   Night low:          ~1–5 °C   (274–278 K)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Normalized cycle-time boundaries that define each temperature tier.</summary>
    /// Cycle time 0.0 = midnight, 0.5 = noon.
    private static readonly (float Start, float End, TemperatureTier Tier, float TempKelvin)[] TierWindows =
    {
        // Night  (midnight → pre-dawn)
        (0.00f, 0.22f, TemperatureTier.VeryCold,  274f),
        // Dawn   (pre-dawn → sunrise)
        (0.22f, 0.33f, TemperatureTier.Cold,       283f),
        // Morning (sunrise → mid-morning)
        (0.33f, 0.44f, TemperatureTier.Warm,       298f),
        // Midday  (mid-morning → early afternoon)
        (0.44f, 0.60f, TemperatureTier.VeryHot,    313f),
        // Afternoon (early afternoon → late afternoon)
        (0.60f, 0.72f, TemperatureTier.Hot,         306f),
        // Evening  (late afternoon → dusk)
        (0.72f, 0.85f, TemperatureTier.Cool,        290f),
        // Dusk → early night
        (0.85f, 1.00f, TemperatureTier.VeryCold,   274f),
    };

    // ── Body temperature thresholds ──
    // These drive flavor text only — never damage.
    private const float SweatBodyTempK  = 308f; // above this → sweating messages
    private const float ShiverBodyTempK = 288f; // below this → shivering messages

    // ── Flavor message arrays ──

    private static readonly string[] VeryHotMessages =
    {
        "thermal-ambience-very-hot-1",
        "thermal-ambience-very-hot-2",
        "thermal-ambience-very-hot-3",
        "thermal-ambience-very-hot-4",
        "thermal-ambience-very-hot-5",
    };

    private static readonly string[] HotMessages =
    {
        "thermal-ambience-hot-1",
        "thermal-ambience-hot-2",
        "thermal-ambience-hot-3",
        "thermal-ambience-hot-4",
    };

    private static readonly string[] WarmMessages =
    {
        "thermal-ambience-warm-1",
        "thermal-ambience-warm-2",
        "thermal-ambience-warm-3",
    };

    private static readonly string[] CoolMessages =
    {
        "thermal-ambience-cool-1",
        "thermal-ambience-cool-2",
        "thermal-ambience-cool-3",
        "thermal-ambience-cool-4",
    };

    private static readonly string[] ColdMessages =
    {
        "thermal-ambience-cold-1",
        "thermal-ambience-cold-2",
        "thermal-ambience-cold-3",
        "thermal-ambience-cold-4",
    };

    private static readonly string[] VeryColdMessages =
    {
        "thermal-ambience-very-cold-1",
        "thermal-ambience-very-cold-2",
        "thermal-ambience-very-cold-3",
        "thermal-ambience-very-cold-4",
    };

    private static readonly string[] SweatingMessages =
    {
        "thermal-ambience-sweating-1",
        "thermal-ambience-sweating-2",
        "thermal-ambience-sweating-3",
    };

    private static readonly string[] ShiveringMessages =
    {
        "thermal-ambience-shivering-1",
        "thermal-ambience-shivering-2",
        "thermal-ambience-shivering-3",
    };

    // ─────────────────────────────────────────────────────────────────────────

    public override void Initialize()
    {
        base.Initialize();

        // #Misfits Fix: System defunct — removed for 70+ player performance. Re-enable by
        // un-commenting the subscriptions here and the Update body below.
        // SubscribeLocalEvent<DayNightCycleComponent, ComponentRemove>(OnCycleRemoved);
        // SubscribeLocalEvent<TemperatureComponent, ComponentShutdown>(OnTemperatureShutdown);
    }

    private void OnCycleRemoved(EntityUid uid, DayNightCycleComponent _, ComponentRemove args)
    {
        _lastMapTier.Remove(uid);
    }

    private void OnTemperatureShutdown(EntityUid uid, TemperatureComponent _, ComponentShutdown args)
    {
        _nextAmbientAt.Remove(uid);
        _nextOutdoorFlavorAt.Remove(uid);
        _outdoorEligibleAt.Remove(uid);
        _lastBodyState.Remove(uid);
        _lastOutdoorFlavorTier.Remove(uid);
        _outdoorExposed.Remove(uid);
    }

    public override void Update(float frameTime)
    {
        // #Misfits Fix: Defunct at 70+ players — per-player atmospheric and flavor scans
        // added measurable tick cost. Temperature flavour text is purely cosmetic.
        // Restore by un-commenting below and the Initialize subscriptions above.
        //
        // base.Update(frameTime);
        // _updateTimer += frameTime;
        // if (_updateTimer < UpdateInterval)
        //     return;
        // _updateTimer -= UpdateInterval;
        // UpdateMapTemperatures();
        // UpdatePlayerFlavor();
        // UpdatePlayerOutdoorFlavor();
    }

    // ── Part A: Ambient outdoor temperature ───────────────────────────────────

    /// <summary>
    /// For every map with both <see cref="DayNightCycleComponent"/> and <see cref="MapComponent"/>,
    /// compute the current cycle tier and, if it has changed, update the map's gas mixture temperature.
    /// We query MapComponent (not MapAtmosphereComponent) to avoid the access restriction on that
    /// component's Mixture field — the mixture is built from standard constants instead.
    /// </summary>
    private void UpdateMapTemperatures()
    {
        var query = EntityQueryEnumerator<DayNightCycleComponent, MapComponent>();
        while (query.MoveNext(out var mapUid, out var dayNight, out _))
        {
            var cycleTime = GetNormalizedCycleTime(dayNight);
            var tier = GetTierForTime(cycleTime);

            // Only call SetMapAtmosphere when the tier actually changes —
            // that triggers a full grid tile refresh and is expensive.
            if (_lastMapTier.TryGetValue(mapUid, out var lastTier) && lastTier == tier)
                continue;

            _lastMapTier[mapUid] = tier;

            var targetTemp = GetTemperatureForTier(tier);
            ApplyMapTemperature(mapUid, targetTemp);
        }
    }

    /// <summary>
    /// Constructs a standard outdoor air mixture at <paramref name="targetKelvin"/> and applies it
    /// to the map via <see cref="AtmosphereSystem.SetMapAtmosphere"/>.
    /// The mole counts match Wendover.yml exactly because OxygenMolesStandard / NitrogenMolesStandard
    /// are calculated from the same CellVolume (2500 L) used by the map.
    /// We build from constants rather than cloning MapAtmosphereComponent.Mixture to avoid the
    /// [Access(typeof(SharedAtmosphereSystem))] restriction on that component.
    /// </summary>
    private void ApplyMapTemperature(EntityUid mapUid, float targetKelvin)
    {
        var mixture = new GasMixture(Atmospherics.CellVolume) { Temperature = targetKelvin };
        mixture.AdjustMoles(Gas.Oxygen,   Atmospherics.OxygenMolesStandard);
        mixture.AdjustMoles(Gas.Nitrogen, Atmospherics.NitrogenMolesStandard);

        // space: false — breathable non-space outdoor atmosphere.
        _atmosphere.SetMapAtmosphere(mapUid, false, mixture);
    }

    // ── Part B: Player body-temperature flavor text ───────────────────────────

    /// <summary>
    /// For every player-controlled entity with a <see cref="TemperatureComponent"/>,
    /// send private body-state flavor messages (sweating / shivering).
    /// </summary>
    private void UpdatePlayerFlavor()
    {
        var query = EntityQueryEnumerator<ActorComponent, TemperatureComponent>();
        while (query.MoveNext(out var uid, out var actor, out var tempComp))
        {
            // Skip if the player has since detached or the mob is dead.
            if (actor.PlayerSession.AttachedEntity != uid)
                continue;
            if (TryComp<MobStateComponent>(uid, out var mobState) && _mobState.IsDead(uid, mobState))
                continue;
            // Skip ghosts and aghosts — they have no physical body to feel temperature. #Misfits Fix
            if (HasComp<GhostComponent>(uid))
                continue;

            ProcessPlayerThermalFlavor(uid, actor.PlayerSession, tempComp);
        }
    }

    private void ProcessPlayerThermalFlavor(EntityUid uid, ICommonSession session, TemperatureComponent tempComp)
    {
        var bodyTemp = tempComp.CurrentTemperature;

        // Determine the current body thermal state.
        BodyThermalState currentState;
        if (bodyTemp >= SweatBodyTempK)
            currentState = BodyThermalState.Sweating;
        else if (bodyTemp <= ShiverBodyTempK)
            currentState = BodyThermalState.Shivering;
        else
            currentState = BodyThermalState.Comfortable;

        if (_lastBodyState.TryGetValue(uid, out var previousState) && previousState == currentState)
            return;

        _lastBodyState[uid] = currentState;

        // Only message on threshold crossings, not while the state persists.
        if (currentState == BodyThermalState.Comfortable)
            return;

        if (!CanSendFlavor(uid))
            return;

        SendBodyFlavorMessage(uid, session, currentState, immediate: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Computes the normalised 0–1 position within the day/night cycle.</summary>
    private float GetNormalizedCycleTime(DayNightCycleComponent dayNight)
    {
        var cycleDurationSeconds  = dayNight.CycleDurationMinutes * 60f;
        var offsetSeconds         = dayNight.StartOffset * cycleDurationSeconds;
        var rawSeconds            = (float) _timing.CurTime.TotalSeconds + offsetSeconds;
        return (rawSeconds % cycleDurationSeconds) / cycleDurationSeconds;
    }

    private static TemperatureTier GetTierForTime(float cycleTime)
    {
        foreach (var (start, end, tier, _) in TierWindows)
        {
            if (cycleTime >= start && cycleTime < end)
                return tier;
        }

        // Fallback — should never be reached given the windows cover 0–1.
        return TemperatureTier.VeryCold;
    }

    private static float GetTemperatureForTier(TemperatureTier tier)
    {
        foreach (var (_, _, t, temp) in TierWindows)
        {
            if (t == tier)
                return temp;
        }

        return Atmospherics.T20C; // safe default
    }
    // ── Part C: Outdoor environment flavor text ───────────────────────────────

    /// <summary>
    /// For every player on a map that has a tracked temperature tier,
    /// send a sparse private ambient message only after sustained exposure to severe outdoor heat or cold.
    /// </summary>
    private void UpdatePlayerOutdoorFlavor()
    {
        // Include TransformComponent in the query to avoid RA0030 (non-generic TryComp warning).
        var query = EntityQueryEnumerator<ActorComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var actor, out var xform))
        {
            if (actor.PlayerSession.AttachedEntity != uid)
                continue;
            if (TryComp<MobStateComponent>(uid, out var mobState) && _mobState.IsDead(uid, mobState))
                continue;
            // Skip ghosts and aghosts — they have no physical body to feel outdoor temperature. #Misfits Fix
            if (HasComp<GhostComponent>(uid))
                continue;

            // Look up which temperature tier the map this player is on is currently in.
            if (xform.MapUid is not { } mapUid)
            {
                ClearOutdoorExposure(uid);
                continue;
            }
            if (!_lastMapTier.TryGetValue(mapUid, out var tier))
            {
                ClearOutdoorExposure(uid);
                continue;
            }
            if (xform.GridUid is not { } gridUid)
            {
                ClearOutdoorExposure(uid);
                continue;
            }
            if (!TryComp<MapGridComponent>(gridUid, out var grid) ||
                !_mapSystem.TryGetTileRef(gridUid, grid, xform.Coordinates, out var tileRef) ||
                !IsWeatherExposed(gridUid, grid, tileRef))
            {
                ClearOutdoorExposure(uid);
                continue;
            }

            var wasOutdoors = _outdoorExposed.Contains(uid);
            _outdoorExposed.Add(uid);

            if (!wasOutdoors)
            {
                _outdoorEligibleAt[uid] = _timing.CurTime + TimeSpan.FromMinutes(2);
                continue;
            }

            if (_outdoorEligibleAt.TryGetValue(uid, out var eligibleAt) && _timing.CurTime < eligibleAt)
                continue;

            var tierChanged = !_lastOutdoorFlavorTier.TryGetValue(uid, out var previousTier) || previousTier != tier;
            _lastOutdoorFlavorTier[uid] = tier;

            if (!tierChanged || !ShouldSendOutdoorAmbient(tier))
                continue;

            var messages = GetOutdoorAmbientMessages(tier);
            if (messages.Length == 0)
                continue;

            if (!CanSendOutdoorFlavor(uid))
                continue;

            _nextOutdoorFlavorAt[uid] = _timing.CurTime + GetOutdoorAmbientCooldown(tier);
            _chat.SendPrivateDoMessage(actor.PlayerSession, Loc.GetString(_random.Pick(messages)));
        }
    }

    private bool IsWeatherExposed(EntityUid gridUid, MapGridComponent grid, TileRef tileRef)
    {
        TryComp<RoofComponent>(gridUid, out var roofComp);
        return _weather.CanWeatherAffect(gridUid, grid, tileRef, roofComp);
    }

    private void ClearOutdoorExposure(EntityUid uid)
    {
        _outdoorExposed.Remove(uid);
        _outdoorEligibleAt.Remove(uid);
    }

    private bool CanSendOutdoorFlavor(EntityUid uid)
    {
        return !_nextOutdoorFlavorAt.TryGetValue(uid, out var next) || _timing.CurTime >= next;
    }

    /// <summary>
    /// Returns the flavor message key array for a given temperature tier.
    /// Mild outdoor tiers stay silent to keep ambient narration rare.
    /// </summary>
    private static string[] GetOutdoorAmbientMessages(TemperatureTier tier)
    {
        return tier switch
        {
            TemperatureTier.VeryHot  => VeryHotMessages,
            TemperatureTier.VeryCold => VeryColdMessages,
            _                        => Array.Empty<string>(),
        };
    }

    private static bool ShouldSendOutdoorAmbient(TemperatureTier tier)
    {
        return tier is TemperatureTier.VeryHot or TemperatureTier.VeryCold;
    }

    private static TimeSpan GetOutdoorAmbientCooldown(TemperatureTier tier)
    {
        return tier switch
        {
            TemperatureTier.VeryHot  => TimeSpan.FromMinutes(10),
            TemperatureTier.VeryCold => TimeSpan.FromMinutes(10),
            _                        => TimeSpan.FromMinutes(10),
        };
    }

    // ── Shared helpers ────────────────────────────────────────────────────────
    private bool CanSendFlavor(EntityUid uid)
    {
        return !_nextAmbientAt.TryGetValue(uid, out var next) || _timing.CurTime >= next;
    }

    private void SendBodyFlavorMessage(EntityUid uid, ICommonSession session, BodyThermalState state, bool immediate)
    {
        // Set next allowed time. Immediate messages still reset the cooldown
        // so we don't flood right after a state change.
        var cooldown = state switch
        {
            BodyThermalState.Sweating  => TimeSpan.FromMinutes(immediate ? 5 : 6),
            BodyThermalState.Shivering => TimeSpan.FromMinutes(immediate ? 5 : 6),
            _                          => TimeSpan.FromSeconds(120),
        };
        _nextAmbientAt[uid] = _timing.CurTime + cooldown;

        var messages = state switch
        {
            BodyThermalState.Sweating  => SweatingMessages,
            BodyThermalState.Shivering => ShiveringMessages,
            _                          => Array.Empty<string>(),
        };

        if (messages.Length == 0)
            return;

        var text = Loc.GetString(_random.Pick(messages));
        _chat.SendPrivateDoMessage(session, text);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Nested types
    // ─────────────────────────────────────────────────────────────────────────

    private enum TemperatureTier : byte
    {
        VeryCold,
        Cold,
        Cool,
        Warm,
        Hot,
        VeryHot,
    }

    private enum BodyThermalState : byte
    {
        Comfortable,
        Sweating,
        Shivering,
    }
}
