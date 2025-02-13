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
        public HashSet<Ship> ShipsWarpingHere = new HashSet<Ship>();
        public Collider2D WarpCollider;
        public override void ClearData()
        {
            base.ClearData();
            ShipsWarpingHere.Clear();
        }

        public void OnTriggerEnter2D(Collider2D collider)
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
                if (ship.Side == Side && ship.Squad?.Command?.CommandType == ConfigData.CommandTypes.FullRetreat && ship.ShipType != this.ShipType)
                {
                    FullRetreat fullRetreat = (FullRetreat)ship.Squad.Command;
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
    }
}