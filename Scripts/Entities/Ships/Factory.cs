using Assets.Scripts.Levels;
using System.Linq;

using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Factory : Ship
    {
        public GameObject MiningAnimation;
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