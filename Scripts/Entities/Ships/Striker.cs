using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Level.Commands;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Striker : CarrierShip
    {
        /// <summary>
        /// If the striker's bomb is loaded and ready, if not, this means it has dropped it and must return to the carrier before pursuing another target
        /// </summary>
        public bool IsBombReady;
        /// <summary>
        /// Has the striker either dropped its bomb on its target it, or doesn't have a bomb and is going back to the carrier, or the whole target squad is dead
        /// </summary>
        public bool HasCompletedRun;
        /// <summary>
        /// Has the striker dropped its bomb on its target
        /// </summary>
        public bool HasDroppedBomb;
        public bool HasReturnedToCarrier;
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
            else if (collidingThing.CompareTag("Ship") && Collider.IsTouching(collider))
            {
                TouchingShip = collidingThing.GetComponent<Ship>();
                //Debug.Log($"Striker collided with a ship!" +
                //    $"{TouchingShip}, " +
                //    $"{Squad}, " +
                //    $"{TargetShips.First()}");

                if (TouchingShip.Side != Side && Squad.HasCommand && HasWeaponsTargetShips && WeaponsTargetShips.Contains(TouchingShip) && IsBombReady)
                {
                    //Debug.Log($"Collided with our target {TouchingShip.Name}!");
                    ContactedShip = TouchingShip;
                    DropBomb();

                }
            }
            
        }
        protected override void OnTriggerExit2D(Collider2D collider)
        {
            GameObject collidingThing = collider.gameObject;
            if (TouchingShip != null  && collidingThing.CompareTag("Ship") && Collider.IsTouching(collider))
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
            if (TouchingShip != null && TouchingShip.Side != Side && IsBombReady)
            {
                ContactedShip = TouchingShip;
                DropBomb();
                return;
            }
            //Debug.Log($"Failed trying to drop bombs with {Name}: TouchingShip: [{TouchingShip}], BombsReady: {BombsReady}");

        }
        private void CheckCarrierReload()
        {
            if (HasCarrier && DistanceTo(Carrier) < 15 && !IsBombReady)
            {
                SetBombsReadyStatus(true);
                SetIndicatorColor();
            }
        }
        /// <summary>
        /// Sets the status of the bomb (loaded or not) and sets the indicator accordingly
        /// </summary>
        /// <param name="status"></param>
        public void SetBombsReadyStatus(bool status)
        {
            if (IsBombReady != status)
            {
                IsBombReady = status;
                SetIndicatorColor();
            }
        }
        public void SetIndicatorColor()
        { 
            if (IsBombReady)
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
            //if (ContactedShip.ShipType == "Beehive")
            //{
            //    Vector2 targetPoint = GetPosition();
            //    Vector2 frontOfShip = targetPoint + new Vector2(0, GetHalfHeight() + 2);
            //    bombPosition = ContactedShip.GetRandomPointOnShip(Utilities.RotatePointAroundPoint(targetPoint, frontOfShip, GetRotation() * Mathf.Deg2Rad));
            //}

            GameObject instance = Instantiate(BombSprite, Vector2.zero, Quaternion.identity);
            instance.transform.localPosition = bombPosition;
            instance.transform.parent = ContactedShip.transform;

            StrikerBomb bomb = (StrikerBomb)instance.GetComponent(typeof(StrikerBomb));
            bomb.Setup(Bomb.Power, this, ContactedShip);

            CompleteRun();

        }
        public void CompleteRun()
        {
            HasCompletedRun = true;
            TargetEnemyShipToFollow = null;
            SetIndicatorColor();

        }
        public void ReturnToCarrierIfNecessary()
        {
            // If you haven't returned to the carrier and you've either dropped your bombs or don't have them
            if (!HasReturnedToCarrier && (!IsBombReady || HasCompletedRun))
            {
                // send any bomber that is't loaded to its carrier
                //Debug.Log($"Sending {striker.Id} back to its carrier");
                if (HasCarrier)
                {
                    Vector2 destination = Carrier.GetPosition() + OffsetFromCenter;
                    //Vector2 targetPoint = Level.ForceBounds(destination + OffsetFromCenter);
                    float distance = DistanceToPoint(destination);

                    if (distance < ConfigData.RefillDistanceToCarrier || DistanceTo(Carrier) < ConfigData.RefillDistanceToCarrier)
                    {
                        //Debug.Log($"{Name} has returned to carrier and is moving towards {destination}");
                        SetBombsReadyStatus(true);
                        if (HasCompletedRun)
                        {
                            HasReturnedToCarrier = true;
                            ((BombingRun)Squad.Command).ShipsCompletedCommand.Add(this);
                        }
                        else if (HasTargetEnemyShipToFollow)
                        {
                            ((BombingRun)Squad.Command).SendShipToTarget(this);
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