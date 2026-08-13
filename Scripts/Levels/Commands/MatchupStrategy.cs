using System.Collections.Generic;
using Assets.Scripts.Entities.Ships;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class MatchupStrategy
    {
        public Squad Squad;
        public ConfigData.MatchupStrategyTypes MatchupType;
        /// <summary>
        /// The matchup outcome Id, not the strategy outcomeId
        /// </summary>
        public long OutcomeId;
        public int Side;
        public Level Level;

        public void Setup(ConfigData.MatchupStrategyTypes type, long outcomeId, Squad squad)
        {
            OutcomeId = outcomeId;
            MatchupType = type;
            Squad = squad;
            Side = Squad.Side;
            Level = Squad.Level;
        }

        private readonly List<Squad> _queue = new List<Squad>();
        private readonly HashSet<Squad> _visibleSquads = new HashSet<Squad>(ReferenceIdentityComparer<Squad>.Instance);
        private List<Squad> _targetedSquads;
        private Vector2 _location;
        private ConfigData.ShipTypes _type;

        private void BuildVisibleSquadQueue()
        {
            _queue.Clear();
            _visibleSquads.Clear();

            if (Side == ConfigData.Configuration.UserSide && Level.HasPlayer)
            {
                List<Squad> squads = Level.State.GetAllSquads();
                for (int i = 0; i < squads.Count; i++)
                {
                    Squad candidate = squads[i];
                    if (candidate.Side != Side && !candidate.IsDead)
                    {
                        _queue.Add(candidate);
                    }
                }
                return;
            }

            foreach (Ship ship in Level.State.GetShipsVisibleToHiveMind(Side))
            {
                Squad candidate = ship.Squad;
                if (candidate != null && !candidate.IsDead && _visibleSquads.Add(candidate))
                {
                    _queue.Add(candidate);
                }
            }
        }

        private double GetCurrentMetric(Squad squad)
        {
            List<Ship> ships = squad.GetShips();
            double total = 0d;
            switch (MatchupType)
            {
                case ConfigData.MatchupStrategyTypes.Revenge:
                    for (int i = 0; i < ships.Count; i++)
                    {
                        if (ships[i].LastKilled > total) total = ships[i].LastKilled;
                    }
                    return total;
                case ConfigData.MatchupStrategyTypes.MostDangerous:
                    for (int i = 0; i < ships.Count; i++) total += ships[i].FleetShip.DamageDone;
                    return total;
                case ConfigData.MatchupStrategyTypes.LeastHealth:
                case ConfigData.MatchupStrategyTypes.MostHealth:
                    for (int i = 0; i < ships.Count; i++) total += ships[i].Health;
                    return total;
                case ConfigData.MatchupStrategyTypes.MostPowerful:
                case ConfigData.MatchupStrategyTypes.LeastPowerful:
                    for (int i = 0; i < ships.Count; i++) total += ships[i].Firepower;
                    return total;
                case ConfigData.MatchupStrategyTypes.MostRange:
                case ConfigData.MatchupStrategyTypes.LeastRange:
                    for (int i = 0; i < ships.Count; i++)
                    {
                        if (ships[i].MaxRange > total) total = ships[i].MaxRange;
                    }
                    return total;
                case ConfigData.MatchupStrategyTypes.Fastest:
                case ConfigData.MatchupStrategyTypes.Slowest:
                    for (int i = 0; i < ships.Count; i++) total += ships[i].Speed;
                    return total;
                case ConfigData.MatchupStrategyTypes.MostValuable:
                case ConfigData.MatchupStrategyTypes.LeastValuable:
                    for (int i = 0; i < ships.Count; i++) total += ships[i].Tsv;
                    return total;
                default:
                    return 0d;
            }
        }

        private Squad SelectByCurrentMetric(bool descending)
        {
            Squad selected = _queue[0];
            double selectedScore = GetCurrentMetric(selected);
            for (int i = 1; i < _queue.Count; i++)
            {
                Squad candidate = _queue[i];
                double candidateScore = GetCurrentMetric(candidate);
                if ((descending && candidateScore > selectedScore) || (!descending && candidateScore < selectedScore))
                {
                    selected = candidate;
                    selectedScore = candidateScore;
                }
            }
            return selected;
        }

        private static bool IsSquadInCombat(Squad squad)
        {
            List<Ship> ships = squad.GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].InCombat)
                {
                    return true;
                }
            }
            return false;
        }

        private static int GetSquadMaxRange(Squad squad)
        {
            int maxRange = 0;
            List<Ship> ships = squad.GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].MaxRange > maxRange)
                {
                    maxRange = ships[i].MaxRange;
                }
            }
            return maxRange;
        }

        private Squad SelectInCombat()
        {
            Squad selected = _queue[0];
            bool selectedInCombat = IsSquadInCombat(selected);
            int selectedMaxRange = GetSquadMaxRange(selected);
            for (int i = 1; i < _queue.Count; i++)
            {
                Squad candidate = _queue[i];
                bool candidateInCombat = IsSquadInCombat(candidate);
                int candidateMaxRange = GetSquadMaxRange(candidate);
                if ((candidateInCombat && !selectedInCombat) ||
                    (candidateInCombat == selectedInCombat && candidateMaxRange < selectedMaxRange))
                {
                    selected = candidate;
                    selectedInCombat = candidateInCombat;
                    selectedMaxRange = candidateMaxRange;
                }
            }
            return selected;
        }

        private Squad SelectByDistance(bool furthest)
        {
            _location = Squad.GetPosition();
            Squad selected = _queue[0];
            float selectedDistance = selected.DistanceToPoint(_location);
            for (int i = 1; i < _queue.Count; i++)
            {
                Squad candidate = _queue[i];
                float candidateDistance = candidate.DistanceToPoint(_location);
                if ((furthest && candidateDistance > selectedDistance) ||
                    (!furthest && candidateDistance < selectedDistance))
                {
                    selected = candidate;
                    selectedDistance = candidateDistance;
                }
            }
            return selected;
        }

        private Squad SelectByTypeCount(ConfigData.ShipTypes type)
        {
            Squad selected = _queue[0];
            int selectedCount = CountShipsOfType(selected, type);
            for (int i = 1; i < _queue.Count; i++)
            {
                Squad candidate = _queue[i];
                int candidateCount = CountShipsOfType(candidate, type);
                if (candidateCount > selectedCount)
                {
                    selected = candidate;
                    selectedCount = candidateCount;
                }
            }
            return selected;
        }

        private static int CountShipsOfType(Squad squad, ConfigData.ShipTypes type)
        {
            int count = 0;
            List<Ship> ships = squad.GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].ShipType == type)
                {
                    count++;
                }
            }
            return count;
        }

        public Squad SortSquads()
        {
            BuildVisibleSquadQueue();
            if (_queue.Count == 0)
            {
                return null;
            }

            switch (MatchupType)
            {
                case ConfigData.MatchupStrategyTypes.Random:
                    return _queue[Utilities.RandomInt(_queue.Count)];
                case ConfigData.MatchupStrategyTypes.Revenge:
                case ConfigData.MatchupStrategyTypes.MostDangerous:
                case ConfigData.MatchupStrategyTypes.MostHealth:
                case ConfigData.MatchupStrategyTypes.MostPowerful:
                case ConfigData.MatchupStrategyTypes.MostRange:
                case ConfigData.MatchupStrategyTypes.Fastest:
                case ConfigData.MatchupStrategyTypes.MostValuable:
                    return SelectByCurrentMetric(true);
                case ConfigData.MatchupStrategyTypes.LeastHealth:
                case ConfigData.MatchupStrategyTypes.LeastPowerful:
                case ConfigData.MatchupStrategyTypes.LeastRange:
                case ConfigData.MatchupStrategyTypes.Slowest:
                case ConfigData.MatchupStrategyTypes.LeastValuable:
                    return SelectByCurrentMetric(false);
                case ConfigData.MatchupStrategyTypes.Closest:
                    return SelectByDistance(false);
                case ConfigData.MatchupStrategyTypes.Furthest:
                    return SelectByDistance(true);
                case ConfigData.MatchupStrategyTypes.InCombat:
                    return SelectInCombat();
                case ConfigData.MatchupStrategyTypes.GangUp:
                    _targetedSquads = Level.State.GetTargetedSquads(Side);
                    return _targetedSquads.Count > 0 ? _targetedSquads[0] : SelectInCombat();
                case ConfigData.MatchupStrategyTypes.TypeA:
                case ConfigData.MatchupStrategyTypes.TypeB:
                case ConfigData.MatchupStrategyTypes.TypeC:
                case ConfigData.MatchupStrategyTypes.TypeD:
                case ConfigData.MatchupStrategyTypes.TypeE:
                case ConfigData.MatchupStrategyTypes.TypeF:
                case ConfigData.MatchupStrategyTypes.TypeG:
                case ConfigData.MatchupStrategyTypes.TypeH:
                case ConfigData.MatchupStrategyTypes.TypeI:
                case ConfigData.MatchupStrategyTypes.TypeJ:
                case ConfigData.MatchupStrategyTypes.TypeK:
                case ConfigData.MatchupStrategyTypes.TypeL:
                case ConfigData.MatchupStrategyTypes.TypeM:
                case ConfigData.MatchupStrategyTypes.TypeN:
                case ConfigData.MatchupStrategyTypes.TypeO:
                case ConfigData.MatchupStrategyTypes.TypeP:
                case ConfigData.MatchupStrategyTypes.TypeQ:
                case ConfigData.MatchupStrategyTypes.TypeR:
                case ConfigData.MatchupStrategyTypes.TypeS:
                case ConfigData.MatchupStrategyTypes.TypeT:
                case ConfigData.MatchupStrategyTypes.TypeU:
                case ConfigData.MatchupStrategyTypes.TypeV:
                case ConfigData.MatchupStrategyTypes.TypeW:
                case ConfigData.MatchupStrategyTypes.TypeX:
                    _type = Utilities.ConvertMatchupStrategyToShipType[MatchupType];
                    return SelectByTypeCount(_type);
                default:
                    return _queue[0];
            }
        }
    }
}
