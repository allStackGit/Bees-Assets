using Assets.Scripts.Levels;
using System.Linq;

using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class CarpenterBee : Ship
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
            base.Activate();
            if (!Stage.IsTraining)
            {
                MiningAnimation.SetActive(true);
            }
        }
        public override void Deactivate()
        {
            base.Deactivate();
            if (!Stage.IsTraining)
            {
                MiningAnimation.SetActive(false);
            }
        }
    }
}