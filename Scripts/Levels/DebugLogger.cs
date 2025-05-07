using Assets.Scripts;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class DebugLogger : MonoBehaviour
{
    public Stage Stage;
    /// <summary>
    /// Whether or not the game is being debugged and should log a lot of debugging data
    /// </summary>
    public bool IsDebugging;
    public bool IsNetworkLogging;
    // Stage variables
    public int __HivemindCommands, __LevelTimeouts, __TotalShips, __LevelCompletes, __Grids;
    // Network variables
    public static int __TotalRequests;
    public static double __TotalLatency, __AverageLatency, __TotalLength, __AverageLength;
    public List<string> __PastServerRequests, __SocketLevels, __StandingRequests;
    /// <summary>
    /// The average time a request takes to complete in ms
    /// </summary>
    public double __AverageRequestTime;
    public List<long> __UsedHashes;

    public int[] __CommandCounts;


    // Pool Variables
    public int __BargePoolSize, __BeaconPoolSize, __BeehivePoolSize, __BumblebeePoolSize, __CarpenterBeePoolSize, __CarrierPoolSize, __CruiserPoolSize, __DreadnoughtPoolSize,
        __DronePoolSize, __FactoryPoolSize, __FireBargePoolSize, __FlagshipPoolSize, __FrigatePoolSize, __GunshipPoolSize, __HoneybeePoolSize, __HornetPoolSize, __LeafcutterPoolSize,
        __QueenPoolSize, __ScoutPoolSize, __StrikerPoolSize, __WarpGatePoolSize, __WaspPoolSize, __YellowJacketPoolSize, __PlutoMapPoolSize, __NeptuneMapPoolSize, __UranusMapPoolSize, __BeeSmallProjectilePoolSize,
        __BeeMediumProjectilePoolSize, __BumblebeeShotProjectilePoolSize, __FlagshipShotProjectilePoolSize, __RocketProjectilePoolSize, __HumanSmallProjectilePoolSize, __HumanMediumProjectilePoolSize,
        __BeamProjectilePoolSize, __SplitShotProjectilePoolSize, __QueenSmallProjectilePoolSize, __QueenLargeProjectilePoolSize, __StrikerBombProjectilePoolSize, __RocketExplosionProjectilePoolSize,
        __FireBargeExplosionProjectilePoolSize, __EmptyObstacleListObjectPoolSize, __MazeObstacleListObjectPoolSize, __ThreePathsObstacleListObjectPoolSize, __ForestObstacleListObjectPoolSize,
        __TheWallObstacleListObjectPoolSize, __CollisionAsteroidPoolSize, __CollisionAsteroidShardPoolSize, __AsteroidPiecePoolSize, __MiningAsteroidPoolSize, __SquadPoolSize, __CarrierSquadPoolSize, __AggressiveCommandPoolSize, __BombingRunCommandPoolSize,
        __ChargeCommandPoolSize, __CircleSquadCommandPoolSize, __ClosestFriendlyCommandPoolSize, __FullRetreatCommandPoolSize, __GuardCommandPoolSize, __InAndOutCommandPoolSize,
        __MiningCommandPoolSize, __MoveToRandomCommandPoolSize, __PatrolCommandPoolSize, __RetreatCommandPoolSize, __ScoutingCommandPoolSize, __SwipeSquadCommandPoolSize, __HoldCommandPoolSize,
        __HealCommandPoolSize;
    public void LogData()
    {
        if (IsDebugging)
        {
            PoolStats();
            StageLogging();
            LevelLogging();
        }

        if (Stage.WatchServerRequests || IsNetworkLogging)
        {
            NetworkLogging();
        }
    }
    public void NetworkLogging()
    {
        __TotalRequests = ConfigData.__TotalRequests;
        //__TotalLatency = ConfigData.__TotalTimeOnQueue;
        __AverageLatency = ConfigData.__AverageTimeOnQueue;
        __TotalLength = ConfigData.__TotalLength;
        __AverageLength = ConfigData.__AverageLength;

        if (ConfigData.__PastServerRequests.Count > 0)
        {
            __UsedHashes = ConfigData.UsedHashes.ToList();
            __PastServerRequests = ConfigData.__PastServerRequests.Select((r) => $"Request #{r.Hash} ({r.Type}) on queue for {r.TimeOnQueue}ms").ToList();
            __AverageRequestTime = (ConfigData.__PastServerRequests.Sum((r) => r.TimeOnQueue) / ConfigData.__PastServerRequests.Count);
            __SocketLevels = ConfigData.Socket.OpenLevels.Select(s => s.Name).ToList();
            __StandingRequests = ConfigData.Socket.StandingRequests.Select((r) => $"Request #{r.Hash} ({r.Type}) on queue since {r.StartTime}").ToList();
            //__Updates = Time.frameCount;
        }
    }
    public void LevelLogging()
    {
        Stage.Levels.ForEach(level =>
        {
            level.UpdateDebugVariables();
        });
    }
    public void StageLogging()
    {
        __TotalShips = Stage.Levels.Sum((l) => l.State.Ships.Count);
        __Grids = Stage.PathfinderGrids.Count;
    }
    public void PoolStats()
    {
        __BargePoolSize = Stage.Pool.BargePool.CountAll;
        __BeaconPoolSize = Stage.Pool.BeaconPool.CountAll;
        __BeehivePoolSize = Stage.Pool.BeehivePool.CountAll;
        __BumblebeePoolSize = Stage.Pool.BumblebeePool.CountAll;
        __CarpenterBeePoolSize = Stage.Pool.CarpenterBeePool.CountAll;
        __CarrierPoolSize = Stage.Pool.CarrierPool.CountAll;
        __CruiserPoolSize = Stage.Pool.CruiserPool.CountAll;
        __DreadnoughtPoolSize = Stage.Pool.DreadnoughtPool.CountAll;
        __DronePoolSize = Stage.Pool.DronePool.CountAll;
        __FactoryPoolSize = Stage.Pool.FactoryPool.CountAll;
        __FireBargePoolSize = Stage.Pool.FireBargePool.CountAll;
        __FlagshipPoolSize = Stage.Pool.FlagshipPool.CountAll;
        __FrigatePoolSize = Stage.Pool.FrigatePool.CountAll;
        __GunshipPoolSize = Stage.Pool.GunshipPool.CountAll;
        __HoneybeePoolSize = Stage.Pool.HoneybeePool.CountAll;
        __HornetPoolSize = Stage.Pool.HornetPool.CountAll;
        __LeafcutterPoolSize = Stage.Pool.LeafcutterPool.CountAll;
        __QueenPoolSize = Stage.Pool.QueenPool.CountAll;
        __ScoutPoolSize = Stage.Pool.ScoutPool.CountAll;
        __StrikerPoolSize = Stage.Pool.StrikerPool.CountAll;
        __WarpGatePoolSize = Stage.Pool.WarpGatePool.CountAll;
        __WaspPoolSize = Stage.Pool.WarpGatePool.CountAll;
        __YellowJacketPoolSize = Stage.Pool.YellowJacketPool.CountAll;
        __PlutoMapPoolSize = Stage.Pool.PlutoMapPool.CountAll;
        __NeptuneMapPoolSize = Stage.Pool.NeptuneMapPool.CountAll;
        __UranusMapPoolSize = Stage.Pool.UranusMapPool.CountAll;
        __BeeSmallProjectilePoolSize = Stage.Pool.BeeSmallProjectilePool.CountAll;
        __BeeMediumProjectilePoolSize = Stage.Pool.BeeMediumProjectilePool.CountAll;
        __BumblebeeShotProjectilePoolSize = Stage.Pool.BumblebeeShotProjectilePool.CountAll;
        __FlagshipShotProjectilePoolSize = Stage.Pool.FlagshipShotProjectilePool.CountAll;
        __RocketProjectilePoolSize = Stage.Pool.RocketProjectilePool.CountAll;
        __HumanSmallProjectilePoolSize = Stage.Pool.HumanSmallProjectilePool.CountAll;
        __HumanMediumProjectilePoolSize = Stage.Pool.HumanMediumProjectilePool.CountAll;
        __BeamProjectilePoolSize = Stage.Pool.BeamProjectilePool.CountAll;
        __SplitShotProjectilePoolSize = Stage.Pool.SplitShotProjectilePool.CountAll;
        __QueenSmallProjectilePoolSize = Stage.Pool.QueenSmallProjectilePool.CountAll;
        __QueenLargeProjectilePoolSize = Stage.Pool.QueenLargeProjectilePool.CountAll;
        __StrikerBombProjectilePoolSize = Stage.Pool.StrikerBombProjectilePool.CountAll;
        __RocketExplosionProjectilePoolSize = Stage.Pool.RocketExplosionProjectilePool.CountAll;
        __FireBargeExplosionProjectilePoolSize = Stage.Pool.FireBargeExplosionProjectilePool.CountAll;
        __EmptyObstacleListObjectPoolSize = Stage.Pool.EmptyObstacleListObjectPool.CountAll;
        __MazeObstacleListObjectPoolSize = Stage.Pool.MazeObstacleListObjectPool.CountAll;
        __ThreePathsObstacleListObjectPoolSize = Stage.Pool.ThreePathsObstacleListObjectPool.CountAll;
        __ForestObstacleListObjectPoolSize = Stage.Pool.ForestObstacleListObjectPool.CountAll;
        __TheWallObstacleListObjectPoolSize = Stage.Pool.TheWallObstacleListObjectPool.CountAll;
        __CollisionAsteroidPoolSize = Stage.Pool.CollisionAsteroidPool.CountAll;
        __CollisionAsteroidShardPoolSize = Stage.Pool.CollisionAsteroidShardPool.CountAll;
        __AsteroidPiecePoolSize = Stage.Pool.AsteroidPiecePool.CountAll;
        __MiningAsteroidPoolSize = Stage.Pool.MiningAsteroidPool.CountAll;
        __SquadPoolSize = Stage.Pool.SquadPool.CountAll;
        __CarrierSquadPoolSize = Stage.Pool.CarrierSquadPool.CountAll;
        __AggressiveCommandPoolSize = Stage.Pool.AggressiveCommandPool.CountAll;
        __BombingRunCommandPoolSize = Stage.Pool.BombingRunCommandPool.CountAll;
        __ChargeCommandPoolSize = Stage.Pool.ChargeCommandPool.CountAll;
        __CircleSquadCommandPoolSize = Stage.Pool.CircleSquadCommandPool.CountAll;
        __ClosestFriendlyCommandPoolSize = Stage.Pool.ClosestFriendlyCommandPool.CountAll;
        __FullRetreatCommandPoolSize = Stage.Pool.FullRetreatCommandPool.CountAll;
        __GuardCommandPoolSize = Stage.Pool.GuardCommandPool.CountAll;
        __InAndOutCommandPoolSize = Stage.Pool.InAndOutCommandPool.CountAll;
        __MiningCommandPoolSize = Stage.Pool.MiningCommandPool.CountAll;
        __MoveToRandomCommandPoolSize = Stage.Pool.MoveToRandomCommandPool.CountAll;
        __PatrolCommandPoolSize = Stage.Pool.PatrolCommandPool.CountAll;
        __RetreatCommandPoolSize = Stage.Pool.RetreatCommandPool.CountAll;
        __ScoutingCommandPoolSize = Stage.Pool.ScoutingCommandPool.CountAll;
        __SwipeSquadCommandPoolSize = Stage.Pool.SwipeSquadCommandPool.CountAll;
        __HoldCommandPoolSize = Stage.Pool.HoldCommandPool.CountAll;
        __HealCommandPoolSize = Stage.Pool.HealCommandPool.CountAll;
    }
}
