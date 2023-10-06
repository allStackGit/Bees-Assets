using System;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public class SquadStatBlock : ICloneable
    {
        public string Commander;
        public int BattlesFought, BattlesWon, ShipsLost, DamageDone, DamageReceived, Kills;
        public int BattlesLost => BattlesFought - BattlesWon;

        public SquadStatBlock(string commander, int battlesFought, int battlesWon, int shipsLost, int damageDone, int damageReceived, int kills) { 
            Commander = commander;
            BattlesFought = battlesFought;
            BattlesWon = battlesWon;
            ShipsLost = shipsLost;
            DamageDone = damageDone;
            DamageReceived = damageReceived;
            Kills = kills;
        }
        public object Clone()
        {
            SquadStatBlock clone = (SquadStatBlock) this.MemberwiseClone();
            return clone;
        }
        public string ToJson()
        {
            return JsonUtility.ToJson(this);
        }
    }
}