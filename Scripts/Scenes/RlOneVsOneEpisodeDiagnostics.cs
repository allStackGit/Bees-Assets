using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
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
    internal readonly struct EnvironmentSnapshot
    {
        internal readonly bool StaticObstaclesEnabled;
        internal readonly bool CollisionAsteroidsEnabled;
        internal readonly bool MiningAsteroidsEnabled;
        internal readonly bool StaticLayoutEmpty;
        internal readonly int StaticObstacleCount;
        internal readonly float StaticObstacleAreaFraction;
        internal readonly int CollisionAsteroidsSpawned;
        internal readonly int MiningAsteroidsSpawned;
        internal readonly int BeeStaticObstacleContacts;
        internal readonly int HumanStaticObstacleContacts;
        internal readonly int BeeStaticObstacleDeaths;
        internal readonly int HumanStaticObstacleDeaths;
        internal readonly int BeeCollisionAsteroidHits;
        internal readonly int HumanCollisionAsteroidHits;
        internal readonly int BeeCollisionAsteroidDamage;
        internal readonly int HumanCollisionAsteroidDamage;
        internal readonly int BeeCollisionAsteroidDeaths;
        internal readonly int HumanCollisionAsteroidDeaths;
        internal readonly int BeeMiningEvents;
        internal readonly int HumanMiningEvents;
        internal readonly int BeeResourcesMined;
        internal readonly int HumanResourcesMined;
        internal readonly int BeeMiningAsteroidsMined;
        internal readonly int HumanMiningAsteroidsMined;
        internal readonly int BeeMiningAsteroidsDepleted;
        internal readonly int HumanMiningAsteroidsDepleted;

        private EnvironmentSnapshot(ArenaState state)
        {
            StaticObstaclesEnabled = state.StaticObstaclesEnabled;
            CollisionAsteroidsEnabled = state.CollisionAsteroidsEnabled;
            MiningAsteroidsEnabled = state.MiningAsteroidsEnabled;
            StaticLayoutEmpty = state.StaticLayoutEmpty;
            StaticObstacleCount = state.StaticObstacleCount;
            StaticObstacleAreaFraction = state.StaticObstacleAreaFraction;
            CollisionAsteroidsSpawned = state.CollisionAsteroidsSpawned;
            MiningAsteroidsSpawned = state.MiningAsteroidsSpawned;
            BeeStaticObstacleContacts = state.StaticObstacleContacts[0];
            HumanStaticObstacleContacts = state.StaticObstacleContacts[1];
            BeeStaticObstacleDeaths = state.StaticObstacleDeaths[0];
            HumanStaticObstacleDeaths = state.StaticObstacleDeaths[1];
            BeeCollisionAsteroidHits = state.CollisionAsteroidHits[0];
            HumanCollisionAsteroidHits = state.CollisionAsteroidHits[1];
            BeeCollisionAsteroidDamage = state.CollisionAsteroidDamage[0];
            HumanCollisionAsteroidDamage = state.CollisionAsteroidDamage[1];
            BeeCollisionAsteroidDeaths = state.CollisionAsteroidDeaths[0];
            HumanCollisionAsteroidDeaths = state.CollisionAsteroidDeaths[1];
            BeeMiningEvents = state.MiningEvents[0];
            HumanMiningEvents = state.MiningEvents[1];
            BeeResourcesMined = state.ResourcesMined[0];
            HumanResourcesMined = state.ResourcesMined[1];
            BeeMiningAsteroidsMined = state.MinedAsteroidIds[0].Count;
            HumanMiningAsteroidsMined = state.MinedAsteroidIds[1].Count;
            BeeMiningAsteroidsDepleted = state.DepletedMiningAsteroidIds[0].Count;
            HumanMiningAsteroidsDepleted = state.DepletedMiningAsteroidIds[1].Count;
        }

        internal static EnvironmentSnapshot FromLevel(Level level)
        {
            return TryGetState(level, out ArenaState state)
                ? new EnvironmentSnapshot(state)
                : default;
        }
    }

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
        internal readonly int[] StaticObstacleContacts = new int[2];
        internal readonly int[] StaticObstacleDeaths = new int[2];
        internal readonly int[] CollisionAsteroidHits = new int[2];
        internal readonly int[] CollisionAsteroidDamage = new int[2];
        internal readonly int[] CollisionAsteroidDeaths = new int[2];
        internal readonly int[] MiningEvents = new int[2];
        internal readonly int[] ResourcesMined = new int[2];
        internal readonly Dictionary<string, int>[] StaticObstacleDeathsByShipType =
        {
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal)
        };
        internal readonly Dictionary<string, int>[] CollisionAsteroidDeathsByShipType =
        {
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal)
        };
        internal readonly HashSet<int>[] MinedAsteroidIds =
        {
            new HashSet<int>(),
            new HashSet<int>()
        };
        internal readonly HashSet<int>[] DepletedMiningAsteroidIds =
        {
            new HashSet<int>(),
            new HashSet<int>()
        };
        internal bool StaticObstaclesEnabled;
        internal bool CollisionAsteroidsEnabled;
        internal bool MiningAsteroidsEnabled;
        internal bool StaticLayoutEmpty;
        internal int StaticObstacleCount;
        internal float StaticObstacleAreaFraction;
        internal int CollisionAsteroidsSpawned;
        internal int MiningAsteroidsSpawned;

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
        CaptureEnvironmentBaseline(state);
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

        if (string.Equals(source, "static_obstacle", StringComparison.Ordinal))
        {
            state.StaticObstacleContacts[sideIndex]++;
            if (target.Health <= 0)
            {
                state.StaticObstacleDeaths[sideIndex]++;
                Increment(state.StaticObstacleDeathsByShipType[sideIndex], target.ShipType.ToString(), 1);
                RecordEnvironmentDeathCause(state, target, sideIndex, "static_obstacle");
            }
        }
        else if (string.Equals(source, "collision_asteroid", StringComparison.Ordinal))
        {
            state.CollisionAsteroidHits[sideIndex]++;
            state.CollisionAsteroidDamage[sideIndex] += appliedDamage;
            if (target.Health <= 0)
            {
                state.CollisionAsteroidDeaths[sideIndex]++;
                Increment(state.CollisionAsteroidDeathsByShipType[sideIndex], target.ShipType.ToString(), 1);
                RecordEnvironmentDeathCause(state, target, sideIndex, "collision_asteroid");
            }
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

    internal static void RecordMiningOutcome(Ship ship, MiningAsteroid asteroid, int amountMined, bool depleted)
    {
        if (!TryGetSideIndex(ship, out ArenaState state, out int sideIndex) || amountMined <= 0)
        {
            return;
        }

        TrackShip(state, ship, sideIndex);
        state.MiningEvents[sideIndex]++;
        state.ResourcesMined[sideIndex] += amountMined;
        if (asteroid != null)
        {
            state.MinedAsteroidIds[sideIndex].Add(asteroid.Id);
            if (depleted)
            {
                state.DepletedMiningAsteroidIds[sideIndex].Add(asteroid.Id);
            }
        }
    }

    internal static void RecordCollisionAsteroidSpawned(Level level)
    {
        if (TryGetState(level, out ArenaState state))
        {
            state.CollisionAsteroidsSpawned++;
        }
    }

    internal static EnvironmentSnapshot GetEnvironmentSnapshot(Level level)
    {
        return EnvironmentSnapshot.FromLevel(level);
    }

    internal static string BuildEnvironmentEpisodeFields(Level level)
    {
        if (!TryGetState(level, out ArenaState state))
        {
            return "env_static=0 env_collision=0 env_mining=0 static_obstacles=0 static_obstacle_area_fraction=0.0000 static_layout_empty=0 " +
                   "collision_asteroids_spawned=0 mining_asteroids_spawned=0 " +
                   "bee_static_contacts=0 human_static_contacts=0 bee_static_deaths=0 human_static_deaths=0 " +
                   "bee_asteroid_hits=0 human_asteroid_hits=0 bee_asteroid_damage=0 human_asteroid_damage=0 bee_asteroid_deaths=0 human_asteroid_deaths=0 " +
                   "bee_mining_events=0 human_mining_events=0 bee_resources_mined=0 human_resources_mined=0 " +
                   "bee_mining_asteroids_mined=0 human_mining_asteroids_mined=0 bee_mining_asteroids_depleted=0 human_mining_asteroids_depleted=0";
        }

        return $"env_static={(state.StaticObstaclesEnabled ? 1 : 0)} env_collision={(state.CollisionAsteroidsEnabled ? 1 : 0)} env_mining={(state.MiningAsteroidsEnabled ? 1 : 0)} " +
               $"static_obstacles={state.StaticObstacleCount} static_obstacle_area_fraction={state.StaticObstacleAreaFraction:F4} static_layout_empty={(state.StaticLayoutEmpty ? 1 : 0)} " +
               $"collision_asteroids_spawned={state.CollisionAsteroidsSpawned} mining_asteroids_spawned={state.MiningAsteroidsSpawned} " +
               $"bee_static_contacts={state.StaticObstacleContacts[0]} human_static_contacts={state.StaticObstacleContacts[1]} " +
               $"bee_static_deaths={state.StaticObstacleDeaths[0]} human_static_deaths={state.StaticObstacleDeaths[1]} " +
               $"bee_asteroid_hits={state.CollisionAsteroidHits[0]} human_asteroid_hits={state.CollisionAsteroidHits[1]} " +
               $"bee_asteroid_damage={state.CollisionAsteroidDamage[0]} human_asteroid_damage={state.CollisionAsteroidDamage[1]} " +
               $"bee_asteroid_deaths={state.CollisionAsteroidDeaths[0]} human_asteroid_deaths={state.CollisionAsteroidDeaths[1]} " +
               $"bee_mining_events={state.MiningEvents[0]} human_mining_events={state.MiningEvents[1]} " +
               $"bee_resources_mined={state.ResourcesMined[0]} human_resources_mined={state.ResourcesMined[1]} " +
               $"bee_mining_asteroids_mined={state.MinedAsteroidIds[0].Count} human_mining_asteroids_mined={state.MinedAsteroidIds[1].Count} " +
               $"bee_mining_asteroids_depleted={state.DepletedMiningAsteroidIds[0].Count} human_mining_asteroids_depleted={state.DepletedMiningAsteroidIds[1].Count}";
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
        string environmentTelemetry = BuildEnvironmentEpisodeFields(level);
        string combatTelemetry = RlOneVsOneCombatTelemetry.BuildEpisodeFields(level);
        if (!TryGetState(level, out ArenaState state))
        {
            return "bee_ships=none human_ships=none bee_children=none human_children=none " +
                   "bee_striker_reloads=0 human_striker_reloads=0 " +
                   "bee_damage_sources=none human_damage_sources=none bee_damage_by_ship=none human_damage_by_ship=none " +
                   "bee_self_damage=0 human_self_damage=0 bee_friendly_damage=0 human_friendly_damage=0 " +
                   "bee_unattributed_damage=0 human_unattributed_damage=0 " +
                   "bee_static_deaths_by_ship=none human_static_deaths_by_ship=none " +
                   "bee_asteroid_deaths_by_ship=none human_asteroid_deaths_by_ship=none " +
                   "bee_specials=none human_specials=none bee_root_outcomes=none human_root_outcomes=none " +
                   environmentTelemetry + " " + combatTelemetry;
        }

        return $"bee_ships={FormatRootShips(state, 0)} human_ships={FormatRootShips(state, 1)} " +
               $"bee_children={FormatChildren(state, 0)} human_children={FormatChildren(state, 1)} " +
               $"bee_striker_reloads={state.StrikerReloads[0]} human_striker_reloads={state.StrikerReloads[1]} " +
               $"bee_damage_sources={FormatCounts(state.DamageSources[0])} human_damage_sources={FormatCounts(state.DamageSources[1])} " +
               $"bee_damage_by_ship={FormatCounts(state.DamageByShipType[0])} human_damage_by_ship={FormatCounts(state.DamageByShipType[1])} " +
               $"bee_self_damage={state.SelfDamage[0]} human_self_damage={state.SelfDamage[1]} " +
               $"bee_friendly_damage={state.FriendlyDamage[0]} human_friendly_damage={state.FriendlyDamage[1]} " +
               $"bee_unattributed_damage={state.UnattributedDamage[0]} human_unattributed_damage={state.UnattributedDamage[1]} " +
               $"bee_static_deaths_by_ship={FormatCounts(state.StaticObstacleDeathsByShipType[0])} human_static_deaths_by_ship={FormatCounts(state.StaticObstacleDeathsByShipType[1])} " +
               $"bee_asteroid_deaths_by_ship={FormatCounts(state.CollisionAsteroidDeathsByShipType[0])} human_asteroid_deaths_by_ship={FormatCounts(state.CollisionAsteroidDeathsByShipType[1])} " +
               $"bee_specials={FormatCounts(state.SpecialActions[0])} human_specials={FormatCounts(state.SpecialActions[1])} " +
               $"bee_root_outcomes={FormatRootOutcomes(state, 0, timedOut)} human_root_outcomes={FormatRootOutcomes(state, 1, timedOut)} " +
               environmentTelemetry + " " + combatTelemetry;
    }

    private static void CaptureEnvironmentBaseline(ArenaState state)
    {
        Level level = state.Level;
        state.StaticObstaclesEnabled = RlOneVsOneTrainingBootstrap.CurrentStaticObstaclesEnabled;
        state.CollisionAsteroidsEnabled = RlOneVsOneTrainingBootstrap.CurrentCollisionAsteroidSpawnSeconds > 0f;
        state.MiningAsteroidsEnabled = RlOneVsOneTrainingBootstrap.CurrentMiningAsteroidsEnabled;

        float staticArea = 0f;
        if (level.ObstacleMap != null && level.ObstacleMap.Obstacles != null)
        {
            for (int i = 0; i < level.ObstacleMap.Obstacles.Count; i++)
            {
                StaticObstacle obstacle = level.ObstacleMap.Obstacles[i];
                if (obstacle == null || obstacle.IsDead || !obstacle.KillsShipsOnContact)
                {
                    continue;
                }

                state.StaticObstacleCount++;
                Vector2 size = obstacle.Collider != null
                    ? obstacle.Collider.bounds.size
                    : new Vector2(Mathf.Abs(obstacle.transform.localScale.x), Mathf.Abs(obstacle.transform.localScale.y));
                staticArea += Mathf.Max(0f, size.x) * Mathf.Max(0f, size.y);
            }
        }

        float playableWidth = Mathf.Max(0f, level.MaxX - level.MinX);
        float playableHeight = Mathf.Max(0f, level.MaxY - level.MinY);
        float playableArea = playableWidth * playableHeight;
        state.StaticObstacleAreaFraction = playableArea > 0f
            ? Mathf.Clamp01(staticArea / playableArea)
            : 0f;
        state.StaticLayoutEmpty = state.StaticObstaclesEnabled && state.StaticObstacleCount == 0;

        if (level.State != null && level.State.MiningAsteroids != null)
        {
            foreach (MiningAsteroid asteroid in level.State.MiningAsteroids)
            {
                if (asteroid != null && !asteroid.IsDead && asteroid.Level == level)
                {
                    state.MiningAsteroidsSpawned++;
                }
            }
        }
    }

    private static void RecordEnvironmentDeathCause(ArenaState state, Ship target, int sideIndex, string cause)
    {
        if ((state.RootShips[sideIndex].ContainsKey(target.Id) || state.ChildShipTypes[sideIndex].ContainsKey(target.Id)) &&
            !state.DeathCauses[sideIndex].ContainsKey(target.Id))
        {
            state.DeathCauses[sideIndex][target.Id] = cause;
        }
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
