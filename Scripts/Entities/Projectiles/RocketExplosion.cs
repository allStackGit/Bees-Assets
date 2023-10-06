
using Assets.Scripts.Entities.Ships;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Projectiles
{

    // Explosions don't move but they damage all targets that they touch
    // They operate like normal projectiles upon contact except that contact doesn't kill them and since they linger they can only damage a target once
    public class RocketExplosion : Projectile
    {

        private List<Ship> _shipsHit = new List<Ship>();

        public override void ContactTarget(Ship target)
        {
            //Debugger.Log($"Explosion hit {target.name}");
            _shipsHit.Add(target);
        }

        public bool HasHitShip(Ship ship)
        {
            return _shipsHit.Contains(ship);
        }

        public new virtual void Kill()
        {
            //Debugger.Log("Killed off the rocket explosion");
            Level.GetState().RemoveExplosion(this);
            Destroy(gameObject);
        }

    }




}
