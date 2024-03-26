using Assets.Scripts.Scenes;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;
using Random = System.Random;

namespace Assets.Scripts.Level.Commands
{
    public class MatchupStrategy : Strategy
    {
        public new Squad Squad;
        public new int Side => Squad.Side;
        public new LevelStage Level => Squad.Level;
        public MatchupStrategy(Command command, Squad squad, string name, string matchupString, long matchupId, long outcomeId): base(command, name, matchupString, matchupId, outcomeId)
        {
            Squad = squad;
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
            List<Squad> queue = Level.GetState().GetSquadsVisibleToHiveMind(Side);
            //Debug.Log($"Squads visible to HiveMind {Side}: {queue.Count}");
            //Debug.Log($"Ships visible to HiveMind {Side}: {Level.GetState().GetShipsVisibleToHiveMind(Side).Count}");

            if (queue.Count == 0)
            {
                //Debug.Log($"There were no enemy squads to sort for the targeting queue");
                return null;
            }

            Vector2 location;

            switch (Name)
            {
                case "Random":
                    Random rnd = new Random();
                    return queue.OrderBy(s => rnd.Next()).First();
                case "Revenge":
                    return queue.OrderByDescending(s => s.LastKilled).First();
                case "Most Dangerous":
                    return queue.OrderByDescending(s => s.DamageDone).First();
                case "Least Health":
                    return queue.OrderBy(s => s.Health).First();
                case "Most Health":
                    return queue.OrderByDescending(s => s.Health).First();
                case "Most Powerful":
                    return queue.OrderByDescending(s => s.Firepower).First();
                case "Least Powerful":
                    return queue.OrderBy(s => s.Firepower).First();
                case "Closest":
                    location = Squad.GetPosition();
                    queue.Sort((a, b) => (int)(a.DistanceTo(location) - b.DistanceTo(location)));
                    return queue.First();
                case "Furthest":
                    location = Squad.GetPosition();
                    queue.Sort((a, b) => (int)(b.DistanceTo(location) - a.DistanceTo(location)));
                    return queue.First();
                case "Most Range":
                    return queue.OrderByDescending(s => s.MaxRange).First();
                case "Least Range":
                    return queue.OrderBy(s => s.MaxRange).First();
                case "Fastest":
                    return queue.OrderByDescending(s => s.TotalSpeed).First();
                case "Slowest":
                    return queue.OrderBy(s => s.TotalSpeed).First();
                case "In Combat":
                    return queue.OrderByDescending(s => s.InCombat).ThenBy(s => s.MaxRange).FirstOrDefault();
                case "Gang Up":
                    List<Squad> targetedSquads = Level.GetState().GetTargetedSquads(Side);
                    return targetedSquads.Count > 0 ? targetedSquads.First() : queue.OrderByDescending(s => s.InCombat).ThenBy(s => s.MaxRange).FirstOrDefault(); // In Combat
                case "Most Valuable":
                    return queue.OrderByDescending(s => s.Tsv).First();
                case "Least Valuable":
                    return queue.OrderBy(s => s.Tsv).First();
                case "Type A":
                case "Type B":
                case "Type C":
                case "Type D":
                case "Type E":
                case "Type F":
                case "Type G":
                case "Type H":
                case "Type I":
                case "Type J":
                case "Type K":
                case "Type L":
                case "Type M":
                case "Type N":
                case "Type O":
                case "Type P":
                case "Type Q":
                case "Type R":
                case "Type S":
                case "Type T":
                case "Type U":
                case "Type V":
                    string type = Name.Substring(5);
                    queue.Sort((a, b) =>
                    {
                        int aShipsOfType = a.GetShips().Where(s => s.ShipTypeLetter == type).ToList().Count;
                        int bShipsofType = b.GetShips().Where(s => s.ShipTypeLetter == type).ToList().Count;
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