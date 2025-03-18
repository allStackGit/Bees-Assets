using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class ShipExplosionAnimation : MonoBehaviour
    {
        //public Ship Ship;
        //public void Create(Ship ship)
        //{
        //    Ship = ship;
        //    gameObject.SetActive(false);
        //}
        //public void PlaceRemains()
        //{
        //    if (Ship.HasRemainsShip)
        //    {
        //        //Debug.Log($"Dropping remains for {Name}");
        //        Ship.ShipRemains.Place();
        //    }
        //}
        public virtual void Kill()
        {
            gameObject.SetActive(false);
        }
    }
}