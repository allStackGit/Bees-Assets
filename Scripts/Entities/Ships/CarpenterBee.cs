using Assets.Scripts.Level;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class CarpenterBee : Ship
    {
        //public override void Kill(Ship killer, bool endKill = false)
        //{
        //    GameState state = Level.GetState();
        //    if (state.GetBeeShips().Where((s) => s.ShipType == ShipType).Count() == 1) // check if this is the last carpenter bee
        //    {
        //        state.HasMiningShips[Side - 1] = false;
        //    }
        //    base.Kill(killer, endKill);
        //}
    }
}