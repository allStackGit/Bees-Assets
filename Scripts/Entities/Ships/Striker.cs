using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
using System.Collections.Generic;
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
            Bomb = (Bomb)Weapons[0];
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
                // Scripted BombingRun resets this per run. The direct RL controller deliberately
                // does not create BombingRun commands, so a Striker that has physically returned to
                // its Carrier and reloaded must be allowed to make another primitive bomb attempt.
                if (Stage.IsTrainingNueralNetwork)
                {
                    HasDroppedBomb = false;
                }
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
                bool replenished = status;
                IsBombReady = status;
                if (replenished)
                {
                    global::RlOneVsOneEpisodeDiagnostics.RecordStrikerReplenished(this);
                }
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
        private void DropBomb()
        {
            if (!HasDroppedBomb)
            {
                HasDroppedBomb = true;
                SetBombsReadyStatus(false);
                global::RlOneVsOneEpisodeDiagnostics.RecordSpecialAction(this, "striker_bomb_drop");

                if (!Level.Stage.IsTraining)
                {
                    _bomb = (StrikerBomb)Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.StrikerBomb);
                    _bomb.transform.parent = Level.Map.Transform;
                    _bomb.Setup(Level, Bomb, this, ContactedShip, ContactedShip.GetRandomPointOnShip(GetPosition()), 0, 0, Bomb.Power, ContactedShip);
                    Bomb.TransferTargetReservation();
                }
                else
                {
                    ScheduleTrainingBombDamage(ContactedShip);
                }

                CompleteRun();
            }
        }

        private void ScheduleTrainingBombDamage(Ship target)
        {
            if (target == null)
            {
                Bomb.ReleaseTargetReservation();
                return;
            }

            Ship shooter = this;
            FleetShip shooterFleetShip = FleetShip;
            SavedSquad shooterSavedSquad = Squad.SavedSquad;
            long shooterFleetShipId = shooterFleetShip != null ? shooterFleetShip.Id : 0;
            long targetRuntimeId = target.Id;
            int power = Bomb.Power;
            ShipDamageStatus damageReservation = Bomb.TransferTargetReservation();

            // The fuse is owned by the Level, not by the pooled Striker wrapper. A bomb that has
            // already been dropped therefore survives shooter death exactly like the rendered
            // StrikerBomb projectile. Captured lifecycle IDs prevent pooled wrappers from stealing
            // the delayed damage or receiving credit for another lifecycle's bomb.
            ScaledTimer damageTimer = new ScaledTimer(
                2f,
                () => ResolveTrainingBombDamage(
                    shooter,
                    shooterFleetShip,
                    shooterSavedSquad,
                    shooterFleetShipId,
                    target,
                    targetRuntimeId,
                    power,
                    damageReservation));
            Level.AddTimer(damageTimer);
        }

        private static void ResolveTrainingBombDamage(
            Ship shooter,
            FleetShip shooterFleetShip,
            SavedSquad shooterSavedSquad,
            long shooterFleetShipId,
            Ship target,
            long targetRuntimeId,
            int power,
            ShipDamageStatus damageReservation)
        {
            ReleaseTrainingBombReservation(damageReservation, power);
            if (target == null || target.IsDead || target.Id != targetRuntimeId)
            {
                return;
            }

            if (shooter != null && shooter.FleetShip != null && shooter.FleetShip.Id == shooterFleetShipId)
            {
                LogAttackingDamage(
                    power,
                    shooter,
                    shooterFleetShip,
                    shooterSavedSquad,
                    target,
                    rlDamageSource: "bomb");
            }
            else
            {
                // The original Striker wrapper has been recycled. Preserve the bomb's physical
                // damage without attributing it to the new occupant of that pooled object.
                target.LogDamage(power, "bomb");
            }
        }

        private static void ReleaseTrainingBombReservation(ShipDamageStatus damageReservation, int power)
        {
            if (damageReservation == null)
            {
                return;
            }

            damageReservation.TotalDamageSentToShip = Mathf.Max(
                0,
                damageReservation.TotalDamageSentToShip - power);
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
                        MoveToTrackedPoint(_destination);
                    }
                }
            }
        }

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            // If delivery has not happened yet, Bomb still owns the target reservation and death
            // releases it. A dropped training bomb transfers reservation/fuse ownership to the
            // Level timer, so this becomes a no-op and the in-flight bomb survives shooter death.
            Bomb.ReleaseTargetReservation();
            Level.CancelTimer(_checkCarrierReloadTimer);
            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }
    }
}
