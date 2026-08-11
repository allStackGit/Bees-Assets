namespace Assets.Scripts
{
    public static partial class ConfigData
    {
        public enum SceneTypes
        {
            Scene,
            Stage
        }

        public enum SquadTypes
        {
            Squad,
            CarrierSquad
        }

        public enum ShipTypes
        {
            Barge,
            Beacon,
            Beehive,
            Bumblebee,
            CarpenterBee,
            Carrier,
            Cruiser,
            Dreadnought,
            Drone,
            Factory,
            FireBarge,
            Flagship,
            Frigate,
            Gunship,
            Honeybee,
            Hornet,
            Leafcutter,
            Queen,
            Scout,
            Striker,
            WarpGate,
            Wasp,
            YellowJacket,
            HumanTarget,
        }

        public enum ShipTypeLetters
        {
            A,
            B,
            C,
            D,
            E,
            F,
            G,
            H,
            I,
            J,
            K,
            L,
            M,
            N,
            O,
            P,
            Q,
            R,
            S,
            T,
            U,
            V,
            W,
            X,
        }

        public enum WeaponTypes
        {
            Bomb,
            BeamCannon,
            LightCannon,
            Turret,
            FullShipTurret,
            RocketTurret,
            DualCannon,
            Eye,
            QueenEye,
            SplitShot,
        }

        public enum WeaponSoundTypes
        {
            SmallLaser,
            BigLaser,
            FlagshipChargingLaser,
            FlagshipLaser,
            QueenLaser,
            BeamCannon,
            BowtieLaser,
            LightCannon,
            RocketLaunch,
            Bomb,
            FireBargeBomb,
            None,
        }

        public enum RequestTypes
        {
            Request,
            GetMatchupStrategy,
            GetStrategy,
            SendRLData,
            StoreCommands,
            SetupLevel,
            ReconnectLevel,
            StoreUserData,
            GetUserData,
            GetSettings
        }

        public enum ProjectileTypes
        {
            None,
            BeeSmall,
            BeeMedium,
            BumblebeeShot,
            FlagshipShot,
            Rocket,
            HumanSmall,
            HumanMedium,
            Beam,
            SplitShot,
            QueenSmall,
            QueenLarge,
            StrikerBomb,
            RocketExplosion,
            FireBargeExplosion,
            FireTankExplosion
        }

        public enum CommandTypes
        {
            Uninitialized,
            Matchup,
            Shooting,
            Aggressive,
            BombingRun,
            Charge,
            Retreat,
            MoveToRandom,
            CircleSquad,
            RightSwipe,
            LeftSwipe,
            ClosestFriendly,
            InAndOut,
            Patrol,
            Guard,
            Scouting,
            Mining,
            FullRetreat,
            Hold,
            Heal,
            MoveToPoint,
        }

        public enum ShootingStrategyTypes
        {
            FirstSeen,
            Random,
            Revenge,
            MostDangerous,
            MostHealth,
            LeastHealth,
            MostPowerful,
            LeastPowerful,
            Closest,
            Furthest,
            MostRange,
            LeastRange,
            Fastest,
            Slowest,
            MostValuable,
            LeastValuable,
            TypeA,
            TypeB,
            TypeC,
            TypeD,
            TypeE,
            TypeF,
            TypeG,
            TypeH,
            TypeI,
            TypeJ,
            TypeK,
            TypeL,
            TypeM,
            TypeN,
            TypeO,
            TypeP,
            TypeQ,
            TypeR,
            TypeS,
            TypeT,
            TypeU,
            TypeV,
            TypeW,
            TypeX
        }

        public enum SquadActions
        {
            IsMatchingSpeed,
            CeaseFire,
            AttackOnSight,
            Patrol,
            Guard,
            Chase,
            Hold,
            LockOn,
        }

        public enum ObstacleTypes
        {
            StaticObstacle,
            MapBorder,
            CollisionAsteroid,
            MiningAsteroid,
            AsteroidPiece
        }

        public enum Locations
        {
            Pluto,
            Neptune,
            Uranus,
            Titania
        }

        public enum GameModes
        {
            Unset,
            Campaign,
            FreePlay,
            Challenge,
            FishTank,
        }

        public enum MatchupStrategyTypes
        {
            Random,
            Revenge,
            MostDangerous,
            LeastHealth,
            MostHealth,
            MostPowerful,
            LeastPowerful,
            Closest,
            Furthest,
            MostRange,
            LeastRange,
            Fastest,
            Slowest,
            InCombat,
            GangUp,
            MostValuable,
            LeastValuable,
            TypeA,
            TypeB,
            TypeC,
            TypeD,
            TypeE,
            TypeF,
            TypeG,
            TypeH,
            TypeI,
            TypeJ,
            TypeK,
            TypeL,
            TypeM,
            TypeN,
            TypeO,
            TypeP,
            TypeQ,
            TypeR,
            TypeS,
            TypeT,
            TypeU,
            TypeV,
            TypeW,
            TypeX
        }
    }
}
