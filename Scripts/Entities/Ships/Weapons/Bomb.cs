
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public class Bomb : Weapon
    {
        private ShipDamageStatus _reservedDamageStatus;
        private readonly List<Ship> _targetBuffer = new List<Ship>();

        public override void ClearData()
        {
            ReleaseTargetReservation();
            _targetBuffer.Clear();
            base.ClearData();
        }

        protected override List<Ship> GetPotentialEnemyTargetShips(bool disregardRange)
        {
            if (!HasCachedChanged && CachedShootingStrategy == Ship.ShootingStrategy)
            {
                IsUsingCachedTargetingQueue = true;
                return CachedTargetingQueue;
            }

            IsUsingCachedTargetingQueue = false;
            _targetBuffer.Clear();
            _targetBuffer.AddRange(Ship.Squad.GetCommand().EnemySquad.GetShips());
            return _targetBuffer;
        }

        /// <summary>
        /// All living ships are valid Bomb targets. BombingRun owns the pursuit range.
        /// </summary>
        public override bool IsShipValidTarget(Ship potentialTargetShip)
        {
            return !potentialTargetShip.IsDead;
        }

        protected override void SetTargetShip(Ship targetShip)
        {
            ReleaseTargetReservation();
            _reservedDamageStatus = Level.State.GetShipDamageStatus(Side, targetShip);
            _reservedDamageStatus.TotalDamageSentToShip += Power;
            TargetShip = targetShip;
        }

        /// <summary>
        /// A launched projectile now owns removal of the reservation. Clear Bomb ownership
        /// without changing the shared damage total.
        /// </summary>
        public void TransferTargetReservation()
        {
            _reservedDamageStatus = null;
            TargetShip = null;
        }

        /// <summary>
        /// Releases target damage reserved during BombingRun assignment when delivery did not
        /// transfer to a projectile (retarget, command cancellation, or bomber death).
        /// </summary>
        public void ReleaseTargetReservation()
        {
            if (_reservedDamageStatus != null)
            {
                _reservedDamageStatus.TotalDamageSentToShip = Mathf.Max(
                    0,
                    _reservedDamageStatus.TotalDamageSentToShip - Power);
                _reservedDamageStatus = null;
            }
            TargetShip = null;
        }

        /// <summary>
        /// Used when the sorted list cannot reserve a target through normal damage-aware selection.
        /// </summary>
        public void SetRandomTarget(List<Ship> ships)
        {
            SetTargetShip(ships[Utilities.RandomInt(ships.Count)]);
        }
    }
}
