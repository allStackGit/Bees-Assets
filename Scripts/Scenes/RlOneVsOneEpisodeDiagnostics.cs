using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Collects compact, episode-scoped combat behavior diagnostics for RL training. This class never
/// logs events itself; the coordinator appends one formatted snapshot to the existing episode line.
/// </summary>
internal static class RlOneVsOneEpisodeDiagnostics
{
    private sealed class RootShipRecord
    {
        internal long Id;
        internal string Type;
    }

    private sealed class CarrierRecord
    {
        internal long Id;
        internal readonly Dictionary<string, int> Children = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    private static readonly Dictionary<long, RootShipRecord>[] RootShips =
    {
        new Dictionary<long, RootShipRecord>(),
        new Dictionary<long, RootShipRecord>()
    };

    private static readonly HashSet<long>[] SeenChildren =
    {
        new HashSet<long>(),
        new HashSet<long>()
    };

    private static readonly Dictionary<long, string>[] ChildShipTypes =
    {
        new Dictionary<long, string>(),
        new Dictionary<long, string>()
    };

    private static readonly Dictionary<string, int>[] ChildTypes =
    {
        new Dictionary<string, int>(StringComparer.Ordinal),
        new Dictionary<string, int>(StringComparer.Ordinal)
    };

    private static readonly Dictionary<long, CarrierRecord>[] CarrierChildren =
    {
        new Dictionary<long, CarrierRecord>(),
        new Dictionary<long, CarrierRecord>()
    };

    private static readonly Dictionary<long, int>[] StrikerReloads =
    {
        new Dictionary<long, int>(),
        new Dictionary<long, int>()
    };

    private static readonly Dictionary<string, int>[] DamageSources =
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

        CaptureInitialSide(level.State.GetShips(_beeSide), 0);
        CaptureInitialSide(level.State.GetShips(_humanSide), 1);
    }

    internal static void Track(Level level)
    {
        if (!_active || level == null || level != _level || level.State == null)
        {
            return;
        }

        TrackSide(level.State.GetShips(_beeSide), 0);
        TrackSide(level.State.GetShips(_humanSide), 1);
    }

    internal static void End(Level level)
    {
        if (_active && level == _level)
        {
            _active = false;
            _level = null;
        }
    }

    internal static void RecordAttributedDamage(Ship attacker, Ship target, int damage)
    {
        if (!TryGetSideIndex(attacker, out int attackerIndex) || !TryGetSideIndex(target, out int targetIndex))
        {
            return;
        }

        TrackShip(attacker, attackerIndex, false);
        TrackShip(target, targetIndex, false);
        int appliedDamage = Mathf.Max(0, damage);
        if (appliedDamage <= 0)
        {
            return;
        }

        Increment(DamageSources[attackerIndex], attacker.ShipType.ToString(), appliedDamage);
        if (attacker.Side == target.Side)
        {
            if (attacker.Id == target.Id)
            {
                SelfDamage[attackerIndex] += appliedDamage;
            }
            else
            {
                FriendlyDamage[attackerIndex] += appliedDamage;
            }
        }
    }

