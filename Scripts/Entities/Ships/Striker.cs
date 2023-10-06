using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Striker : CarrierShip
    {
        public bool BombsReady, CompletedRun;
        public GameObject BombSprite, LoadedIndicator, CarriedBomb;
        //public Vector2 IndicatorOffset;
        private SpriteRenderer _indicatorSprite;
        //private GameObject _droppedBomb;
        public Ship ContactedShip, TouchingShip;
        public Weapon Bomb => Weapons.First();
        public Vector2 LastCarrierPosition;
        private void Start()
        {
            BombsReady = true;
            //LoadedIndicator = Instantiate(LoadedIndicator, Vector2.zero, Quaternion.identity);
            //LoadedIndicator.transform.parent = transform;
            //LoadedIndicator.transform.position = GetPosition();
            //LoadedIndicator.transform.localPosition = IndicatorOffset;
            _indicatorSprite = LoadedIndicator.GetComponent<SpriteRenderer>();
            InvokeRepeating(nameof(CheckCarrierReload), .25f, .25f);
        }
        protected override void OnTriggerEnter2D(Collider2D collider) // projectile collision
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Projectile"))
            {
                ProjectileCollision(collidingThing);
            }
            else if (collidingThing.name == ("Selection Box"))
            {
                //Debugger.Log("Striker hit selection box");
                if (IsUserControlled)
                {
                    Level.Selector.SelectShip(this);
                }
            }
            else if (collidingThing.CompareTag("Ship"))
            {
                TouchingShip = collidingThing.GetComponent<Ship>();
                //Debugger.Log($"Striker collided with a ship!" +
                //    $"{TouchingShip}, " +
                //    $"{Squad}, " +
                //    $"{TargetShips.First()}");

                if (TouchingShip != null && TouchingShip.Side != Side && Squad.HasCommand && HasTargetShips && TargetShips.Contains(TouchingShip) && BombsReady)
                {
                    //Debugger.Log("Collided with our target ship!");
                    ContactedShip = TouchingShip;
                    DropBombs();

                }
            }
        }
        protected override void OnTriggerExit2D(Collider2D collider)
        {
            GameObject collidingThing = collider.gameObject;
            if (TouchingShip != null  && collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                if (ship != null && ship.Equals(TouchingShip))
                {
                    TouchingShip = null;
                }
            }else if (collidingThing.name == ("Selection Box") && IsUserControlled)
            {
                Level.Selector.DeselectShip(this);
            }
        }
        public void TryToDropBombs()
        {
            //Debugger.Log($"Trying to drop bombs with {Name}");
            if (TouchingShip != null && TouchingShip.Side != Side && BombsReady)
            {
                ContactedShip = TouchingShip;
                DropBombs();
                return;
            }
            //Debugger.Log($"Failed trying to drop bombs with {Name}: TouchingShip: [{TouchingShip}], BombsReady: {BombsReady}");

        }
        private void CheckCarrierReload()
        {
            if (HasCarrier && DistanceTo(Carrier) < 8 && !BombsReady)
            {
                BombsReady = true;
                SetIndicatorColor();
            }
        }
        public void SetIndicatorColor()
        {
            if (BombsReady)
            {
                _indicatorSprite.color = ConfigData.GetUIColor("striker-loaded-indicator");
                CarriedBomb.SetActive(true);

            }
            else
            {
                _indicatorSprite.color = ConfigData.GetUIColor("striker-not-loaded-indicator");
                CarriedBomb.SetActive(false);

            }
        }
        
        private void DropBombs()
        {
            //Debugger.Log($"Striker #{Id} is dropping bombs");
            CarriedBomb.SetActive(false);

            // drop bomb animation
            Vector2 randomPoint = Utilities.RandomCoordinate(Level, Vector2.zero, new Vector2(ContactedShip.GetHalfWidth() - ConfigData.OffsetFromFront, ContactedShip.GetHalfHeight() - ConfigData.OffsetFromFront), Vector2.zero);

            //Debugger.Log($"Ship rotation: {rotation}, Half Width: {ContactedShip.GetHalfWidth()}, position: {targetPosition} " +
            //     $"Unrotated Location 1: {randomPoint} " +
            //    $"Rotated Location 1: {rotatedBombLocation} ");

            GameObject instance = Instantiate(BombSprite, Vector2.zero, Quaternion.identity);
            instance.transform.parent = ContactedShip.transform;
            instance.transform.localPosition = randomPoint;



            StrikerBomb bomb = (StrikerBomb)instance.GetComponent(typeof(StrikerBomb));
            bomb.Setup(Bomb.Power, this, ContactedShip);

            Invoke(nameof(CompleteRun), .5f);

        }
       

        private void CompleteRun()
        {
            CompletedRun = true;
            BombsReady = false;
            SetIndicatorColor();

        }
    }
}