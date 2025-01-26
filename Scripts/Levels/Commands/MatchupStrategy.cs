using Assets.Scripts.Scenes;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;
using Random = System.Random;

namespace Assets.Scripts.Levels.Commands
{
    public class MatchupStrategy : Strategy
    {
        public new Squad Squad;
        public new int Side => Squad.Side;
        public new Level Level => Squad.Level;
        public ConfigData.MatchupStrategyTypes MatchupType;

        public MatchupStrategy(Command command, Squad squad, ConfigData.MatchupStrategyTypes type, string matchupString, long matchupId, long outcomeId): base(command, ConfigData.CommandTypes.Matchup, matchupString, matchupId, outcomeId)
        {
            Squad = squad;
            MatchupType = type;
            //Command = command;
            //this.Name = name;
            //this.MatchupString = matchupString; // the string of the matchup e.g. GG|DDDDCC|0|2|0
            //this.MatchupId = matchupId; // the database ID of the matchup that connects to the matchup string in the database
            //this.OutcomeId = outcomeId; // the database ID of the targeting outcome record
            ////Debug.Log("Created MatchupStrategy");
        }

        public Squad SortSquads()
        {
            //Debug.Log("Sorting squads");
            //Debug.Log($"Squad: {Squad}");
            List<Squad> queue = Level.State.GetSquadsVisibleToHiveMind(Side);
            //Debug.Log($"Squads visible to HiveMind {Side}: {queue.Count}");
            //Debug.Log($"Ships visible to HiveMind {Side}: {Level.State.GetShipsVisibleToHiveMind(Side).Count}");

            if (queue.Count == 0)
            {
                //Debug.Log($"There were no enemy squads to sort for the targeting queue");
                return null;
            }

            Vector2 location;

            switch (MatchupType)
            {
                case ConfigData.MatchupStrategyTypes.Random:
                    return queue.OrderBy(s => Utilities.RandomInt(2)).First();
                case ConfigData.MatchupStrategyTypes.Revenge:
                    return queue.OrderByDescending(s => s.LastKilled).First();
                case ConfigData.MatchupStrategyTypes.MostDangerous:
                    return queue.OrderByDescending(s => s.DamageDone).First();
                case ConfigData.MatchupStrategyTypes.LeastHealth:
                    return queue.OrderBy(s => s.Health).First();
                case ConfigData.MatchupStrategyTypes.MostHealth:
                    return queue.OrderByDescending(s => s.Health).First();
                case ConfigData.MatchupStrategyTypes.MostPowerful:
                    return queue.OrderByDescending(s => s.Firepower).First();
                case ConfigData.MatchupStrategyTypes.LeastPowerful:
                    return queue.OrderBy(s => s.Firepower).First();
                case ConfigData.MatchupStrategyTypes.Closest:
                    location = Squad.GetPosition();
                    queue.Sort((a, b) => (int)(a.DistanceToPoint(location) - b.DistanceToPoint(location)));
                    return queue.First();
                case ConfigData.MatchupStrategyTypes.Furthest:
                    location = Squad.GetPosition();
                    queue.Sort((a, b) => (int)(b.DistanceToPoint(location) - a.DistanceToPoint(location)));
                    return queue.First();
                case ConfigData.MatchupStrategyTypes.MostRange:
                    return queue.OrderByDescending(s => s.MaxRange).First();
                case ConfigData.MatchupStrategyTypes.LeastRange:
                    return queue.OrderBy(s => s.MaxRange).First();
                case ConfigData.MatchupStrategyTypes.Fastest:
                    return queue.OrderByDescending(s => s.TotalSpeed).First();
                case ConfigData.MatchupStrategyTypes.Slowest:
                    return queue.OrderBy(s => s.TotalSpeed).First();
                case ConfigData.MatchupStrategyTypes.InCombat:
                    return queue.OrderByDescending(s => s.InCombat).ThenBy(s => s.MaxRange).FirstOrDefault();
                case ConfigData.MatchupStrategyTypes.GangUp:
                    List<Squad> targetedSquads = Level.State.GetTargetedSquads(Side);
                    return targetedSquads.Count > 0 ? targetedSquads.First() : queue.OrderByDescending(s => s.InCombat).ThenBy(s => s.MaxRange).FirstOrDefault(); // In Combat
                case ConfigData.MatchupStrategyTypes.MostValuable:
                    return queue.OrderByDescending(s => s.Tsv).First();
                case ConfigData.MatchupStrategyTypes.LeastValuable:
                    return queue.OrderBy(s => s.Tsv).First();
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
                    ConfigData.ShipTypes type = Utilities.ConvertMatchupStrategyToShipType[MatchupType];
                    queue.Sort((a, b) =>
                    {
                        int aShipsOfType = a.GetShips().Where(s => s.ShipType == type).ToList().Count;
                        int bShipsofType = b.GetShips().Where(s => s.ShipType == type).ToList().Count;
                        if (aShipsOfType > bShipsofType)
                        {
                            return -1;
                        }
                        else if (bShipsofType > aShipsOfType)
                        {
                            return 1;
                        }
                        else
                        {
                            return 0;
                        }
                    });
                    return queue.First();
                default:
                    return queue.First();
            }
        }
    }
}