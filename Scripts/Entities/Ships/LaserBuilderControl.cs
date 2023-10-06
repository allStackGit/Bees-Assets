using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class LaserBuilderControl : MonoBehaviour
    {
        public LaserBuilder LaserBuilder;
        public void Setup(LaserBuilder laserBuilder)
        {
            //Debugger.Log("Called Laser Builder Control Setup");
            LaserBuilder = laserBuilder;
        }
        public void Fire()
        {
            //Debugger.Log("Called Laser Builder Control Fire");
            LaserBuilder.ActuallyShoot();
        }
    }
}