using Assets.Scripts.Level.Commands;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Level

{
    public class StoredCommand
    {
        public long Age, Tsv, OutcomeId;
        public string Enemy, Squad;
        public string MatchUp, FinalizationCause;

        public Strategy Strategy;
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
            Strategy= command.Strategy;
            IsStored = command.IsStored;
            IsHiveMindCommand = command.IsHiveMindCommand;

            Enemy = command.Enemy != null ? command.Enemy.name : null;
            //Squad = command.Squad.StaticClone();

            // these are important for sending the results of these strategies back tot he Hive Mind server
            MatchupStrategy = command.MatchupStrategy;
            ShootingStrategy = command.ShootingStrategy;
        }

    }
}