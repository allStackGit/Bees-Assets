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
        public bool IsBombReady;
        public bool HasCompletedRun;
        public bool HasDroppedBomb;
        public bool HasReturnedToCarrier;
        public GameObject LoadedIndicator, CarriedBomb;
        private SpriteRenderer _indicatorSprite;
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
            }
            Bomb = (Bomb)Weapons.First();
            IsBomber = true;
        }

        private ScaledTimer _checkCarrierReloadTimer = new ScaledTimer();
        public override void Setup(Level level, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter)
        {
            base.Setup(level, fleetShip, squad, offsetFromCenter);
            SetIndicatorColor();
            _checkCarrierReloadTimer.Reuse(1, CheckCarrierReload, true);
            Level.AddTimer(_checkCarrierReloadTimer);
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
            _trainingBombTargetRuntimeId = 0;
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

        protected override void OnTriggerEnter2D(Collider2D collider)
        {
            _collidingThing = collider.gameObject;
            if (_collidingThing.CompareTag("Ship") && Collider.IsTouching(collider))
            {
                TouchingShip = _collidingThing.GetComponent<Ship>();
                if (TouchingShip.Side != Side && Squad.HasCommand && Bomb.TargetShip == TouchingShip && IsBombReady)
                {
                    ContactedShip = TouchingShip;
                    DropBomb();
                }
            }
            else if (IsUserControlled && _collidingThing.name == "Selection Box")
            {
                Stage.Selector.SelectShip(this);
            }
        }

        protected override void OnTriggerExit2D(Collider2D collider)
        {
            _collidingThing = collider.gameObject;
            if (TouchingShip != null && _collidingThing.CompareTag("Ship"))
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
            if (TouchingShip != null && TouchingShip.Side != Side && IsBombReady)
            {
                ContactedShip = TouchingShip;
                DropBomb();
            }
        }

        private void CheckCarrierReload()
        {
            if (Carrier != null && !Carrier.IsDead && DistanceTo(Carrier) < 15 && !IsBombReady)
            {
                SetBombsReadyStatus(true);
            }
        }

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
        private long _trainingBombTargetRuntimeId;
        private void DropBomb()
        {
            if (!HasDroppedBomb)
            {
                HasDroppedBomb = true;
                SetBombsReadyStatus(false);

                if (!Level.Stage.IsTraining)
                {
                    _bomb = (StrikerBomb)Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.StrikerBomb);
                    _bomb.transform.parent = Level.Map.Transform;
                    _bomb.Setup(Level, Bomb, this, ContactedShip, ContactedShip.GetRandomPointOnShip(GetPosition()), 0, 0, Bomb.Power, ContactedShip);
                    Bomb.TransferTargetReservation();
                }
                else
                {
                    _trainingBombTargetRuntimeId = ContactedShip != null ? ContactedShip.Id : 0;
                    _damageTimer.Reuse(2, LogBombDamage);
                    Level.AddTimer(_damageTimer);
                }

                CompleteRun();
            }
        }

        public void LogBombDamage()
        {
            if (ContactedShip == null || ContactedShip.IsDead || ContactedShip.Id != _trainingBombTargetRuntimeId)
            {
                Bomb.ReleaseTargetReservation();
                return;
            }
            Bomb.ReleaseTargetReservation();
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
            if (!HasReturnedToCarrier && (!IsBombReady || HasCompletedRun))
            {
                if (Carrier != null && !Carrier.IsDead)
                {
                    _destination = Carrier.GetPosition() + OffsetFromCenter;

                    if (DistanceToPoint(_destination) < ConfigData.RefillDistanceToCarrier || DistanceTo(Carrier) < ConfigData.RefillDistanceToCarrier)
                    {
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
                        MoveToPoint(_destination);
                    }
                }
            }
        }

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            Bomb.ReleaseTargetReservation();
            Level.CancelTimer(_damageTimer);
            Level.CancelTimer(_checkCarrierReloadTimer);
            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }
    }
}
