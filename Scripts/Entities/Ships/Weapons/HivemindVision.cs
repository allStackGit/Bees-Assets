using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    // A collider for identifying ships to the Hive Mind
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
            Collider.radius = range;
        }

        public void Activate()
        {
            Collider.enabled = true;
            enabled = true;
        }

        private Ship _shipEnter;

        protected void OnTriggerEnter2D(Collider2D collider)
        {
            _shipEnter = collider.GetComponent<Ship>();
            RecordSighting();
        }

        private void RecordSighting()
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
            Ship.Stage.DebugLogger?.RecordHiveMindSightEnter(isFirstSideWideSighting);
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
