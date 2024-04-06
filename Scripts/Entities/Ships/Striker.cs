using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships.Weapons;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Striker : CarrierShip
    {
        public bool AreBombsReady, HasDroppedBomb, HasCompletedRun, HasReturnedToCarrier;
        public GameObject BombSprite, LoadedIndicator, CarriedBomb;
        //public Vector2 IndicatorOffset;
        private SpriteRenderer _indicatorSprite;
        //private GameObject _droppedBomb;
        public Ship ContactedShip, TouchingShip;
        public Weapon Bomb => Weapons.First();
        public Vector2 LastCarrierPosition;


        private void Start()
        {
            _indicatorSprite = LoadedIndicator.GetComponent<SpriteRenderer>();
            SetBombsReadyStatus(true);
            InvokeRepeating(nameof(CheckCarrierReload), 1, 1);
        }
        protected override void OnTriggerEnter2D(Collider2D collider) // projectile collision
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.name == "Selection Box")
            {
                //Debug.Log("Striker hit selection box");
                if (IsUserControlled)
                {
                    Level.Selector.SelectShip(this);
                }
            }
            else if (collidingThing.CompareTag("Ship") && ShipCollider.IsTouching(collider))
            {
                TouchingShip = collidingThing.GetComponent<Ship>();
                //Debug.Log($"Striker collided with a ship!" +
                //    $"{TouchingShip}, " +
                //    $"{Squad}, " +
                //    $"{TargetShips.First()}");

                if (TouchingShip.Side != Side && Squad.HasCommand && HasTargetShips && TargetShips.Contains(TouchingShip) && AreBombsReady)
                {
                    //Debug.Log($"Collided with our target {TouchingShip.Name}, {collidingThing.name}!");
                    ContactedShip = TouchingShip;
                    DropBomb();

                }
            }
            
        }
        protected override void OnTriggerExit2D(Collider2D collider)
        {
            GameObject collidingThing = collider.gameObject;
            if (TouchingShip != null  && collidingThing.CompareTag("Ship") && ShipCollider.IsTouching(collider))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                if (ship.Equals(TouchingShip))
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
            //Debug.Log($"Trying to drop bombs with {Name}");
            if (TouchingShip != null && TouchingShip.Side != Side && AreBombsReady)
            {
                ContactedShip = TouchingShip;
                DropBomb();
                return;
            }
            //Debug.Log($"Failed trying to drop bombs with {Name}: TouchingShip: [{TouchingShip}], BombsReady: {BombsReady}");

        }
        private void CheckCarrierReload()
        {
            if (HasCarrier && DistanceTo(Carrier) < 15 && !AreBombsReady)
            {
                SetBombsReadyStatus(true);
                SetIndicatorColor();
            }
        }
        public void SetBombsReadyStatus(bool status)
        {
            if (AreBombsReady != status)
            {
                AreBombsReady = status;
                SetIndicatorColor();
            }
        }
        public void SetIndicatorColor()
        {
            if (AreBombsReady)
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
        private void DropBomb()
        {
            //Debug.Log($"Striker #{Id} is dropping bombs");
            CarriedBomb.SetActive(false);
            HasDroppedBomb = true;
            SetBombsReadyStatus(false);

            // drop bomb animation
            Vector2 bombPosition = ContactedShip.GetRandomPointOnShip(GetPosition());

            GameObject instance = Instantiate(BombSprite, Vector2.zero, Quaternion.identity);
            instance.transform.localPosition = bombPosition;
            instance.transform.parent = ContactedShip.transform;

            StrikerBomb bomb = (StrikerBomb)instance.GetComponent(typeof(StrikerBomb));
            bomb.Setup(Bomb.Power, this, ContactedShip);

            Invoke(nameof(CompleteRun), .5f);

        }
        private void CompleteRun()
        {
            HasCompletedRun = true;
            SetIndicatorColor();

        }
        public void ReturnToCarrierIfNecessary()
        {
            if (!HasReturnedToCarrier && (!AreBombsReady || HasCompletedRun))
            {
                // send any bomber that is't loaded to its carrier
                //Debug.Log($"Sending {striker.Id} back to its carrier");
                if (HasCarrier)
                {
                    Vector2 destination = Carrier.GetPosition() + OffsetFromCenter;
                    //Vector2 targetPoint = Level.ForceBounds(destination + OffsetFromCenter);
                    float distance = DistanceToPoint(destination);

                    if (distance < ConfigData.CloseEnoughCoordinateVariance * 3)
                    {
                        SetBombsReadyStatus(true);
                        if (HasCompletedRun)
                        {
                            HasReturnedToCarrier = true;
                            ClearTargets();

                        }
                    }
                    else
                    {
                        //Debug.Log($"{striker.Id} is still {distance} away from {targetPoint}");
                        MoveToPoint(destination);
                    }
                }
            }
        }
    }
}