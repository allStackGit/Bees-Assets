using Assets.Scripts.Scenes;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Strategy
    {
        // the historical total TSV of the strat
        // the historical number of times this strat has been used on this matchup
        // matchupID is the id of the ship matchup
        // stratID is the id of the type of strat [ no longer in use ]
        // outcomeID is the id of the specific strategic outcome for this usage
        //public string MatchupString; // [testing]
        public ConfigData.CommandTypes CommandType;
        //public long MatchupId; // [testing]
        public long OutcomeId;
        public bool IsDead;
        
        public Strategy()
        {
            IsDead = true;
        }

        public void Setup(ConfigData.CommandTypes commandType, long outcomeId)
        {
            //Command = command;
            CommandType = commandType;
            //MatchupString = matchupString; // the string of the matchup e.g. GG|DDDDCC|0|2|0
            //MatchupId = matchupId;
            OutcomeId = outcomeId;
            IsDead = false;
        }

        public void Kill()
        {
            IsDead = true;
        }
        
    }
}
