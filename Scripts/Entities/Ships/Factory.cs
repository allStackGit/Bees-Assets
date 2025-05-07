using Assets.Scripts.Data;
using Assets.Scripts.Levels;
using System.Linq;

using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Factory : Ship
    {
        public GameObject MiningAnimation;
        public override void Setup(Level level, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter)
        {
            base.Setup(level, fleetShip, squad, offsetFromCenter);
            ShipAnimationController.Deactivate();
        }
        public override void Create(Stage stage)
        {
            base.Create(stage);
            if (Stage.IsTraining)
            {
                Destroy(MiningAnimation);
            }
        }
        public override void Activate()
        {
            if (!Stage.IsTraining)
            {
                ShipAnimationController.Activate();
            }
            base.Activate();
        }
        public override void Deactivate()
        {
            if (!Stage.IsTraining)
            {
                ShipAnimationController.Deactivate();
            }
            base.Deactivate();
        }
    }
}