using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class ShipExplosionAnimation : MonoBehaviour
    {
        public AudioSource SoundEffect;
        public Ship Ship;

        //public Ship Ship;
        public void Create(Ship ship)
        {
            Ship = ship;
            gameObject.SetActive(false);
            SoundEffect = Ship.ShipExplosionSoundEffect;
        }
        //public void PlaceRemains()
        //{
        //    if (Ship.HasRemainsShip)
        //    {
        //        //Debug.Log($"Dropping remains for {Name}");
        //        Ship.ShipRemains.Place();
        //    }
        //}
        public void Play()
        {
            gameObject.SetActive(true);
            if (Ship.Stage.ActivateAudio && Ship.HasShipExplosionSoundEffect)
            {
                SoundEffect.Play();
            }
        }
        public virtual void Kill()
        {
            gameObject.SetActive(false);
        }
    }
}