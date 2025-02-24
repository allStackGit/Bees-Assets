using Assets.Scripts.Levels;
using System.Linq;

using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Factory : Ship
    {
        public override void Activate()
        {
            ShipAnimationController.Activate();
            base.Activate();
        }
        public override void Deactivate()
        {
            ShipAnimationController.Deactivate();
            base.Deactivate();
        }
    }
}