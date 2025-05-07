using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
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
        public GameObject LoadedIndicator, CarriedBomb;
        //public Vector2 IndicatorOffset;
        private SpriteRenderer _indicatorSprite;
        //private GameObject _droppedBomb;
        public Ship ContactedShip, TouchingShip;
        public Bomb Bomb;
        public Vector2 LastCarrierPosition;

        public override void Create(Stage stage)
        {
            base.Create(stage);

            if (Stage.IsTraining)
            {
                Destroy(LoadedIndicator);
            }
            else
            {
                _indicatorSprite = LoadedIndicator.GetComponent<SpriteRenderer>();
                SetBombsReadyStatus(true);
            }
            Bomb = (Bomb)Weapons.First();
            IsBomber = true;

        }
        private ScaledTimer _checkCarrierReloadTimer = new ScaledTimer();
        public override void Setup(Level level, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter)
        {
            base.Setup(level, fleetShip, squad, offsetFromCenter);
            _checkCarrierReloadTimer.Reuse(1, CheckCarrierReload, true);
            Level.AddTimer(_checkCarrierReloadTimer);
            //InvokeRepeating(nameof(CheckCarrierReload), 1, 1);
        }
        public override void ClearData()
        {
            base.ClearData();
            IsBombReady = true;
            HasCompletedRun = false;
            HasDroppedBomb = false;
            HasReturnedToCarrier = false;
            TouchingShip = null;
            LastCarrierPosition = Vector2.zero;
        }
        public override void Deactivate()
        {
            base.Deactivate();
            if (!Stage.IsTraining)
            {
                LoadedIndicator.SetActive(false);
            }
        }
        public override void Activate()
        {
            base.Activate();
            if (!Stage.IsTraining)
            {
                LoadedIndicator.SetActive(true);
            }
        }
        private GameObject _collidingThing;
        private Ship _collidingShip;
        public override bool IsCloseEnoughToTargetCoordinates(float distance)
        {
            return distance < ConfigData.ShipTurningRadius && !(Squad.HasOnlyBombers && !IsFollowingPath && HasTargetEnemyShipToFollow && Squad.HasCommand && Squad.GetCommand().CommandType == ConfigData.CommandTypes.BombingRun
                && ProximityCollider.NearbyEnemyShips.Contains(TargetEnemyShipToFollow));
        }
        protected override void OnTriggerEnter2D(Collider2D collider) // projectile collision
        {
            _collidingThing = collider.gameObject;
            
            if (_collidingThing.CompareTag("Ship") && Collider.IsTouching(collider))
            {
                TouchingShip = _collidingThing.GetComponent<Ship>();
                //Debug.Log($"Striker collided with a ship!" +
                //    $"{TouchingShip}, " +
                //    $"{Squad}, " +
                //    $"{TargetShips.First()}");

                if (TouchingShip.Side != Side && Squad.HasCommand && Bomb.TargetShip == TouchingShip && IsBombReady)
                {
                    //Debug.Log($"Collided with our target {TouchingShip.Name}!");
                    ContactedShip = TouchingShip;
                    DropBomb();

                }
            }
            else if (IsUserControlled && _collidingThing.name == "Selection Box")
            {
                //Debug.Log("Striker hit selection box");
                Stage.Selector.SelectShip(this);
            }

        }
        protected override void OnTriggerExit2D(Collider2D collider)
        {
            _collidingThing = collider.gameObject;
            if (TouchingShip != null  && _collidingThing.CompareTag("Ship") && Collider.IsTouching(collider))
            {
                _collidingShip = _collidingThing.GetComponent<Ship>();
                if (_collidingShip == TouchingShip)
                {
                    TouchingShip = null;
                }
            }
            else if (IsUserControlled && _collidingThing.name == "Selection Box")
            {
                Stage.Selector.DeselectShip(this);
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
            if (!Carrier.IsDead && DistanceTo(Carrier) < 15 && !IsBombReady)
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
            if (!Stage.IsTraining)
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

        }
        private StrikerBomb _bomb;
        private ScaledTimer _damageTimer = new ScaledTimer();
        private void DropBomb()
        {
            if (!HasDroppedBomb)
            {
                //Debug.Log($"Striker #{Id} is dropping bombs");
                HasDroppedBomb = true;
                SetBombsReadyStatus(false);

                if (!Level.Stage.IsTraining)
                {
                    _bomb = (StrikerBomb)Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.StrikerBomb);
                    _bomb.transform.parent = Level.Map.Transform;
                    _bomb.Setup(Level, Bomb, this, ContactedShip, ContactedShip.GetRandomPointOnShip(GetPosition()), 0, 0, Bomb.Power, ContactedShip);
                }
                else
                {
                    if (Level._currentTimerIDs.Contains(_damageTimer.Id)) // [debug]
                    {
                        Debug.LogError($"Tried to add {_damageTimer} but it already exists in Timers.");
                    }
                    else
                    {
                        Debug.Log($"Adding {_damageTimer} to Timers.");
                    }
                    _damageTimer.Reuse(2, LogBombDamage);
                    Level.AddTimer(_damageTimer);
                }

                CompleteRun();
            }
        }
        public void LogBombDamage()
        {
            LogAttackingDamage(Bomb.Power, this, FleetShip, Squad.SavedSquad, ContactedShip);
        }
        public void CompleteRun()
        {
            HasCompletedRun = true;
            TargetEnemyShipToFollow = null;
            SetIndicatorColor();

        }
        private Vector2 _destination;
        public void ReturnToCarrierIfNecessary()
        {
            // If you haven't returned to the carrier and you've either dropped your bombs or don't have them
            if (!HasReturnedToCarrier && (!IsBombReady || HasCompletedRun))
            {
                // send any bomber that is't loaded to its carrier
                //Debug.Log($"Sending {striker.Id} back to its carrier");
                if (!Carrier.IsDead)
                {
                    _destination = Carrier.GetPosition() + OffsetFromCenter;
                    //Vector2 targetPoint = Level.ForceBounds(destination + OffsetFromCenter);

                    if (DistanceToPoint(_destination) < ConfigData.RefillDistanceToCarrier || DistanceTo(Carrier) < ConfigData.RefillDistanceToCarrier)
                    {
                        //Debug.Log($"{Name} has returned to carrier and is moving towards {destination}");
                        SetBombsReadyStatus(true);
                        if (HasCompletedRun)
                        {
                            HasReturnedToCarrier = true;
                            ((BombingRun)Squad.GetCommand()).ShipsCompletedCommand.Add(this);
                        }
                        else if (HasTargetEnemyShipToFollow)
                        {
                            ((BombingRun)Squad.GetCommand()).SendShipToTarget(this);
                        }
                    }
                    else
                    {
                        //Debug.Log($"{striker.Id} is still {distance} away from {targetPoint}");
                        MoveToPoint(_destination);
                    }
                }
            }
        }

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            Debug.Log($"Striker {Name} killed, canceling {_damageTimer}");
            Level.CancelTimer(_damageTimer);
            Level.CancelTimer(_checkCarrierReloadTimer);
            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }
    }
}