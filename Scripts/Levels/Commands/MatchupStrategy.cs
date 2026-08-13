using Assets.Scripts.Scenes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;
using Random = System.Random;

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
        //public bool IsDead;

        public MatchupStrategy()
        {
            //IsDead = true;
        }
        public void Setup(ConfigData.MatchupStrategyTypes type, long outcomeId, Squad squad)
        {
            OutcomeId = outcomeId;
            MatchupType = type;
            Squad = squad;
            Side = Squad.Side;
            Level = Squad.Level;
            //IsDead = false;
        }
        //public void Kill()
        //{
        //    IsDead = true;
        //}
        private List<Squad> _queue, _targetedSquads;
        private Vector2 _location;
        private ConfigData.ShipTypes _type;
        private readonly Dictionary<Squad, float> _distanceKeys = new Dictionary<Squad, float>();
        private readonly Dictionary<Squad, int> _typeCountKeys = new Dictionary<Squad, int>();

        private Squad SelectByScore(Func<Squad, double> score, bool descending)
        {
            Squad selected = _queue[0];
            double selectedScore = score(selected);
            for (int i = 1; i < _queue.Count; i++)
            {
                Squad candidate = _queue[i];
                double candidateScore = score(candidate);
                if ((descending && candidateScore > selectedScore) || (!descending && candidateScore < selectedScore))
                {
                    selected = candidate;
                    selectedScore = candidateScore;
                }
            }
            return selected;
        }

        private Squad SelectInCombat()
        {
            Squad selected = _queue[0];
            for (int i = 1; i < _queue.Count; i++)
            {
                Squad candidate = _queue[i];
                if ((candidate.InCombat && !selected.InCombat) ||
                    (candidate.InCombat == selected.InCombat && candidate.MaxRange < selected.MaxRange))
                {
                    selected = candidate;
                }
            }
            return selected;
        }

        public Squad SortSquads()
        {
            _queue = Level.State.GetSquadsVisibleToHiveMind(Side);

            if (_queue.Count == 0)
            {
                return null;
            }

            switch (MatchupType)
            {
                case ConfigData.MatchupStrategyTypes.Random:
                    return _queue[Utilities.RandomInt(_queue.Count)];

                case ConfigData.MatchupStrategyTypes.Revenge:
                    return SelectByScore(s => s.LastKilled, true);

                case ConfigData.MatchupStrategyTypes.MostDangerous:
                    return SelectByScore(s => s.DamageDone, true);

                case ConfigData.MatchupStrategyTypes.LeastHealth:
                    return SelectByScore(s => s.Health, false);

                case ConfigData.MatchupStrategyTypes.MostHealth:
                    return SelectByScore(s => s.Health, true);

                case ConfigData.MatchupStrategyTypes.MostPowerful:
                    return SelectByScore(s => s.Firepower, true);

                case ConfigData.MatchupStrategyTypes.LeastPowerful:
                    return SelectByScore(s => s.Firepower, false);

                case ConfigData.MatchupStrategyTypes.Closest:
                    _location = Squad.GetPosition();
                    _distanceKeys.Clear();
                    foreach (Squad candidate in _queue)
                    {
                        _distanceKeys[candidate] = candidate.DistanceToPoint(_location);
                    }
                    _queue.Sort((a, b) => _distanceKeys[a].CompareTo(_distanceKeys[b]));
                    return _queue.First();

                case ConfigData.MatchupStrategyTypes.Furthest:
                    _location = Squad.GetPosition();
                    _distanceKeys.Clear();
                    foreach (Squad candidate in _queue)
                    {
                        _distanceKeys[candidate] = candidate.DistanceToPoint(_location);
                    }
                    _queue.Sort((a, b) => _distanceKeys[b].CompareTo(_distanceKeys[a]));
                    return _queue.First();

                case ConfigData.MatchupStrategyTypes.MostRange:
                    return SelectByScore(s => s.MaxRange, true);

                case ConfigData.MatchupStrategyTypes.LeastRange:
                    return SelectByScore(s => s.MaxRange, false);

                case ConfigData.MatchupStrategyTypes.Fastest:
                    return SelectByScore(s => s.TotalSpeed, true);

                case ConfigData.MatchupStrategyTypes.Slowest:
                    return SelectByScore(s => s.TotalSpeed, false);

                case ConfigData.MatchupStrategyTypes.InCombat:
                    return SelectInCombat();

                case ConfigData.MatchupStrategyTypes.GangUp:
                    _targetedSquads = Level.State.GetTargetedSquads(Side);
                    return _targetedSquads.Count > 0 ? _targetedSquads.First() : SelectInCombat();

                case ConfigData.MatchupStrategyTypes.MostValuable:
                    return SelectByScore(s => s.Tsv, true);

                case ConfigData.MatchupStrategyTypes.LeastValuable:
                    return SelectByScore(s => s.Tsv, false);

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
                    _typeCountKeys.Clear();
                    foreach (Squad candidate in _queue)
                    {
                        int count = 0;
                        foreach (var ship in candidate.GetShips())
                        {
                            if (ship.ShipType == _type)
                            {
                                count++;
                            }
                        }
                        _typeCountKeys[candidate] = count;
                    }
                    _queue.Sort((a, b) => _typeCountKeys[b].CompareTo(_typeCountKeys[a]));
                    return _queue.First();
                default:
                    return _queue.First();
            }
        }
    }
}