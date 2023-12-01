

using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Projectiles
{
    public class FireShipExplosion : RocketExplosion
    {


        public override void Kill()
        {
            base.Kill();
            //Debugger.Log("Fire ship explosion kill called");
            Destroy(Shooter.gameObject);
        }
    }
}