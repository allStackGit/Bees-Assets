using Assets.Scripts.Levels.Commands;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels

{
    public class StoredCommand
    {
        public long Age, Tsv, OutcomeId;
        public string Enemy, Squad;
        public string MatchUp, FinalizationCause;

        public ConfigData.CommandTypes CommandType;
        public MatchupStrategy MatchupStrategy;
        public ShootingStrategy ShootingStrategy;
        public List<Vector2> Destinations = new List<Vector2>();
        //public List<ShipStatus> damageSent = new List<ShipStatus>();
        public bool IsFinalized, IsStored, IsHiveMindCommand;

        // current this is just used to get the outcome Id and TSV of a command for sending to the server
        // if was used for more things, like viewing past commands for debugging, it might be useful to use the rest of the properties
        public StoredCommand(Command command)
        {
            Age = command.Age;
            Tsv = command.Tsv;
            OutcomeId = command.OutcomeId;
            //MatchUp = command.Matchup;
            FinalizationCause = command.FinalizationCause;
            //Destinations = command.GetDestinations();
            //damageSent = command.damageSent;
            IsFinalized = command.IsFinalized;
            CommandType = command.CommandType;
            IsStored = false;
            IsHiveMindCommand = command.IsHiveMindCommand;

            Enemy = command.EnemySquad != null ? command.EnemySquad.Name : "Null";
            Squad = command.Squad.Name;

            // these are important for sending the results of these strategies back tot he Hive Mind server
            MatchupStrategy = command.MatchupStrategy;
            ShootingStrategy = command.ShootingStrategy;
        }

    }
}