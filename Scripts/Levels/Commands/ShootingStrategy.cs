using System.Collections;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;
using Random = System.Random;

namespace Assets.Scripts.Levels.Commands
{
    public class ShootingStrategy 
    {
        public ConfigData.ShootingStrategyTypes ShootingStrategyType;
        /// <summary>
        /// The matchup outcome Id, not the strategy outcomeId
        /// </summary>
        public long OutcomeId;
        public bool IsDead;
        public ShootingStrategy()
        {
            IsDead = true;
        }
        public void Setup(ConfigData.ShootingStrategyTypes type, long outcomeId)
        {
            ShootingStrategyType = type;
            IsDead = false;
            OutcomeId = outcomeId;
        }
        public void Kill()
        {
            IsDead = true;
        }

    }
}