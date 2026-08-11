using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Squad
    {
        private const int FormationCompressionSteps = 20;
        private Vector2 _center;

        public void SetOffsets()
        {
            RefreshCompositionCommandBans();
            _center = GetCenterPoint();
            _tempShips = GetShips();
            foreach (Ship ship in _tempShips)
            {
                ship.OffsetFromCenter = new Vector2(ship.GetX() - _center.x, ship.GetY() - _center.y);
            }
        }

        private void RefreshCompositionCommandBans()
        {
            if (GetShips().Count == 0)
            {
                return;
            }

            if (IsDefenseless)
            {
                BannedStrats.Add(ConfigData.CommandTypes.Aggressive);
                BannedStrats.Add(ConfigData.CommandTypes.CircleSquad);
                BannedStrats.Add(ConfigData.CommandTypes.RightSwipe);
                BannedStrats.Add(ConfigData.CommandTypes.LeftSwipe);
                BannedStrats.Add(ConfigData.CommandTypes.InAndOut);
                BannedStrats.Add(ConfigData.CommandTypes.Hold);
            }
            else if (HasOnlyBombers)
            {
                BannedStrats.Remove(ConfigData.CommandTypes.Aggressive);
                BannedStrats.Add(ConfigData.CommandTypes.CircleSquad);
                BannedStrats.Add(ConfigData.CommandTypes.RightSwipe);
                BannedStrats.Add(ConfigData.CommandTypes.LeftSwipe);
                BannedStrats.Add(ConfigData.CommandTypes.InAndOut);
                BannedStrats.Add(ConfigData.CommandTypes.Hold);
            }
            else if (HasOnlyBarges)
            {
                // The response handler maps Circle/swipes/InAndOut to Charge for barge-only
                // squads. Keep Aggressive as the canonical Charge policy identity so Hive Mind
                // training does not spend samples distinguishing identical executed actions.
                BannedStrats.Remove(ConfigData.CommandTypes.Aggressive);
                BannedStrats.Add(ConfigData.CommandTypes.CircleSquad);
                BannedStrats.Add(ConfigData.CommandTypes.RightSwipe);
                BannedStrats.Add(ConfigData.CommandTypes.LeftSwipe);
                BannedStrats.Add(ConfigData.CommandTypes.InAndOut);
                BannedStrats.Remove(ConfigData.CommandTypes.Hold);
            }
            else
            {
                BannedStrats.Remove(ConfigData.CommandTypes.Aggressive);
                BannedStrats.Remove(ConfigData.CommandTypes.CircleSquad);
                BannedStrats.Remove(ConfigData.CommandTypes.RightSwipe);
                BannedStrats.Remove(ConfigData.CommandTypes.LeftSwipe);
                BannedStrats.Remove(ConfigData.CommandTypes.InAndOut);
                BannedStrats.Remove(ConfigData.CommandTypes.Hold);
            }
        }

        public void StopMoving()
        {
            _tempShips = GetShips();
            foreach (Ship ship in _tempShips)
            {
                if (ship.IsMobile)
                {
                    ship.StopMoving("Squad ordered to stop");
                }
            }
        }

        public void Move(Vector2 destination)
        {
            if (IsSelected && Level.Stage.Menus.HasSquadActionBox)
            {
                Level.Stage.Menus.ActionBox.HighlightSelectedButtons();
            }

            _tempShips = GetShips().Where(ship => ship.IsMobile).ToList();
            Vector2 formationCenter = Level.ForceBounds(destination);
            float formationCompression = 1f;

            if (Level.HasObstacles && _tempShips.Count > 0 && !TryGetFormationCompression(formationCenter, _tempShips, out formationCompression))
            {
                int largestClearance = _tempShips.Max(ship => ship.GetClearance());
                if (!Level.Pathfinder.TryFindNearestValidDestination(formationCenter, largestClearance, out formationCenter) ||
                    !TryGetFormationCompression(formationCenter, _tempShips, out formationCompression))
                {
                    return;
                }
            }

            foreach (Ship ship in _tempShips)
            {
                Vector2 shipDestination = Level.ForceBounds(formationCenter + (ship.OffsetFromCenter * formationCompression));
                ship.MoveToPoint(shipDestination);
            }
            Destination = formationCenter;
        }

        private bool TryGetFormationCompression(Vector2 formationCenter, List<Ship> ships, out float compression)
        {
            for (int step = 0; step <= FormationCompressionSteps; step++)
            {
                float candidateCompression = 1f - ((float)step / FormationCompressionSteps);
                bool allDestinationsValid = true;
                for (int shipIndex = 0; shipIndex < ships.Count; shipIndex++)
                {
                    Ship ship = ships[shipIndex];
                    Vector2 candidate = Level.ForceBounds(formationCenter + (ship.OffsetFromCenter * candidateCompression));
                    if (!Level.Pathfinder.CanOccupyDestination(candidate, ship.GetClearance()))
                    {
                        allDestinationsValid = false;
                        break;
                    }
                }

                if (allDestinationsValid)
                {
                    compression = candidateCompression;
                    return true;
                }
            }

            compression = 0f;
            return false;
        }

        public void MatchSpeed(float speed = 0)
        {
            IsMatchingSpeed = true;
            SetSquadSpeed(speed > 0 ? speed : SlowestSpeed);
        }

        public void UnmatchSpeed()
        {
            _tempShips = GetShips();
            foreach (Ship ship in _tempShips)
            {
                ship.SetCurrentSpeed(ship.Speed);
            }
            IsMatchingSpeed = false;
        }

        public void SetSquadSpeed(float speed)
        {
            CurrentSpeed = speed;
            _tempShips = GetShips();
            foreach (Ship ship in _tempShips)
            {
                ship.SetCurrentSpeed(speed);
            }
        }

        public void SetChase(bool chase) => _shouldChase = chase;

        public bool IsChasing()
        {
            return HasCommand && GetCommand() != null && GetCommand().CommandType == ConfigData.CommandTypes.Aggressive && _shouldChase;
        }

        public void StopChasing()
        {
            if (HasCommand && GetCommand() != null && GetCommand().CommandType == ConfigData.CommandTypes.Aggressive)
            {
                GetCommand().SetFinalize("Stopped Chasing");
            }
            SetChase(false);
        }

        public bool ShouldChase() => _shouldChase;
    }
}
