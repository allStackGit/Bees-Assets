using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Entities.Ships;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    /// <summary>
    /// Low-overhead, opt-in counters for diagnosing combat freezes. Unlike DebugLogger's
    /// detailed inspector state, these counters do not rebuild large runtime collections every
    /// frame. A single aggregate line is emitted once per real-time second when enabled.
    /// </summary>
    public static class FreezeDiagnostics
    {
        private sealed class Counters
        {
            public float IntervalStart;
            public float WorstFrameMs;
            public int AggressiveTicks;
            public int HiveMindSightEnters;
            public int FirstSideWideSightings;
            public int WeaponRangeEnters;
            public int TurretTargetingPasses;
            public int TurretCandidates;
            public int PathRequests;
            public readonly Dictionary<long, int> PathRequestsByShip = new Dictionary<long, int>();
            public readonly Dictionary<long, string> PathShipNames = new Dictionary<long, string>();
        }

        private static readonly Dictionary<Level, Counters> LevelCounters = new Dictionary<Level, Counters>();

        public static void RecordAggressiveTick(Level level)
        {
            Counters counters = GetEnabledCounters(level);
            if (counters != null)
            {
                counters.AggressiveTicks++;
            }
        }

        public static void RecordHiveMindSightEnter(Level level, bool firstSideWideSighting)
        {
            Counters counters = GetEnabledCounters(level);
            if (counters == null)
            {
                return;
            }

            counters.HiveMindSightEnters++;
            if (firstSideWideSighting)
            {
                counters.FirstSideWideSightings++;
            }
        }

        public static void RecordWeaponRangeEnter(Level level)
        {
            Counters counters = GetEnabledCounters(level);
            if (counters != null)
            {
                counters.WeaponRangeEnters++;
            }
        }

        public static void RecordTurretTargetingPass(Level level, int candidateCount)
        {
            Counters counters = GetEnabledCounters(level);
            if (counters == null)
            {
                return;
            }

            counters.TurretTargetingPasses++;
            counters.TurretCandidates += Mathf.Max(0, candidateCount);
        }

        public static void RecordPathRequest(Ship ship)
        {
            Level level = ship?.Level;
            Counters counters = GetEnabledCounters(level);
            if (counters == null)
            {
                return;
            }

            counters.PathRequests++;
            long id = ship.Id;
            counters.PathRequestsByShip[id] = counters.PathRequestsByShip.GetValueOrDefault(id) + 1;
            counters.PathShipNames[id] = ship.Name;
        }

        public static void Tick(Level level)
        {
            if (level == null)
            {
                return;
            }
            if (!level.EnableFreezeDiagnostics)
            {
                LevelCounters.Remove(level);
                return;
            }

            Counters counters = GetEnabledCounters(level);
            counters.WorstFrameMs = Mathf.Max(counters.WorstFrameMs, Time.unscaledDeltaTime * 1000f);
            float now = Time.realtimeSinceStartup;
            if (now - counters.IntervalStart < 1f)
            {
                return;
            }

            int pathQueueDepth = level.Pathfinder?.PathsWaiting.Count ?? 0;
            int activePathWorkers = level.Pathfinder?.IsThreadActive.Count(active => active) ?? 0;
            int projectiles = level.State?.Projectiles.Count ?? 0;
            int timers = level.Timers?.Count ?? 0;

            long busiestShipId = 0;
            int busiestShipRequests = 0;
            foreach (KeyValuePair<long, int> pair in counters.PathRequestsByShip)
            {
                if (pair.Value > busiestShipRequests)
                {
                    busiestShipId = pair.Key;
                    busiestShipRequests = pair.Value;
                }
            }
            string busiestShip = busiestShipRequests > 0 && counters.PathShipNames.TryGetValue(busiestShipId, out string name)
                ? $"{name}:{busiestShipRequests}"
                : "none";

            level.__FreezeDiagnosticsLastSnapshot =
                $"frameWorst={counters.WorstFrameMs:F1}ms aggressive={counters.AggressiveTicks} " +
                $"pathRequests={counters.PathRequests} busiestPathShip={busiestShip} " +
                $"pathQueue={pathQueueDepth} activePathWorkers={activePathWorkers} " +
                $"hiveSightEnters={counters.HiveMindSightEnters} firstSightings={counters.FirstSideWideSightings} " +
                $"weaponRangeEnters={counters.WeaponRangeEnters} turretPasses={counters.TurretTargetingPasses} " +
                $"turretCandidates={counters.TurretCandidates} projectiles={projectiles} timers={timers}";
            Debug.Log($"[FreezeDiag:{level.Name}] {level.__FreezeDiagnosticsLastSnapshot}");

            counters.IntervalStart = now;
            counters.WorstFrameMs = 0f;
            counters.AggressiveTicks = 0;
            counters.HiveMindSightEnters = 0;
            counters.FirstSideWideSightings = 0;
            counters.WeaponRangeEnters = 0;
            counters.TurretTargetingPasses = 0;
            counters.TurretCandidates = 0;
            counters.PathRequests = 0;
            counters.PathRequestsByShip.Clear();
            counters.PathShipNames.Clear();
        }

        private static Counters GetEnabledCounters(Level level)
        {
            if (level == null || !level.EnableFreezeDiagnostics)
            {
                return null;
            }

            if (!LevelCounters.TryGetValue(level, out Counters counters))
            {
                counters = new Counters { IntervalStart = Time.realtimeSinceStartup };
                LevelCounters.Add(level, counters);
            }
            return counters;
        }
    }

    public partial class Level
    {
        [Tooltip("Emit one aggregate combat/pathfinding freeze-diagnostic line per second without enabling the heavy DebugLogger mode.")]
        public bool EnableFreezeDiagnostics;

        [TextArea]
        public string __FreezeDiagnosticsLastSnapshot;

        private void LateUpdate()
        {
            FreezeDiagnostics.Tick(this);
        }
    }
}
