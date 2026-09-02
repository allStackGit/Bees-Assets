using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlCombatPolicySchemaTests
    {
        [Test]
        public void FinalCombatSchemaHasFixedFullScaleCapacity()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");

            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedAllies"), Is.EqualTo(64));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedEnemies"), Is.EqualTo(64));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedMiningAsteroids"), Is.EqualTo(8));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedMapObjects"), Is.EqualTo(64));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedCollisionAsteroids"), Is.EqualTo(48));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedEnemyWeaponMounts"), Is.EqualTo(16));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "NavigationGridSize"), Is.EqualTo(13));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "NavigationGridCellCount"), Is.EqualTo(169));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxWeaponSlots"), Is.EqualTo(16));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "SelfObservationSize"), Is.EqualTo(29));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "CapabilityObservationSize"), Is.EqualTo(12));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "ParentCarrierObservationSize"), Is.EqualTo(19));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MiningAsteroidObservationSize"), Is.EqualTo(7));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MapObjectObservationSize"), Is.EqualTo(12));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "CollisionAsteroidObservationSize"), Is.EqualTo(11));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "ObservationSize"), Is.EqualTo(4685));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "ContinuousActionCount"), Is.EqualTo(34));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "WeaponFireBranchCount"), Is.EqualTo(16));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "WeaponFireBranchSize"), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "SpecialActionBranch"), Is.EqualTo(16));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "AllyTargetBranch"), Is.EqualTo(17));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "EnemyTargetBranch"), Is.EqualTo(18));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MapObjectTargetBranch"), Is.EqualTo(19));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "DiscreteBranchCount"), Is.EqualTo(20));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "SpecialActionBranchSize"), Is.EqualTo(5));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "ShipSpecialAction"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MiningAction"), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "HealingAction"), Is.EqualTo(3));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "WarpAction"), Is.EqualTo(4));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "AllyTargetBranchSize"), Is.EqualTo(65));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "EnemyTargetBranchSize"), Is.EqualTo(65));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MapObjectTargetBranchSize"), Is.EqualTo(65));
        }

        [Test]
        public void EveryWeaponSlotHasItsOwnDiscreteFireBranch()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            int[] branchSizes = (int[])RuntimeAssembly.InvokeStatic(agentType, "CreateDiscreteBranchSizes");

            Assert.That(branchSizes.Length, Is.EqualTo(20));
            for (int slot = 0; slot < 16; slot++)
            {
                Assert.That(branchSizes[slot], Is.EqualTo(2), $"Weapon slot {slot} must have an independent cease/fire branch.");
            }
            Assert.That(branchSizes[16], Is.EqualTo(5));
            Assert.That(branchSizes[17], Is.EqualTo(65));
            Assert.That(branchSizes[18], Is.EqualTo(65));
            Assert.That(branchSizes[19], Is.EqualTo(65));
        }

        [Test]
        public void TacticalPerceptionCapacityIsIndependentOfTrainingPopulationLimit()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            Type optionsType = RuntimeAssembly.GetType("RlOneVsOneTrainingOptions");

            int trainingMaximum = (int)RuntimeAssembly.GetStaticField(optionsType, "MaximumShipsPerSide");
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedAllies"), Is.EqualTo(64));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedEnemies"), Is.EqualTo(64));
            Assert.That(64, Is.GreaterThan(trainingMaximum),
                "Deployment-scale tactical perception must not be capped by the current curriculum population limit.");
        }

        [Test]
        public void EnumIdentityEncodingHasCapacityForEveryCurrentShipAndWeaponType()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            int shipBits = (int)RuntimeAssembly.GetStaticField(agentType, "ShipTypeBitCount");
            int weaponBits = (int)RuntimeAssembly.GetStaticField(agentType, "WeaponTypeBitCount");
            int shipTypeCount = Enum.GetValues(RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes")).Length;
            int weaponTypeCount = Enum.GetValues(RuntimeAssembly.GetType("Assets.Scripts.ConfigData+WeaponTypes")).Length;

            Assert.That(1 << shipBits, Is.GreaterThanOrEqualTo(shipTypeCount));
            Assert.That(1 << weaponBits, Is.GreaterThanOrEqualTo(weaponTypeCount));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MapObjectTypeBitCount"), Is.EqualTo(4));
        }

        [Test]
        public void TacticalDistanceEncodingIsSignedBoundedAndIndependentOfArenaSize()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            float positive = (float)RuntimeAssembly.InvokeStatic(agentType, "SquashSignedDistance", 10f);
            float negative = (float)RuntimeAssembly.InvokeStatic(agentType, "SquashSignedDistance", -10f);
            float far = (float)RuntimeAssembly.InvokeStatic(agentType, "SquashSignedDistance", 10000f);

            Assert.That(positive, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(negative, Is.EqualTo(-0.2f).Within(0.0001f));
            Assert.That(far, Is.GreaterThan(0f).And.LessThan(1f));
        }

        [Test]
        public void PassiveVisionOnlyShipsDoNotRequirePolicyAgents()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            GameObject beaconObject = new GameObject("RL Passive Beacon Test");
            GameObject mobileObject = new GameObject("RL Mobile Ship Test");
            try
            {
                Component beacon = beaconObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Beacon"));
                Component mobileShip = mobileObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
                RuntimeAssembly.SetField(mobileShip, "IsMobile", true);

                Assert.That((bool)RuntimeAssembly.InvokeStatic(agentType, "RequiresPolicyControl", beacon), Is.False);
                Assert.That((bool)RuntimeAssembly.InvokeStatic(agentType, "RequiresPolicyControl", mobileShip), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mobileObject);
                UnityEngine.Object.DestroyImmediate(beaconObject);
            }
        }

        [Test]
        public void PassiveBeaconCanContributeMiningKnowledgeWithoutReceivingPolicy()
        {
            GameObject stateObject = new GameObject("RL Shared Vision State Test");
            GameObject beaconObject = new GameObject("RL Shared Vision Beacon Test");
            GameObject asteroidObject = new GameObject("RL Shared Vision Asteroid Test");
            try
            {
                Component state = stateObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
                Component beacon = beaconObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Beacon"));
                Component asteroid = asteroidObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.MiningAsteroid"));

                RuntimeAssembly.SetField(beacon, "Side", 1);
                RuntimeAssembly.SetField(beacon, "IsHiveMindControlled", true);

                Assert.That((bool)RuntimeAssembly.Invoke(
                    state,
                    "RecordHiveMindMiningAsteroidSighting",
                    beacon,
                    asteroid), Is.True);

                Array caches = (Array)RuntimeAssembly.GetField(state, "HiveMindMiningAsteroidCache");
                Assert.That(RuntimeAssembly.GetCount(caches.GetValue(0)), Is.EqualTo(1));
                Assert.That(RuntimeAssembly.GetCount(caches.GetValue(1)), Is.Zero);
                Assert.That((bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType("RlOneVsOneAgent"),
                    "RequiresPolicyControl",
                    beacon), Is.False,
                    "Beacon vision should contribute knowledge without giving the Beacon its own policy trajectory.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asteroidObject);
                UnityEngine.Object.DestroyImmediate(beaconObject);
                UnityEngine.Object.DestroyImmediate(stateObject);
            }
        }

        [Test]
        public void HiveMindEnvironmentalKnowledgeIsSideWidePersistentAndResettable()
        {
            GameObject stateObject = new GameObject("RL Hive Mind State Test");
            GameObject observerObject = new GameObject("RL Hive Mind Observer Test");
            GameObject asteroidObject = new GameObject("RL Hive Mind Asteroid Test");
            GameObject obstacleObject = new GameObject("RL Hive Mind Obstacle Test");
            GameObject mapObjectObject = new GameObject("RL Hive Mind Map Object Test");
            try
            {
                Component state = stateObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
                Component observer = observerObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
                Component asteroid = asteroidObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.MiningAsteroid"));
                Component obstacle = obstacleObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.StaticObstacle"));
                Component mapObject = mapObjectObject.AddComponent(RuntimeAssembly.GetType("MapObject"));
                RuntimeAssembly.SetField(observer, "Side", 1);
                RuntimeAssembly.SetField(observer, "IsHiveMindControlled", true);

                Assert.That((bool)RuntimeAssembly.Invoke(
                    state,
                    "RecordHiveMindMiningAsteroidSighting",
                    observer,
                    asteroid), Is.True);
                Assert.That((bool)RuntimeAssembly.Invoke(
                    state,
                    "RecordHiveMindMiningAsteroidSighting",
                    observer,
                    asteroid), Is.False,
                    "Repeated sightings should preserve one side-wide memory entry.");
                Assert.That((bool)RuntimeAssembly.Invoke(
                    state,
                    "RecordHiveMindObstacleSighting",
                    observer,
                    obstacle), Is.True);
                Assert.That((bool)RuntimeAssembly.Invoke(
                    state,
                    "RecordHiveMindMapObjectSighting",
                    observer,
                    mapObject), Is.True);

                Array miningCaches = (Array)RuntimeAssembly.GetField(state, "HiveMindMiningAsteroidCache");
                Array obstacleCaches = (Array)RuntimeAssembly.GetField(state, "HiveMindObstacleCache");
                Array mapObjectCaches = (Array)RuntimeAssembly.GetField(state, "HiveMindMapObjectCache");
                Assert.That(RuntimeAssembly.GetCount(miningCaches.GetValue(0)), Is.EqualTo(1));
                Assert.That(RuntimeAssembly.GetCount(obstacleCaches.GetValue(0)), Is.EqualTo(2),
                    "Mining asteroids are also obstacle knowledge even though the policy emits them in dedicated mining slots only.");
                Assert.That(RuntimeAssembly.GetCount(mapObjectCaches.GetValue(0)), Is.EqualTo(1));

                RuntimeAssembly.Invoke(state, "ResetState");
                Assert.That(RuntimeAssembly.GetCount(miningCaches.GetValue(0)), Is.Zero);
                Assert.That(RuntimeAssembly.GetCount(obstacleCaches.GetValue(0)), Is.Zero);
                Assert.That(RuntimeAssembly.GetCount(mapObjectCaches.GetValue(0)), Is.Zero,
                    "Level reset must clear discovered environment from the prior lifecycle.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mapObjectObject);
                UnityEngine.Object.DestroyImmediate(obstacleObject);
                UnityEngine.Object.DestroyImmediate(asteroidObject);
                UnityEngine.Object.DestroyImmediate(observerObject);
                UnityEngine.Object.DestroyImmediate(stateObject);
            }
        }

        [Test]
        public void NeuralTrainingKeepsEveryShipOnActivatedHiveMindVisionPath()
        {
            string lifecycle = ReadSource("Scripts", "Entities", "Ships", "Ship.Lifecycle.cs");
            string vision = ReadSource("Scripts", "Entities", "Ships", "Weapons", "HivemindVision.cs");

            Assert.That(lifecycle, Does.Contain("IsHiveMindControlled = Stage.IsTrainingNueralNetwork || !IsUserControlled"));
            Assert.That(lifecycle, Does.Contain("IsUserControlled && !Stage.IsTrainingNueralNetwork"));
            Assert.That(lifecycle, Does.Contain("if (IsHiveMindControlled)"));
            Assert.That(lifecycle, Does.Contain("HiveMindVision.Activate()"));
            Assert.That(vision, Does.Contain("public bool CanSee(Collider2D targetCollider"));
            Assert.That(vision, Does.Contain("targetCollider.ClosestPoint(observerWorldPosition)"),
                "Large targets and walls should become visible when their collider edge enters sight, not only when their center does.");
        }

        [Test]
        public void SharedVisionRefreshCoversEnemyShipsObstaclesAndTargetableMapObjects()
        {
            string queries = ReadSource("Scripts", "Levels", "GameState.Queries.cs");

            Assert.That(queries, Does.Contain("observer.HiveMindVision.CanSee(spotted.Collider, spotted.GetPosition())"));
            Assert.That(queries, Does.Contain("RecordHiveMindSighting(observer, spotted)"),
                "HumanTarget and every other enemy Ship type must use the ordinary shared enemy-ship vision path.");
            Assert.That(queries, Does.Contain("PathfinderObstacleScope.GetActiveObstacleObjects(Level)"));
            Assert.That(queries, Does.Contain("RecordHiveMindObstacleSighting(observer, obstacle)"));
            Assert.That(queries, Does.Contain("GetComponentsInChildren<MapObject>(false)"));
            Assert.That(queries, Does.Contain("RecordHiveMindMapObjectSighting(observer, mapObject)"));
        }

        [Test]
        public void EnvironmentObservationsSeparateStaticGeometryMovingAsteroidsAndStrategicObjects()
        {
            string source = ReadSource("Scripts", "Scenes", "RlCombatPerception.cs");

            Assert.That(source, Does.Contain("GetMiningAsteroidsVisibleToHiveMind(side)"));
            Assert.That(source, Does.Contain("GetMapObjectsVisibleToHiveMind(side)"));
            Assert.That(source, Does.Contain("GetObstaclesVisibleToHiveMind(side)"));
            Assert.That(source, Does.Contain("collisionAsteroid.Body.linearVelocity"));
            Assert.That(source, Does.Contain("asteroid.HalfExtents.x"));
            Assert.That(source, Does.Contain("asteroid.HalfExtents.y"));
            Assert.That(source, Does.Contain("AddHeading(sensor, asteroid.Rotation)"));
            Assert.That(source, Does.Contain("obstacle.ObstacleType == ConfigData.ObstacleTypes.StaticObstacle"));
            Assert.That(source, Does.Contain("MarkNavigationAabb(_navigationOccupancy"));
            Assert.That(source, Does.Contain("MarkNavigationBounds(_navigationOccupancy"));
            Assert.That(source, Does.Contain("mapObject is CanisterBomb"));
            Assert.That(source, Does.Contain("FireTankObservationType"));
            Assert.That(source, Does.Contain("mapObject.Targetable ? 1f : 0f"));
            Assert.That(source, Does.Not.Contain("Projectile"),
                "Projectile-evasion observations are intentionally excluded from the combat policy.");
        }

        [Test]
        public void NavigationGridMarksLocalStaticGeometryWithoutConsumingExplicitObjectSlots()
        {
            Type perceptionType = RuntimeAssembly.GetType("RlCombatPerception");
            int gridSize = (int)RuntimeAssembly.GetStaticField(perceptionType, "NavigationGridSize");
            int cellCount = (int)RuntimeAssembly.GetStaticField(perceptionType, "NavigationGridCellCount");
            float[] occupancy = new float[cellCount];

            RuntimeAssembly.InvokeStatic(
                perceptionType,
                "MarkNavigationAabb",
                occupancy,
                Vector2.zero,
                Vector2.zero,
                new Vector2(4f, 4f));

            int center = (gridSize / 2) * gridSize + gridSize / 2;
            int blocked = 0;
            for (int i = 0; i < occupancy.Length; i++)
            {
                if (occupancy[i] > 0f)
                {
                    blocked++;
                }
            }

            Assert.That(occupancy[center], Is.EqualTo(1f));
            Assert.That(blocked, Is.EqualTo(1),
                "A small obstacle centered on the ship should occupy only the center 10x10 navigation cell.");

            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            Assert.That(agent, Does.Not.Contain("MaxObservedObstacles"));
            Assert.That(agent, Does.Contain("MaxObservedCollisionAsteroids"));
        }

        [Test]
        public void SelfPerceptionUsesActualMapBoundsAndCompactGlobalBattleCounts()
        {
            string source = ReadSource("Scripts", "Scenes", "RlCombatPerception.cs");

            Assert.That(source, Does.Contain("NormalizeSignedCoordinate(position.x, level.MinX, level.MaxX)"));
            Assert.That(source, Does.Contain("NormalizeSignedCoordinate(position.y, level.MinY, level.MaxY)"));
            Assert.That(source, Does.Contain("level.MaxX - level.MinX"));
            Assert.That(source, Does.Contain("level.MaxY - level.MinY"));
            Assert.That(source, Does.Contain("state.GetShips(side).Count"));
            Assert.That(source, Does.Contain("state.GetShipsVisibleToHiveMind(side).Count"));
        }

        [Test]
        public void TargetableNonShipObjectsCanBeHitByRlPointFire()
        {
            string projectile = ReadSource("Scripts", "Entities", "Projectiles", "Projectile.cs");
            string mapObject = ReadSource("Scripts", "Entities", "MapObject.cs");
            string turret = ReadSource("Scripts", "Entities", "Ships", "Weapons", "Turret.Targeting.cs");

            Assert.That(projectile, Does.Contain("DamageObstacle((CollisionAsteroid)obstacle)"),
                "Collision asteroids must remain destructible by ordinary projectiles.");
            Assert.That(mapObject, Does.Contain("Health -= LastHitProjectile.Power"),
                "Targetable MapObjects such as the Fire Tank must remain destructible by projectile contact.");
            Assert.That(turret, Does.Contain("if (IsRlControlled)"));
            Assert.That(turret, Does.Contain("FireAtPoint()"),
                "RL turrets must be able to shoot an observed point without a scripted Ship target.");
        }

        [Test]
        public void ObservationCollectionsUseExplicitDeterministicOrdering()
        {
            string source = ReadSource("Scripts", "Scenes", "RlCombatPerception.cs");

            Assert.That(source, Does.Contain("SortShipsForObservation(_allyCandidates, origin)"));
            Assert.That(source, Does.Contain("SortShipsForObservation(_enemyCandidates, origin)"));
            Assert.That(source, Does.Contain("((int)left.ShipType).CompareTo((int)right.ShipType)"));
            Assert.That(source, Does.Contain("left.Id.CompareTo(right.Id)"));
            Assert.That(source, Does.Contain("_miningAsteroidCandidates.Sort"));
            Assert.That(source, Does.Contain("_mapObjectCandidates.Sort"));
            Assert.That(source, Does.Contain("_collisionAsteroidCandidates.Sort"));
            Assert.That(source, Does.Contain("left.Type.CompareTo(right.Type)"));
            Assert.That(source, Does.Contain("iteration order is intentionally irrelevant"));
            Assert.That(source, Does.Contain("Weapon is an authored List rather than an unordered set"));
        }

        [Test]
        public void PrimitiveCapabilityActionsAreMaskedByCapabilityNotCurrentSituation()
        {
            string source = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");

            Assert.That(source, Does.Contain("ship.ShipType == ConfigData.ShipTypes.Factory"));
            Assert.That(source, Does.Contain("ship.ShipType == ConfigData.ShipTypes.CarpenterBee"));
            Assert.That(source, Does.Contain("ship.Side != ConfigData.Configuration.BeeSide"));
            Assert.That(source, Does.Contain("shipSize.x < beehiveSize.x && shipSize.y < beehiveSize.y"));
            Assert.That(source, Does.Contain("ship.Side == ConfigData.Configuration.HumanSide"));
            Assert.That(source, Does.Contain("ship.ShipType != ConfigData.ShipTypes.WarpGate"));

            Assert.That(source, Does.Contain("canControl && CanUseMiningAction(_ship)"));
            Assert.That(source, Does.Contain("canControl && CanUseHealingAction(_ship)"));
            Assert.That(source, Does.Contain("canControl && CanUseWarpAction(_ship)"));
            Assert.That(source, Does.Not.Contain("GetSpecialReadiness(_ship) > 0f"),
                "Cooldown/contact validity must not leak through the action mask.");
        }

        [Test]
        public void PrimitiveCapabilityExecutionUsesCurrentPhysicalContactAndNeverHiveMindCommands()
        {
            string source = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");

            Assert.That(source, Does.Contain("FindTouchingMiningAsteroid()"));
            Assert.That(source, Does.Contain("_ship.Collider.IsTouching(asteroid.Collider)"));
            Assert.That(source, Does.Contain("FindTouchingBeehive()"));
            Assert.That(source, Does.Contain("beehive.HealCollider.IsTouching(_ship.Collider)"));
            Assert.That(source, Does.Contain("FindTouchingWarpGate()"));
            Assert.That(source, Does.Contain("warpGate.WarpCollider.IsTouching(_ship.Collider)"));
            Assert.That(source, Does.Contain("_ship.EndKill()"));
            Assert.That(source, Does.Not.Contain("CommandTypes.Mining"));
            Assert.That(source, Does.Not.Contain("CommandTypes.Heal"));
            Assert.That(source, Does.Not.Contain("CommandTypes.FullRetreat"));
            Assert.That(source, Does.Not.Contain("MoveToTrackedPoint"),
                "Mine/heal/warp actions must not navigate the ship for the policy.");
        }

        [Test]
        public void SuccessfulPrimitiveOutcomesProduceRewardButInvalidAttemptsDoNot()
        {
            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            string coordinator = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeCoordinator.cs");
            string yellowJacket = ReadSource("Scripts", "Entities", "Ships", "YellowJacket.cs");

            Assert.That(agent, Does.Contain("RewardSuccessfulCapabilityOutcome(_ship.Tsv - oldTsv)"));
            Assert.That(agent, Does.Contain("RewardSuccessfulCapabilityOutcome(preservedTsv)"));
            Assert.That(agent, Does.Contain("if (asteroid == null)"));
            Assert.That(agent, Does.Contain("if (beehive == null)"));
            Assert.That(agent, Does.Contain("if (warpGate == null)"));
            Assert.That(coordinator, Does.Contain("RecordSuccessfulCapabilityOutcome"));
            Assert.That(coordinator, Does.Contain("_active.ApplyImmediateTsvReward(ship.Side, reward)"));
            Assert.That(yellowJacket, Does.Contain("RlOneVsOneEpisodeCoordinator.RecordHit"),
                "Direct Yellow Jacket damage must receive the same real-outcome reward path as weapon impacts.");
        }

        [Test]
        public void DirectShipSpecialsDoNotDependOnTheirScriptedCommandLoops()
        {
            string striker = ReadSource("Scripts", "Entities", "Ships", "Striker.cs");
            string barge = ReadSource("Scripts", "Entities", "Ships", "Barge.cs");

            Assert.That(striker, Does.Contain("if (Stage.IsTrainingNueralNetwork)"));
            Assert.That(striker, Does.Contain("HasDroppedBomb = false;"),
                "A policy-controlled Striker must be able to drop again after its proximity reload without BombingRun resetting a new run.");
            Assert.That(barge, Does.Contain("if (!Stage.IsTrainingNueralNetwork && target != null && !target.IsDead)"));
            Assert.That(barge, Does.Contain("MoveInDirection(Rotation)"),
                "RL Barge charge must follow the heading established by the policy rather than auto-aiming at a scripted target.");
        }

        private static string ReadSource(params string[] pathParts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < pathParts.Length; i++)
            {
                path = Path.Combine(path, pathParts[i]);
            }
            return File.ReadAllText(path);
        }
    }
}
