using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Collects compact, episode-scoped combat diagnostics for RL training. Events update in-memory
/// counters only; the coordinator appends one formatted snapshot to the existing episode log line.
/// Mutable diagnostics are partitioned by Level so simultaneous arenas remain independent.
/// </summary>
internal static class RlOneVsOneEpisodeDiagnostics
{
    private sealed class RootShipRecord
    {
        internal long Id;
        internal string Type;
    }

    private sealed class ChildSummary
    {
        internal int Spawned;
        internal int Alive;
        internal int Damage;
    }

    private sealed class ArenaState
    {
        internal readonly Level Level;
        internal readonly int BeeSide;
        internal readonly int HumanSide;
        internal readonly Dictionary<long, RootShipRecord>[] RootShips =
        {
            new Dictionary<long, RootShipRecord>(),
            new Dictionary<long, RootShipRecord>()
        };
        internal readonly Dictionary<long, string>[] ChildShipTypes =
        {
            new Dictionary<long, string>(),
            new Dictionary<long, string>()
        };
        internal readonly Dictionary<string, int>[] DamageSources =
        {
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal)
        };
        internal readonly Dictionary<string, int>[] DamageByShipType =
        {
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal)
        };
        internal readonly Dictionary<string, int>[] ChildDamageByShipType =
        {
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal)
        };
        internal readonly Dictionary<string, int>[] SpecialActions =
        {
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal)
        };
        internal readonly Dictionary<long, string>[] DeathCauses =
        {
            new Dictionary<long, string>(),
            new Dictionary<long, string>()
        };
        internal readonly int[] StrikerReloads = new int[2];
        internal readonly int[] SelfDamage = new int[2];
        internal readonly int[] FriendlyDamage = new int[2];
        internal readonly int[] UnattributedDamage = new int[2];

        internal ArenaState(Level level, int beeSide, int humanSide)
        {
            Level = level;
            BeeSide = beeSide;
            HumanSide = humanSide;
        }
    }

    private static readonly Dictionary<Level, ArenaState> States = new Dictionary<Level, ArenaState>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ResetStateRegistry()
    {
        States.Clear();
    }

    internal static void Begin(Level level)
    {
        if (level == null || level.State == null || ConfigData.Configuration == null)
        {
            return;
        }

        ArenaState state = new ArenaState(
            level,
            ConfigData.Configuration.BeeSide,
            ConfigData.Configuration.HumanSide);
        States[level] = state;
        RlOneVsOneCombatTelemetry.Begin(level);

        CaptureInitialSide(state, level.State.GetShips(state.BeeSide), 0);
        CaptureInitialSide(state, level.State.GetShips(state.HumanSide), 1);
    }

    internal static void End(Level level)
    {
        if (level == null)
        {
            return;
        }

        RlOneVsOneCombatTelemetry.End(level);
        States.Remove(level);
    }

    /// <summary>
    /// Kept as a compatibility hook for the coordinator. Initial ships are captured in Begin and
    /// newly spawned ships register from GameState.AddShip, so this no longer performs a per-frame fleet scan.
    /// </summary>
    internal static void Track(Level level)
    {
        if (level == null || !States.ContainsKey(level))
        {
            return;
        }
    }

    /// <summary>
    /// Allows ship lifecycle and event paths to register a newly spawned ship immediately.
    /// </summary>
    internal static void TrackShip(Ship ship)
    {
        if (!TryGetSideIndex(ship, out ArenaState state, out int sideIndex))
        {
            return;
        }
        TrackShip(state, ship, sideIndex);
    }

    /// <summary>
    /// Records actual health damage. Enemy damage is broken down both by broad mechanism and by
    /// attacking ship type. Same-side damage is kept separate so it cannot masquerade as damage done.
    /// </summary>
    internal static void RecordAttributedDamage(Ship sourceShip, Ship target, int damage, string source = "gun")
    {
        if (!TryGetSideIndex(sourceShip, out ArenaState sourceState, out int sourceIndex) ||
            !TryGetSideIndex(target, out ArenaState targetState, out int targetIndex) ||
            sourceState != targetState)
        {
            return;
        }

        TrackShip(sourceState, sourceShip, sourceIndex);
        TrackShip(sourceState, target, targetIndex);
        int appliedDamage = Mathf.Max(0, damage);
        if (appliedDamage <= 0)
        {
            return;
        }

        if (sourceShip.Side != target.Side)
        {
            RlOneVsOneCombatTelemetry.RecordHit(sourceShip, target, appliedDamage);
            string sourceName = string.IsNullOrEmpty(source) ? "other" : source;
            string shipType = sourceShip.ShipType.ToString();
            Increment(sourceState.DamageSources[sourceIndex], sourceName, appliedDamage);
            Increment(sourceState.DamageByShipType[sourceIndex], shipType, appliedDamage);
            if (sourceState.ChildShipTypes[sourceIndex].ContainsKey(sourceShip.Id))
            {
                Increment(sourceState.ChildDamageByShipType[sourceIndex], shipType, appliedDamage);
            }
            return;
        }

        if (sourceShip.Id == target.Id)
        {
            sourceState.SelfDamage[sourceIndex] += appliedDamage;
        }
        else
        {
            sourceState.FriendlyDamage[sourceIndex] += appliedDamage;
        }
    }

    /// <summary>
    /// Records damage that does not have a normal attacker path, such as hazards or explicit
    /// self-damage. The optional source label is intentionally not emitted as a separate field;
    /// self/friendly/unattributed totals stay compact and unambiguous.
    /// </summary>
    internal static void RecordUnattributedDamage(Ship target, int damage, string source = "other", bool selfInflicted = false)
    {
        if (!TryGetSideIndex(target, out ArenaState state, out int sideIndex))
        {
            return;
        }

        TrackShip(state, target, sideIndex);
        int appliedDamage = Mathf.Max(0, damage);
        if (appliedDamage <= 0)
        {
            return;
        }

        if (selfInflicted)
        {
            state.SelfDamage[sideIndex] += appliedDamage;
        }
        else
        {
            state.UnattributedDamage[sideIndex] += appliedDamage;
        }
    }

    internal static void RecordSpecialAction(Ship ship, string action)
    {
        if (!TryGetSideIndex(ship, out ArenaState state, out int sideIndex) || string.IsNullOrEmpty(action))
        {
            return;
        }

        TrackShip(state, ship, sideIndex);
        Increment(state.SpecialActions[sideIndex], action, 1);
    }

    internal static void RecordStrikerReplenished(Striker striker)
    {
        if (!TryGetSideIndex(striker, out ArenaState state, out int sideIndex))
        {
            return;
        }

        TrackShip(state, striker, sideIndex);
        state.StrikerReloads[sideIndex]++;
    }

    internal static void RecordShipDeath(Ship victim, Ship killer, bool endKill, string causeOverride = null)
    {
        if (!TryGetSideIndex(victim, out ArenaState state, out int sideIndex))
        {
            return;
        }

        TrackShip(state, victim, sideIndex);
        if ((!state.RootShips[sideIndex].ContainsKey(victim.Id) && !state.ChildShipTypes[sideIndex].ContainsKey(victim.Id)) ||
            state.DeathCauses[sideIndex].ContainsKey(victim.Id))
        {
            return;
        }

        string cause = causeOverride;
        if (string.IsNullOrEmpty(cause))
        {
            if (endKill)
            {
                cause = "warp_return";
            }
            else if (killer == null)
            {
                cause = "unattributed";
            }
            else if (killer.Id == victim.Id)
            {
                cause = "self";
            }
            else if (killer.Side == victim.Side)
            {
                cause = $"friendly-{killer.ShipType}";
            }
            else
            {
                cause = $"enemy-{killer.ShipType}";
            }
        }
        state.DeathCauses[sideIndex][victim.Id] = cause;
    }

    internal static string BuildEpisodeFields(Level level, bool timedOut)
    {
        string combatTelemetry = RlOneVsOneCombatTelemetry.BuildEpisodeFields(level);
        if (!TryGetState(level, out ArenaState state))
        {
            return "bee_ships=none human_ships=none bee_children=none human_children=none " +
                   "bee_striker_reloads=0 human_striker_reloads=0 " +
                   "bee_damage_sources=none human_damage_sources=none bee_damage_by_ship=none human_damage_by_ship=none " +
                   "bee_self_damage=0 human_self_damage=0 bee_friendly_damage=0 human_friendly_damage=0 " +
                   "bee_unattributed_damage=0 human_unattributed_damage=0 " +
                   "bee_specials=none human_specials=none bee_root_outcomes=none human_root_outcomes=none " +
                   combatTelemetry;
        }

        return $"bee_ships={FormatRootShips(state, 0)} human_ships={FormatRootShips(state, 1)} " +
               $"bee_children={FormatChildren(state, 0)} human_children={FormatChildren(state, 1)} " +
               $"bee_striker_reloads={state.StrikerReloads[0]} human_striker_reloads={state.StrikerReloads[1]} " +
               $"bee_damage_sources={FormatCounts(state.DamageSources[0])} human_damage_sources={FormatCounts(state.DamageSources[1])} " +
               $"bee_damage_by_ship={FormatCounts(state.DamageByShipType[0])} human_damage_by_ship={FormatCounts(state.DamageByShipType[1])} " +
               $"bee_self_damage={state.SelfDamage[0]} human_self_damage={state.SelfDamage[1]} " +
               $"bee_friendly_damage={state.FriendlyDamage[0]} human_friendly_damage={state.FriendlyDamage[1]} " +
               $"bee_unattributed_damage={state.UnattributedDamage[0]} human_unattributed_damage={state.UnattributedDamage[1]} " +
               $"bee_specials={FormatCounts(state.SpecialActions[0])} human_specials={FormatCounts(state.SpecialActions[1])} " +
               $"bee_root_outcomes={FormatRootOutcomes(state, 0, timedOut)} human_root_outcomes={FormatRootOutcomes(state, 1, timedOut)} " +
               combatTelemetry;
    }

    private static void CaptureInitialSide(ArenaState state, List<Ship> ships, int sideIndex)
    {
        for (int i = 0; i < ships.Count; i++)
        {
            Ship ship = ships[i];
            if (ship != null && !ship.IsDead)
            {
                TrackShip(state, ship, sideIndex);
            }
        }
    }

    private static void TrackShip(ArenaState state, Ship ship, int sideIndex)
    {
        if (state == null || ship == null || ship.Level != state.Level ||
            state.RootShips[sideIndex].ContainsKey(ship.Id) || state.ChildShipTypes[sideIndex].ContainsKey(ship.Id))
        {
            return;
        }

        if (!ship.IsCarrierShip && !ship.IsMinionShip)
        {
            state.RootShips[sideIndex][ship.Id] = new RootShipRecord { Id = ship.Id, Type = ship.ShipType.ToString() };
            return;
        }

        state.ChildShipTypes[sideIndex][ship.Id] = ship.ShipType.ToString();
    }

    private static bool TryGetSideIndex(Ship ship, out ArenaState state, out int sideIndex)
    {
        state = null;
        sideIndex = -1;
        if (ship == null || !TryGetState(ship.Level, out state))
        {
            return false;
        }
        if (ship.Side == state.BeeSide)
        {
            sideIndex = 0;
            return true;
        }
        if (ship.Side == state.HumanSide)
        {
            sideIndex = 1;
            return true;
        }
        return false;
    }

    private static bool TryGetState(Level level, out ArenaState state)
    {
        state = null;
        return level != null && States.TryGetValue(level, out state) && state != null;
    }

    private static void Increment(Dictionary<string, int> values, string key, int amount)
    {
        values.TryGetValue(key, out int current);
        values[key] = current + amount;
    }

    private static string FormatRootShips(ArenaState state, int sideIndex)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (RootShipRecord record in state.RootShips[sideIndex].Values)
        {
            Increment(counts, record.Type, 1);
        }
        return FormatCounts(counts);
    }

    private static string FormatChildren(ArenaState state, int sideIndex)
    {
        if (state.ChildShipTypes[sideIndex].Count == 0)
        {
            return "none";
        }

        Dictionary<string, ChildSummary> summaries = new Dictionary<string, ChildSummary>(StringComparer.Ordinal);
        foreach (KeyValuePair<long, string> child in state.ChildShipTypes[sideIndex])
        {
            if (!summaries.TryGetValue(child.Value, out ChildSummary summary))
            {
                summary = new ChildSummary();
                summaries[child.Value] = summary;
            }
            summary.Spawned++;
            if (!state.DeathCauses[sideIndex].ContainsKey(child.Key))
            {
                summary.Alive++;
            }
        }

        foreach (KeyValuePair<string, int> damage in state.ChildDamageByShipType[sideIndex])
        {
            if (!summaries.TryGetValue(damage.Key, out ChildSummary summary))
            {
                summary = new ChildSummary();
                summaries[damage.Key] = summary;
            }
            summary.Damage = damage.Value;
        }

        List<string> types = new List<string>(summaries.Keys);
        types.Sort(StringComparer.Ordinal);
        List<string> values = new List<string>(types.Count);
        for (int i = 0; i < types.Count; i++)
        {
            string type = types[i];
            ChildSummary summary = summaries[type];
            values.Add($"{type}:spawn{summary.Spawned},alive{summary.Alive},dmg{summary.Damage}");
        }
        return string.Join("|", values);
    }

    private static string FormatCounts(Dictionary<string, int> counts)
    {
        if (counts.Count == 0)
        {
            return "none";
        }

        List<string> keys = new List<string>(counts.Keys);
        keys.Sort(StringComparer.Ordinal);
        List<string> values = new List<string>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            values.Add($"{keys[i]}:{counts[keys[i]]}");
        }
        return string.Join("|", values);
    }

    private static string FormatRootOutcomes(ArenaState state, int sideIndex, bool timedOut)
    {
        if (state.RootShips[sideIndex].Count == 0)
        {
            return "none";
        }

        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (RootShipRecord root in state.RootShips[sideIndex].Values)
        {
            string outcome = state.DeathCauses[sideIndex].TryGetValue(root.Id, out string cause)
                ? cause
                : timedOut ? "timeout-alive" : "alive";
            Increment(counts, $"{root.Type}/{outcome}", 1);
        }
        return FormatCounts(counts);
    }

    internal static void SetStateForTests(Level level, int beeSide, int humanSide)
    {
        if (level != null)
        {
            States[level] = new ArenaState(level, beeSide, humanSide);
        }
    }

    internal static int GetTrackedLevelCountForTests()
    {
        return States.Count;
    }

    internal static void ResetForTests()
    {
        States.Clear();
    }
}
