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
        public bool AreBombsReady, HasDroppedBombs, HasCompletedRun, HasReturnedToCarrier;
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
            if (collidingThing.name == ("Selection Box"))
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

                if (TouchingShip != null && TouchingShip.Side != Side && Squad.HasCommand && HasTargetShips && TargetShips.Contains(TouchingShip) && AreBombsReady)
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
            if (TouchingShip != null && TouchingShip.Side != Side && AreBombsReady)
            {
                ContactedShip = TouchingShip;
                DropBombs();
                return;
            }
            //Debugger.Log($"Failed trying to drop bombs with {Name}: TouchingShip: [{TouchingShip}], BombsReady: {BombsReady}");

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
        private void DropBombs()
        {
            //Debugger.Log($"Striker #{Id} is dropping bombs");
            CarriedBomb.SetActive(false);
            HasDroppedBombs = true;
            SetBombsReadyStatus(false);

            // drop bomb animation
            Vector2 bombPosition = ContactedShip.GetRandomPointOnShip();

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
                //Debugger.Log($"Sending {striker.Id} back to its carrier");
                if (HasCarrier)
                {
                    Vector2 destination = Carrier.GetPosition();
                    Vector2 targetPoint = Level.ForceBounds(destination + OffsetFromCenter);
                    float distance = DistanceToPoint(targetPoint);

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
                        //Debugger.Log($"{striker.Id} is still {distance} away from {targetPoint}");
                        MoveToPoint(targetPoint);
                    }
                }
            }
        }
    }
}