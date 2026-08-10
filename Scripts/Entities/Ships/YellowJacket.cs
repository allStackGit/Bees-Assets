using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Levels;
using System.Linq;
using Assets.Scripts.Entities.Ships.Weapons;
using UnityEngine;
using Assets.Scripts.Entities.Projectiles;
using Unity.Mathematics;

namespace Assets.Scripts.Entities.Ships
{
    public class YellowJacket : Ship
    {

        public bool HasCompletedRun;
        public Ship ContactedShip, TouchingShip;

        public Bomb Bomb;
        public override void Create(Stage stage)
        {
            base.Create(stage);
            Bomb = (Bomb) Weapons.First();
            Destroy(Bomb.Piece);
            IsBomber = true;
        }
        public override void ClearData()
        {
            base.ClearData();
            ContactedShip = null;
            TouchingShip = null;
            HasCompletedRun = false;
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
            if (_collidingThing.name == ("Selection Box"))
            {
                if (IsUserControlled)
                {
                    Stage.Selector.SelectShip(this);
                }
            }
            else if (_collidingThing.CompareTag("Ship") && Collider.IsTouching(collider))
            {
                TouchingShip = _collidingThing.GetComponent<Ship>();

                if (TouchingShip.Side != Side && Squad.HasCommand && Bomb.TargetShip == TouchingShip)
                {
                    ContactedShip = TouchingShip;
                    Detonate();

                }
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
            else if (_collidingThing.name == ("Selection Box") && IsUserControlled)
            {
                Stage.Selector.DeselectShip(this);
            }
        }
        public void TryToDetonate()
        {
            if (TouchingShip != null && TouchingShip.Side != Side)
            {
                ContactedShip = TouchingShip;
                Detonate();
                return;
            }

        }
        private void Detonate()
        {
            HasCompletedRun = true;

            // The selected bombing-run target is being resolved synchronously rather than
            // by a projectile, so release the inbound-damage reservation before applying it.
            Bomb.ReleaseTargetReservation();

            LogDetonationDamage(Bomb.Power, this, ContactedShip);
            LogDetonationDamage(Bomb.Power, ContactedShip, this);

            if (ContactedShip.Health <= 0)
            {
                ContactedShip.Kill(this, FleetShip, Squad.SavedSquad);
            }

            Kill(ContactedShip, ContactedShip.FleetShip, ContactedShip.Squad.SavedSquad);

        }

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            Bomb.ReleaseTargetReservation();
            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }

        private int _targetOldTSV, _targetTSVLoss;
        private void LogDetonationDamage(int power, Ship attacker, Ship target) // [damage-method] [note]
        {
            _targetOldTSV = target.Tsv;
            target.Health -= math.min(target.Health, power);
            target.Tsv = Utilities.CalculateTsv(target);


            _targetTSVLoss = target.Tsv - _targetOldTSV;
            LogHitStats(attacker, attacker.FleetShip, attacker.Squad.SavedSquad, target, target.Squad, -_targetTSVLoss);

            if (attacker.Squad.HasCommand)
            {
                attacker.Squad.GetCommand().Tsv += -_targetTSVLoss;
            }
            if (target.Squad.HasCommand)
            {
                target.Squad.GetCommand().Tsv += _targetTSVLoss;
            }
            target.UpdateHealthBar();


        }
    }
}
