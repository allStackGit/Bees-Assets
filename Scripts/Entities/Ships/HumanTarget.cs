using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class HumanTarget : Ship
    {

        public override void Activate()
        {
            base.Activate();
            HealthBar.SetActive(false);
        }
    }
}