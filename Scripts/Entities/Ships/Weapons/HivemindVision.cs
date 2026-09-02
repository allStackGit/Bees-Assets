using Assets.Scripts.Entities;
using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    // A collider for identifying ships and strategic objects to the Hive Mind.
    public class HiveMindVision : MonoBehaviour
    {
        public CircleCollider2D Collider;
        public Ship Ship;
        public int Range;

        public void Create(Ship ship)
        {
            Ship = ship;
            int range = Ship.Sight;
            if (range == 0)
            {
                range = Ship.MaxRange;
            }
            Range = range;
            Collider.radius = range;
        }

        public void Activate()
        {
            Collider.enabled = true;
            enabled = true;
        }

        /// <summary>
        /// Shared geometric visibility test used by RL perception. The target may be a ship,
        /// obstacle, asteroid, wall or MapObject. Collider geometry is preferred so very large
        /// objects become visible when their edge enters sight rather than only when their center does.
        /// </summary>
        public bool CanSee(Collider2D targetCollider, Vector2 fallbackLevelPosition)
        {
            if (Ship == null || Ship.IsDead || Ship.Level == null || !enabled ||
                Collider == null || !Collider.enabled || Range <= 0)
            {
                return false;
            }

            Vector2 observerPosition = Ship.GetPosition();
            Vector2 targetPosition = fallbackLevelPosition;
            if (targetCollider != null && targetCollider.enabled)
            {
                Vector2 observerWorldPosition = PathfinderObstacleScope.LevelToWorld(Ship.Level, observerPosition);
                Vector2 closestWorldPoint = targetCollider.ClosestPoint(observerWorldPosition);
                targetPosition = PathfinderObstacleScope.WorldToLevel(Ship.Level, closestWorldPoint);
            }

            return (targetPosition - observerPosition).sqrMagnitude <= Range * Range;
        }

        private Ship _shipEnter;
        private MiningAsteroid _miningAsteroidEnter;

        protected void OnTriggerEnter2D(Collider2D collider)
        {
            _shipEnter = collider.GetComponentInParent<Ship>();
            if (_shipEnter != null)
            {
                RecordShipSighting();
                return;
            }

            _miningAsteroidEnter = collider.GetComponentInParent<MiningAsteroid>();
            if (_miningAsteroidEnter != null && Ship != null && Ship.IsHiveMindControlled && !Ship.IsDead &&
                Ship.Level != null && !_miningAsteroidEnter.IsDead)
            {
                Ship.Level.State.RecordHiveMindMiningAsteroidSighting(Ship, _miningAsteroidEnter);
            }
        }

        private void RecordShipSighting()
        {
            if (!Ship.IsHiveMindControlled || Ship.IsDead || _shipEnter == null || _shipEnter.IsDead || _shipEnter.Side == Ship.Side)
            {
                return;
            }

            GameState state = Ship.Level.State;

            // Record the observer relationship and first-side-wide sighting in one incremental
            // operation. The previous implementation rebuilt the entire side visibility set for
            // every pairwise trigger enter, which caused first contact between dense squads to
            // multiply main-thread work dramatically.
            bool isFirstSideWideSighting = state.RecordHiveMindSighting(Ship, _shipEnter);
            FreezeDiagnostics.RecordHiveMindSightEnter(Ship.Level, isFirstSideWideSighting);
            if (!isFirstSideWideSighting)
            {
                return;
            }

            // Scout-spawned Beacons credit vision to their parent Scout command. Ordinary
            // Beacons are independent squad members and must credit their own live squad.
            Squad rewardSquad = Ship.ShipType == ConfigData.ShipTypes.Beacon && Ship.IsMinionShip
                ? Ship.MotherSquad
                : Ship.Squad;
            if (rewardSquad == null || rewardSquad.IsDead || !rewardSquad.HasCommand || rewardSquad.GetCommand() == null)
            {
                return;
            }

            // Only the first side-wide sighting earns command TSV. Recording visibility itself
            // above does not depend on there being an active command.
            rewardSquad.GetCommand().Tsv += (int)Mathf.Clamp(
                _shipEnter.Tsv * ConfigData.TsvMultiplierForVision,
                ConfigData.MinimumTsvValueForSeeingAShip,
                ConfigData.MaximumTsvValueForSeeingAShip);

            if (rewardSquad.GetCommand().CommandType == ConfigData.CommandTypes.Scouting)
            {
                ((Scouting)rewardSquad.GetCommand()).FoundNewShips();
            }
        }

        public void Deactivate()
        {
            Collider.enabled = false;
            enabled = false;
        }
    }
}
