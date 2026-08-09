using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Projectiles
{
    public class PowerShot : LaserShot
    {
        private int _powerLoss;
        private HashSet<Ship> _shipsHit = new HashSet<Ship>();

        public override void ClearData()
        {
            base.ClearData();
            _shipsHit.Clear();
        }

        public override void ContactTarget(Ship target)
        {
            if (_shipsHit.Contains(target))
            {
                return;
            }

            _shipsHit.Add(target);
            _powerLoss = Mathf.Clamp(target.Health, 0, Power);
            if (Power <= target.Health)
            {
                KillSequence();
                return;
            }

            Power -= _powerLoss;
            _powerLoss = 0;
        }
    }
}
