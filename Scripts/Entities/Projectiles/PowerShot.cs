using Assets.Scripts.Entities.Ships;
using System.Collections;
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
            if (!_shipsHit.Contains(target))
            {
                _shipsHit.Add(target);
                //Debug.Log($"{Name} hit {target.Name}");
                _powerLoss = Mathf.Clamp(target.Health, 0, Power);
                if (Power <= target.Health)
                {
                    KillSequence();
                }
                Power -= _powerLoss;
                //Debug.Log($"{Name} lost {_powerLoss} power and now has {Power} power.");
                _powerLoss = 0;
            }
            

        }
    }
}