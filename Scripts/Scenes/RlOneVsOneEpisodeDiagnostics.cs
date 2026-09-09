using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Collects compact, episode-scoped combat diagnostics for RL training. Events update in-memory
/// counters only; the coordinator appends one formatted snapshot to the existing episode log line.
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

    private static readonly Dictionary<long, RootShipRecord>[] RootShips =
    {
        new Dictionary<long, RootShipRecord>(),
        new Dictionary<long, RootShipRecord>()
    };

    private static readonly Dictionary<long, string>[] ChildShipTypes =
    {
        new Dictionary<long, string>(),
        new Dictionary<long, string>()
    };

    private static readonly Dictionary<string, int>[] DamageSources =
    {
        new Dictionary<string, int>(StringComparer.Ordinal),
        new Dictionary<string, int>(StringComparer.Ordinal)
    };

    private static readonly Dictionary<string, int>[] DamageByShipType =
    {
        new Dictionary<string, int>(StringComparer.Ordinal),
        new Dictionary<string, int>(StringComparer.Ordinal)
    };

    private static readonly Dictionary<string, int>[] ChildDamageByShipType =
    {
        new Dictionary<string, int>(StringComparer.Ordinal),
        new Dictionary<string, int>(StringComparer.Ordinal)
    };

    private static readonly Dictionary<string, int>[] SpecialActions =
    {
        new Dictionary<string, int>(StringComparer.Ordinal),
        new Dictionary<string, int>(StringComparer.Ordinal)
    };

    private static readonly Dictionary<long, string>[] DeathCauses =
    {
        new Dictionary<long, string>(),
        new Dictionary<long, string>()
    };

    private static readonly int[] StrikerReloads = new int[2];
    private static readonly int[] SelfDamage = new int[2];
    private static readonly int[] FriendlyDamage = new int[2];
    private static readonly int[] UnattributedDamage = new int[2];

    private static Level _level;
    private static int _beeSide;
    private static int _humanSide;
    private static bool _active;

    internal static void Begin(Level level)
    {
        Reset();
        if (level == null || level.State == null || ConfigData.Configuration == null)
        {
            return;
        }

        _level = level;
        _beeSide = ConfigData.Configuration.BeeSide;
        _humanSide = ConfigData.Configuration.HumanSide;
        _active = true;
        RlOneVsOneCombatTelemetry.Begin(level);

        CaptureInitialSide(level.State.GetShips(_beeSide), 0);
        CaptureInitialSide(level.State.GetShips(_humanSide), 1);
    }

    internal static void End(Level level)
    {
        if (_active && level == _level)
        {
            RlOneVsOneCombatTelemetry.End(level);
            _active = false;
            _level = null;
        }
    }

    /// <summary>
    /// Kept as a compatibility hook for the coordinator. Initial ships are captured in Begin and
    /// newly spawned ships register from Ship.Setup, so this no longer performs a per-frame fleet scan.
    /// </summary>
    internal static void Track(Level level)
    {
        if (!_active || level == null || level != _level)
        {
            return;
        }
    }

    /// <summary>
    /// Allows ship lifecycle and event paths to register a newly spawned ship immediately.
    /// </summary>
    internal static void TrackShip(Ship ship)
    {
        if (!TryGetSideIndex(ship, out int sideIndex))
        {
            return;
        }
        TrackShip(ship, sideIndex);
    }

    /// <summary>
    /// Records actual health damage. Enemy damage is broken down both by broad mechanism and by
    /// attacking ship type. Same-side damage is kept separate so it cannot masquerade as damage done.
    /// </summary>
    internal static void RecordAttributedDamage(Ship sourceShip, Ship target, int damage, string source = "gun")
    {
        if (!TryGetSideIndex(sourceShip, out int sourceIndex) || !TryGetSideIndex(target, out int targetIndex))
        {
            return;
        }

        TrackShip(sourceShip, sourceIndex);
        TrackShip(target, targetIndex);
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
            Increment(DamageSources[sourceIndex], sourceName, appliedDamage);
            Increment(DamageByShipType[sourceIndex], shipType, appliedDamage);
            if (ChildShipTypes[sourceIndex].ContainsKey(sourceShip.Id))
            {
                Increment(ChildDamageByShipType[sourceIndex], shipType, appliedDamage);
            }
            return;
        }

        if (sourceShip.Id == target.Id)
        {
            SelfDamage[sourceIndex] += appliedDamage;
        }
        else
        {
            FriendlyDamage[sourceIndex] += appliedDamage;
        }
    }

    /// <summary>
    /// Records damage that does not have a normal attacker path, such as hazards or explicit
    /// self-damage. The optional source label is intentionally not emitted as a separate field;
    /// self/friendly/unattributed totals stay compact and unambiguous.
    /// </summary>
    internal static void RecordUnattributedDamage(Ship target, int damage, string source = "other", bool selfInflicted = false)
    {
        if (!TryGetSideIndex(target, out int sideIndex))
        {
            return;
        }

        TrackShip(target, sideIndex);
        int appliedDamage = Mathf.Max(0, damage);
        if (appliedDamage <= 0)
        {
            return;
        }

        if (selfInflicted)
        {
            SelfDamage[sideIndex] += appliedDamage;
        }
        else
        {
            UnattributedDamage[sideIndex] += appliedDamage;
        }
    }

    internal static void RecordSpecialAction(Ship ship, string action)
    {
        if (!TryGetSideIndex(ship, out int sideIndex) || string.IsNullOrEmpty(action))
        {
            return;
        }

        TrackShip(ship, sideIndex);
        Increment(SpecialActions[sideIndex], action, 1);
    }

    internal static void RecordStrikerReplenished(Striker striker)
    {
        if (!TryGetSideIndex(striker, out int sideIndex))
        {
            return;
        }

        TrackShip(striker, sideIndex);
        StrikerReloads[sideIndex]++;
    }

    internal static void RecordShipDeath(Ship victim, Ship killer, bool endKill, string causeOverride = null)
    {
        if (!TryGetSideIndex(victim, out int sideIndex))
        {
            return;
        }

        TrackShip(victim, sideIndex);
        if ((!RootShips[sideIndex].ContainsKey(victim.Id) && !ChildShipTypes[sideIndex].ContainsKey(victim.Id)) ||
            DeathCauses[sideIndex].ContainsKey(victim.Id))
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
        DeathCauses[sideIndex][victim.Id] = cause;
    }

    internal static string BuildEpisodeFields(bool timedOut)
    {
        string combatTelemetry = RlOneVsOneCombatTelemetry.BuildEpisodeFields();
        if (!_active)
        {
            return "bee_ships=none human_ships=none bee_children=none human_children=none " +
                   "bee_striker_reloads=0 human_striker_reloads=0 " +
                   "bee_damage_sources=none human_damage_sources=none bee_damage_by_ship=none human_damage_by_ship=none " +
                   "bee_self_damage=0 human_self_damage=0 bee_friendly_damage=0 human_friendly_damage=0 " +
                   "bee_unattributed_damage=0 human_unattributed_damage=0 " +
                   "bee_specials=none human_specials=none bee_root_outcomes=none human_root_outcomes=none " +
                   combatTelemetry;
        }

        return $"bee_ships={FormatRootShips(0)} human_ships={FormatRootShips(1)} " +
               $"bee_children={FormatChildren(0)} human_children={FormatChildren(1)} " +
               $"bee_striker_reloads={StrikerReloads[0]} human_striker_reloads={StrikerReloads[1]} " +
               $"bee_damage_sources={FormatCounts(DamageSources[0])} human_damage_sources={FormatCounts(DamageSources[1])} " +
               $"bee_damage_by_ship={FormatCounts(DamageByShipType[0])} human_damage_by_ship={FormatCounts(DamageByShipType[1])} " +
               $"bee_self_damage={SelfDamage[0]} human_self_damage={SelfDamage[1]} " +
               $"bee_friendly_damage={FriendlyDamage[0]} human_friendly_damage={FriendlyDamage[1]} " +
               $"bee_unattributed_damage={UnattributedDamage[0]} human_unattributed_damage={UnattributedDamage[1]} " +
               $"bee_specials={FormatCounts(SpecialActions[0])} human_specials={FormatCounts(SpecialActions[1])} " +
               $"bee_root_outcomes={FormatRootOutcomes(0, timedOut)} human_root_outcomes={FormatRootOutcomes(1, timedOut)} " +
               combatTelemetry;
    }

    private static void CaptureInitialSide(List<Ship> ships, int sideIndex)
    {
        for (int i = 0; i < ships.Count; i++)
        {
            Ship ship = ships[i];
            if (ship != null && !ship.IsDead)
            {
                TrackShip(ship, sideIndex);
            }
        }
    }

    private static void TrackShip(Ship ship, int sideIndex)
    {
        if (ship == null || ship.Level != _level || RootShips[sideIndex].ContainsKey(ship.Id) ||
            ChildShipTypes[sideIndex].ContainsKey(ship.Id))
        {
            return;
        }

        if (!ship.IsCarrierShip && !ship.IsMinionShip)
        {
            RootShips[sideIndex][ship.Id] = new RootShipRecord { Id = ship.Id, Type = ship.ShipType.ToString() };
            return;
        }

        ChildShipTypes[sideIndex][ship.Id] = ship.ShipType.ToString();
    }

    private static bool TryGetSideIndex(Ship ship, out int sideIndex)
    {
        sideIndex = -1;
        if (!_active || ship == null || ship.Level != _level)
        {
            return false;
        }
        if (ship.Side == _beeSide)
        {
            sideIndex = 0;
            return true;
        }
        if (ship.Side == _humanSide)
        {
            sideIndex = 1;
            return true;
        }
        return false;
    }

    private static void Increment(Dictionary<string, int> values, string key, int amount)
    {
        values.TryGetValue(key, out int current);
        values[key] = current + amount;
    }

    private static string FormatRootShips(int sideIndex)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (RootShipRecord record in RootShips[sideIndex].Values)
        {
            Increment(counts, record.Type, 1);
        }
        return FormatCounts(counts);
    }

    private static string FormatChildren(int sideIndex)
    {
        if (ChildShipTypes[sideIndex].Count == 0)
        {
            return "none";
        }

        Dictionary<string, ChildSummary> summaries = new Dictionary<string, ChildSummary>(StringComparer.Ordinal);
        foreach (KeyValuePair<long, string> child in ChildShipTypes[sideIndex])
        {
            if (!summaries.TryGetValue(child.Value, out ChildSummary summary))
            {
                summary = new ChildSummary();
                summaries[child.Value] = summary;
            }
            summary.Spawned++;
            if (!DeathCauses[sideIndex].ContainsKey(child.Key))
            {
                summary.Alive++;
            }
        }

        foreach (KeyValuePair<string, int> damage in ChildDamageByShipType[sideIndex])
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

    private static string FormatRootOutcomes(int sideIndex, bool timedOut)
    {
        if (RootShips[sideIndex].Count == 0)
        {
            return "none";
        }

        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (RootShipRecord root in RootShips[sideIndex].Values)
        {
            string outcome = DeathCauses[sideIndex].TryGetValue(root.Id, out string cause)
                ? cause
                : timedOut ? "timeout-alive" : "alive";
            Increment(counts, $"{root.Type}/{outcome}", 1);
        }
        return FormatCounts(counts);
    }

    private static void Reset()
    {
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            RootShips[sideIndex].Clear();
            ChildShipTypes[sideIndex].Clear();
            DamageSources[sideIndex].Clear();
            DamageByShipType[sideIndex].Clear();
            ChildDamageByShipType[sideIndex].Clear();
            SpecialActions[sideIndex].Clear();
            DeathCauses[sideIndex].Clear();
            StrikerReloads[sideIndex] = 0;
            SelfDamage[sideIndex] = 0;
            FriendlyDamage[sideIndex] = 0;
            UnattributedDamage[sideIndex] = 0;
        }
        _active = false;
        _level = null;
        _beeSide = 0;
        _humanSide = 0;
    }
}
