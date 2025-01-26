using System.Collections;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;
using Random = System.Random;

namespace Assets.Scripts.Levels.Commands
{
    public class ShootingStrategy : Strategy
    {
        public ConfigData.ShootingStrategyTypes ShootingStrategyType;
        public ShootingStrategy(Command command, ConfigData.ShootingStrategyTypes type, string matchupString, long matchupId, long outcomeId): base(command, ConfigData.CommandTypes.Shooting, matchupString, matchupId, outcomeId)
        {
            ShootingStrategyType = type;
            //Command = command;
            //this.Name = name;
            //this.MatchupString = matchupString; // the string of the matchup e.g. GG|DDDDCC|0|2|0
            //this.MatchupId = matchupId; // the database ID of the matchup that connects to the matchup string in the database
            //this.OutcomeId = outcomeId; // the database ID of the shooting outcome record
        }

        // The Targeting Queue is made by the ship and not by the Strategy

    }
}