using Assets.Scripts.Level;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class WarpGate : Ship
    {
        public void OnTriggerEnter2D(Collider2D collider)
        {
            if (collider.gameObject.CompareTag("Ship"))
            {
                Ship ship = collider.GetComponent<Ship>();
                if (ship.Side == Side && ship.Squad?.Command?.Strategy.Name == "Full Retreat")
                {
                    FullRetreat fullRetreat = (FullRetreat)ship.Squad.Command;
                    if (fullRetreat.TargetWarpGate == this)
                    {
                        //Debug.Log($"{ship.Name} hit {Name} and so we're warping it");
                        fullRetreat.Warp(ship);
                    }
                }
            }
        }

        public override void Kill(Ship killer, bool endKill = false)
        {
            GameState state = Level.GetState();
            if (state.GetHumanShips().Where((s) => s.ShipType == ShipType).Count() == 1) // check if this is the last warp gate
            {
                state.HasWarpGates = false;
            }
            base.Kill(killer, endKill);
        }
    }
}