using Assets.Scripts.Levels;
using System.Linq;

using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Factory : Ship
    {
        //public override void Kill(Ship killer, bool endKill = false)
        //{
        //    GameState state = Level.GetState();
        //    if (state.GetHumanShips().Where((s) => s.ShipType == ShipType).Count() == 1) // check if this is the last factory
        //    {
        //        state.HasMiningShips[Side - 1] = false;
        //    }
        //    base.Kill(killer, endKill);
        //}
    }
}