    internal static void RecordUnattributedDamage(Ship target, int damage, string source = "unattributed", bool selfInflicted = false)
    {
        if (!TryGetSideIndex(target, out int sideIndex))
        {
            return;
        }

        TrackShip(target, sideIndex, false);
        int appliedDamage = Mathf.Max(0, damage);
        if (appliedDamage <= 0)
        {
            return;
        }

        if (selfInflicted)
        {
            SelfDamage[sideIndex] += appliedDamage;
            Increment(DamageSources[sideIndex], string.IsNullOrEmpty(source) ? target.ShipType.ToString() : source, appliedDamage);
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

        TrackShip(ship, sideIndex, false);
        Increment(SpecialActions[sideIndex], action, 1);
    }

    internal static void RecordStrikerReplenished(Striker striker)
    {
        if (!TryGetSideIndex(striker, out int sideIndex))
        {
            return;
        }

        TrackShip(striker, sideIndex, false);
        StrikerReloads[sideIndex].TryGetValue(striker.Id, out int count);
        StrikerReloads[sideIndex][striker.Id] = count + 1;
    }

    internal static void RecordShipDeath(Ship victim, Ship killer, bool endKill, string causeOverride = null)
    {
        if (!TryGetSideIndex(victim, out int sideIndex))
        {
            return;
        }

        TrackShip(victim, sideIndex, false);
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
        if (!_active)
        {
            return "bee_ships=none human_ships=none bee_children=none human_children=none " +
                   "bee_child_outcomes=none human_child_outcomes=none bee_carriers=none human_carriers=none " +
                   "bee_striker_reloads=none human_striker_reloads=none " +
                   "bee_damage_sources=none human_damage_sources=none bee_self_damage=0 human_self_damage=0 " +
                   "bee_friendly_damage=0 human_friendly_damage=0 bee_unattributed_damage=0 human_unattributed_damage=0 " +
                   "bee_specials=none human_specials=none bee_root_outcomes=none human_root_outcomes=none";
        }

        return $"bee_ships={FormatRootShips(0)} human_ships={FormatRootShips(1)} " +
               $"bee_children={FormatCounts(ChildTypes[0])} human_children={FormatCounts(ChildTypes[1])} " +
               $"bee_child_outcomes={FormatChildOutcomes(0, timedOut)} human_child_outcomes={FormatChildOutcomes(1, timedOut)} " +
               $"bee_carriers={FormatCarriers(0)} human_carriers={FormatCarriers(1)} " +
               $"bee_striker_reloads={FormatStrikerReloads(0)} human_striker_reloads={FormatStrikerReloads(1)} " +
               $"bee_damage_sources={FormatCounts(DamageSources[0])} human_damage_sources={FormatCounts(DamageSources[1])} " +
               $"bee_self_damage={SelfDamage[0]} human_self_damage={SelfDamage[1]} " +
               $"bee_friendly_damage={FriendlyDamage[0]} human_friendly_damage={FriendlyDamage[1]} " +
               $"bee_unattributed_damage={UnattributedDamage[0]} human_unattributed_damage={UnattributedDamage[1]} " +
               $"bee_specials={FormatCounts(SpecialActions[0])} human_specials={FormatCounts(SpecialActions[1])} " +
               $"bee_root_outcomes={FormatRootOutcomes(0, timedOut)} human_root_outcomes={FormatRootOutcomes(1, timedOut)}";
    }

    private static void CaptureInitialSide(List<Ship> ships, int sideIndex)
    {
        for (int i = 0; i < ships.Count; i++)
        {
            Ship ship = ships[i];
            if (ship == null || ship.IsDead)
            {
                continue;
            }

            if (!ship.IsCarrierShip && !ship.IsMinionShip)
            {
                RootShips[sideIndex][ship.Id] = new RootShipRecord { Id = ship.Id, Type = ship.ShipType.ToString() };
                RegisterStriker(ship, sideIndex);
            }
            else
            {
                TrackShip(ship, sideIndex, true);
            }
        }
    }

    private static void TrackSide(List<Ship> ships, int sideIndex)
    {
        for (int i = 0; i < ships.Count; i++)
        {
            Ship ship = ships[i];
            if (ship != null)
            {
                TrackShip(ship, sideIndex, false);
            }
        }
    }

    private static void TrackShip(Ship ship, int sideIndex, bool initialChild)
    {
        if (ship == null || ship.Level != _level)
        {
            return;
        }

        RegisterStriker(ship, sideIndex);
        if (RootShips[sideIndex].ContainsKey(ship.Id))
        {
            return;
        }
        if (!initialChild && SeenChildren[sideIndex].Contains(ship.Id))
        {
            return;
        }
        if (!SeenChildren[sideIndex].Add(ship.Id))
        {
            return;
        }

        string shipType = ship.ShipType.ToString();
        ChildShipTypes[sideIndex][ship.Id] = shipType;
        Increment(ChildTypes[sideIndex], shipType, 1);
        if (ship is CarrierShip carrierShip && carrierShip.Carrier != null)
        {
            long carrierId = carrierShip.Carrier.Id;
            if (!CarrierChildren[sideIndex].TryGetValue(carrierId, out CarrierRecord record))
            {
                record = new CarrierRecord { Id = carrierId };
                CarrierChildren[sideIndex][carrierId] = record;
            }
            Increment(record.Children, shipType, 1);
        }
    }

    private static void RegisterStriker(Ship ship, int sideIndex)
    {
        if (ship is Striker && !StrikerReloads[sideIndex].ContainsKey(ship.Id))
        {
            StrikerReloads[sideIndex][ship.Id] = 0;
        }
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

    private static string FormatChildOutcomes(int sideIndex, bool timedOut)
    {
        if (ChildShipTypes[sideIndex].Count == 0)
        {
            return "none";
        }

        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (KeyValuePair<long, string> child in ChildShipTypes[sideIndex])
        {
            string outcome = DeathCauses[sideIndex].TryGetValue(child.Key, out string cause)
                ? cause
                : timedOut ? "timeout-alive" : "alive";
            Increment(counts, $"{child.Value}/{outcome}", 1);
        }
        return FormatCounts(counts);
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

    private static string FormatCarriers(int sideIndex)
    {
        if (CarrierChildren[sideIndex].Count == 0)
        {
            return "none";
        }

        List<long> ids = new List<long>(CarrierChildren[sideIndex].Keys);
        ids.Sort();
        List<string> values = new List<string>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            CarrierRecord record = CarrierChildren[sideIndex][ids[i]];
            values.Add($"Carrier#{record.Id}[{FormatCounts(record.Children)}]");
        }
        return string.Join("|", values);
    }

    private static string FormatStrikerReloads(int sideIndex)
    {
        if (StrikerReloads[sideIndex].Count == 0)
        {
            return "none";
        }

        List<long> ids = new List<long>(StrikerReloads[sideIndex].Keys);
        ids.Sort();
        List<string> values = new List<string>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            values.Add($"Striker#{ids[i]}:{StrikerReloads[sideIndex][ids[i]]}");
        }
        return string.Join("|", values);
    }

    private static string FormatRootOutcomes(int sideIndex, bool timedOut)
    {
        if (RootShips[sideIndex].Count == 0)
        {
            return "none";
        }

        List<RootShipRecord> roots = new List<RootShipRecord>(RootShips[sideIndex].Values);
        roots.Sort((left, right) =>
        {
            int byType = string.CompareOrdinal(left.Type, right.Type);
            return byType != 0 ? byType : left.Id.CompareTo(right.Id);
        });

        List<string> values = new List<string>(roots.Count);
        for (int i = 0; i < roots.Count; i++)
        {
            RootShipRecord root = roots[i];
            string outcome = DeathCauses[sideIndex].TryGetValue(root.Id, out string cause)
                ? cause
                : timedOut ? "timeout-alive" : "alive";
            values.Add($"{root.Type}#{root.Id}:{outcome}");
        }
        return string.Join("|", values);
    }

    private static void Reset()
    {
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            RootShips[sideIndex].Clear();
            SeenChildren[sideIndex].Clear();
            ChildShipTypes[sideIndex].Clear();
            ChildTypes[sideIndex].Clear();
            CarrierChildren[sideIndex].Clear();
            StrikerReloads[sideIndex].Clear();
            DamageSources[sideIndex].Clear();
            SpecialActions[sideIndex].Clear();
            DeathCauses[sideIndex].Clear();
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
