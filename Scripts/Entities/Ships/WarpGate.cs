using Assets.Scripts.Data;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class WarpGate : Ship
    {
        public Vector2 WarpPoint;
        public HashSet<long> ShipsWarpingHere = new HashSet<long>();
        public Collider2D WarpCollider;
        public override void ClearData()
        {
            base.ClearData();
            ShipsWarpingHere.Clear();
        }

        protected override void OnTriggerEnter2D(Collider2D collider)
        {
            if (collider.gameObject.name == "Selection Box")
            {
                //Debug.Log("Striker hit selection box");
                if (IsUserControlled)
                {
                    Stage.Selector.SelectShip(this);
                }
            }
            else if (collider.gameObject.CompareTag("Ship"))
            {
                Ship ship = collider.GetComponent<Ship>();
                if (ship.Side == Side && ship.Squad?.GetCommand()?.CommandType == ConfigData.CommandTypes.FullRetreat && ship.ShipType != this.ShipType)
                {
                    FullRetreat fullRetreat = (FullRetreat)ship.Squad.GetCommand();
                    if (fullRetreat.TargetWarpGate == this)
                    {
                        //Debug.Log($"{ship.Name} hit {Name} and so we're warping it");
                        fullRetreat.ShipsWaitingToWarp.Add(ship);
                        fullRetreat.WaitToWarp();
                    }
                }
            }
        }

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            if (Level.State.GetHumanShips().Where((s) => s.ShipType == ShipType).Count() == 1) // check if this is the last warp gate
            {
                Level.State.HasWarpGates = false;
            }
            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }
        public override void Activate()
        {
            ShipAnimationController.Activate();
            WarpCollider.enabled = true;
            base.Activate();
        }
        public override void Deactivate()
        {
            ShipAnimationController.Deactivate();
            WarpCollider.enabled = false;
            base.Deactivate();
        }
    }
}