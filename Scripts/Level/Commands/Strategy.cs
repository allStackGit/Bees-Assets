using Assets.Scripts.Scenes;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class Strategy
    {
        // the historical total TSV of the strat
        // the historical number of times this strat has been used on this matchup
        // matchupID is the id of the ship matchup
        // stratID is the id of the type of strat [ no longer in use ]
        // outcomeID is the id of the specific strategic outcome for this usage
        public string Name, MatchupString;
        public long MatchupId, OutcomeId;
        public bool Banned;
        public Command Command;
        public Squad Squad => Command.Squad;
        public int Side => Squad.Side;
        public LevelStage Level => Squad.Level;
        
        public Strategy(Command command, string name, string matchupString, long matchupId, long outcomeId)
        {
            Command = command;
            this.Name = name;
            this.MatchupString = matchupString; // the string of the matchup e.g. GG|DDDDCC|0|2|0
            this.MatchupId = matchupId; 
            this.OutcomeId = outcomeId; 
        }
        
    }
}
