using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship
    {
        public string __Strategy, __Squad, __SavedSquad, __SquadStatus, __CommandStatus, __LastStopReason, __EnemySquad, __TargetEnemyShipToFollow, __SquadColor, __SquadShootingStrategy;
        public Vector2 __CommandDestination, __Velocity, __TargetCoordinates;
        public float __Firepower, __DamagePerSecond, __CurrentSpeed, __DegreesToTargetCoordinates, __DistanceToTargetCoordinates, __TurningRadius, __Width, __Height, __SquadWidth, __SquadHeight;
        public long __Tsv, __CommandTsv;
        public bool __HasReachedDestination, __SquadHasReachedDestination, __IsInBounds;
        public List<Ship> __WeaponTargetShips, __SquadShips, __NearbyShips, __ShipsWarpingHere, __ShipsOnTopOf, __SortedTargetingQueue;
        public List<string> __ShipsWithinRangeOfWeapons, __PastCommands, __BannedStrats, __DamageStatuses, __CommandTargetingQueue, __NearbyAsteroids, __HivemindShips, __RejectReasons;
        public int __Clearance, __MineralsMined;

        protected virtual void UpdateDebugProperties()
        {
            __Strategy = $"{Squad?.GetCommand()?.CommandType} - {Squad?.GetCommand()?.OutcomeId}";
            __EnemySquad = Squad.HasEnemy ? Squad.GetCommand().EnemySquad.Name : "-";
            __ShipsWithinRangeOfWeapons = ShipsWithinRange.Select(ship => ship.Name).ToList();
            __Squad = Squad.Name;
            __SavedSquad = Squad.SavedSquad.Name;
            __SquadStatus = Squad.Status;
            __CommandDestination = Squad.HasCommand ? Squad.GetCommand().GetDestination() : Vector2.zero;
            __TargetCoordinates = TargetCoordinates;
            if (IsMobile)
            {
                __Velocity = Body.linearVelocity;
            }
            __Firepower = Firepower;
            __Tsv = Tsv;
            __DamagePerSecond = DamagePerSecond;
            __CommandTsv = Squad.HasCommand ? Squad.GetCommand().Tsv : 0;
            __PastCommands = Squad.PastCommands.Select(command => command.IsFinalized
                ? $"#{command.OutcomeId} - {command.CommandType} ({command.Tsv}) against {command.Enemy} ended due to \"{command.FinalizationCause}\" and took {command.Age} ticks"
                : $"#{command.OutcomeId} - {command.CommandType} (Unfinalized)").ToList();
            __HasReachedDestination = HasReachedDestination;
            __SquadHasReachedDestination = Squad.HasReachedDestination;
            __SquadShips = Squad.GetShips();
            __BannedStrats = Squad.BannedStrats.Select(strategy => strategy.ToString()).ToList();
            __DamageStatuses = Level.State.ShipDamageStatuses[Side - 1]
                .Select(status => $"{status.TotalDamageSentToShip} damage sent to {status.Ship.Name} against {status.Health} health. Current health: {status.Ship.Health}")
                .ToList();
            __TargetEnemyShipToFollow = HasTargetEnemyShipToFollow
                ? $"Following {TargetEnemyShipToFollow.Name} at {TargetEnemyShipToFollow.GetPosition()}"
                : "None";
            __CommandTargetingQueue = Squad.HasCommand && Squad.GetCommand().HasEnemy
                ? Squad.GetCommand().TargetingQueue.Select(ship => ship.Name).ToList()
                : new List<string>();
            __CurrentSpeed = CurrentSpeed;
            __NearbyAsteroids = NearbyAsteroids.Select(asteroid => asteroid.Name).ToList();
            __DegreesToTargetCoordinates = GetDegreesTowardsPoint(TargetCoordinates);
            __DistanceToTargetCoordinates = DistanceToPoint(TargetCoordinates);
            __TurningRadius = ConfigData.ShipTurningRadius;
            __NearbyShips = HasProximityCollider ? ProximityCollider.NearbyEnemyShips.ToList() : new List<Ship>();
            __HivemindShips = Level.State.GetShipsVisibleToHiveMind(Side).Select(ship => ship.ToString()).ToList();
            __Clearance = GetClearance();
            __IsInBounds = IsInBounds();
            __SquadColor = ColorUtility.ToHtmlStringRGB(Squad.Color);
            __Width = GetWidth();
            __Height = GetHeight();
            __SquadShootingStrategy = Squad.GetShootingStrategy().ToString();
            __MineralsMined = FleetShip.MineralsMinedThisLevel;

            if (ShipType == ConfigData.ShipTypes.WarpGate)
            {
                __ShipsWarpingHere = ((WarpGate)this).ShipsWarpingHere
                    .Select(id => Level.State.GetShipById(id))
                    .ToList();
            }
        }
    }
}
