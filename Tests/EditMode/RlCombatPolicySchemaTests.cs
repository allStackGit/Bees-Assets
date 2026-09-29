using System;
using System.Collections;
using System.Collections.Generic;
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
            Type schemaType = RuntimeAssembly.GetType("RlPolicySchema");

            Assert.That(RuntimeAssembly.GetStaticField(schemaType, "Version"), Is.EqualTo(21));
            Assert.That((string)RuntimeAssembly.GetStaticField(schemaType, "Signature"),
                Does.Contain("healing=weapon-exclusive"));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedAllies"), Is.EqualTo(64));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedEnemies"), Is.EqualTo(64));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedMiningAsteroids"), Is.EqualTo(8));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedMapObjects"), Is.EqualTo(64));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedCollisionAsteroids"), Is.EqualTo(48));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedEnemyWeaponMounts"), Is.Zero);
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "NavigationGridSize"), Is.EqualTo(21));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "NavigationGridCellCount"), Is.EqualTo(441));
            Assert.That(RuntimeAssembly.GetStaticField(RuntimeAssembly.GetType("RlCombatPerception"), "NavigationGridCellSize"), Is.EqualTo(6f));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxWeaponSlots"), Is.EqualTo(5));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "SelfObservationSize"), Is.EqualTo(25));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "CapabilityObservationSize"), Is.EqualTo(12));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "ParentCarrierObservationSize"), Is.EqualTo(40));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "AllyObservationSize"), Is.EqualTo(44));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "CommunicationObservationSize"), Is.EqualTo(4));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MiningAsteroidObservationSize"), Is.EqualTo(7));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MapObjectObservationSize"), Is.EqualTo(12));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "CollisionAsteroidObservationSize"), Is.EqualTo(11));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "ObservationSize"), Is.EqualTo(7614));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "CommunicationContinuousActionCount"), Is.EqualTo(4));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "CommunicationContinuousActionStart"), Is.EqualTo(12));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "ContinuousActionCount"), Is.EqualTo(16));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "WeaponFireBranchCount"), Is.EqualTo(5));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "WeaponFireBranchSize"), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "SpecialActionBranch"), Is.EqualTo(5));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "DiscreteBranchCount"), Is.EqualTo(6));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "SpecialActionBranchSize"), Is.EqualTo(5));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "ShipSpecialAction"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MiningAction"), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "HealingAction"), Is.EqualTo(3));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "WarpAction"), Is.EqualTo(4));
        }

        [Test]
        public void EveryWeaponSlotHasItsOwnDiscreteFireBranch()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            int[] branchSizes = (int[])RuntimeAssembly.InvokeStatic(agentType, "CreateDiscreteBranchSizes");

            Assert.That(branchSizes.Length, Is.EqualTo(6));
            for (int slot = 0; slot < 5; slot++)
            {
                Assert.That(branchSizes[slot], Is.EqualTo(2), $"Weapon slot {slot} must have an independent cease/fire branch.");
            }
            Assert.That(branchSizes[5], Is.EqualTo(5));
        }

        [Test]
        public void TacticalPerceptionCapacityRemainsBoundedWithoutTrainingPopulationLimit()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedAllies"), Is.EqualTo(64));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedEnemies"), Is.EqualTo(64));
        }

        [Test]
        public void EnumIdentityEncodingHasCapacityForEveryCurrentShipAndWeaponType()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            int shipTypeSize = (int)RuntimeAssembly.GetStaticField(agentType, "ShipTypeObservationSize");
            int weaponTypeSize = (int)RuntimeAssembly.GetStaticField(agentType, "WeaponTypeObservationSize");
            int shipTypeCount = Enum.GetValues(RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes")).Length;
            int weaponTypeCount = Enum.GetValues(RuntimeAssembly.GetType("Assets.Scripts.ConfigData+WeaponTypes")).Length;

            Assert.That(shipTypeSize, Is.EqualTo(1));
            Assert.That(weaponTypeSize, Is.EqualTo(1));
            Assert.That(shipTypeCount, Is.EqualTo(24));
            Assert.That(weaponTypeCount, Is.EqualTo(10));
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
        public void ExplorationGridUsesEffectiveHiveMindVisionRangeWhenSightIsZero()
        {
            Type visionType = RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Weapons.HiveMindVision");
            Type gridType = RuntimeAssembly.GetType("RlTeamExplorationGrid");
            object grid = Activator.CreateInstance(gridType, true);
            GameObject levelObject = new GameObject("RL Exploration Vision Level Test");
            GameObject shipObject = new GameObject("RL Exploration Vision Ship Test");
            try
            {
                Component level = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
                Component ship = shipObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
                RuntimeAssembly.SetField(level, "MinX", -16f);
                RuntimeAssembly.SetField(level, "MinY", -16f);
                RuntimeAssembly.SetField(level, "MaxX", 16f);
                RuntimeAssembly.SetField(level, "MaxY", 16f);
                RuntimeAssembly.SetField(ship, "Level", level);
                RuntimeAssembly.SetField(ship, "Transform", shipObject.transform);
                RuntimeAssembly.SetField(ship, "Sight", 0);
                RuntimeAssembly.SetField(ship, "MaxRange", 40);

                Assert.That((int)RuntimeAssembly.InvokeStatic(visionType, "GetEffectiveRange", ship), Is.EqualTo(40),
                    "Combat ships with authored Sight=0 must retain the MaxRange fallback used by Hive Mind vision.");

                Type shipListType = typeof(List<>).MakeGenericType(ship.GetType());
                object ships = Activator.CreateInstance(shipListType);
                RuntimeAssembly.AddToCollection(ships, null);
                RuntimeAssembly.AddToCollection(ships, ship);
                Assert.DoesNotThrow(() => RuntimeAssembly.Invoke(grid, "Update", level, ships, 0.25f),
                    "Sparse ship collections may contain null entries during lifecycle transitions.");

                int cellCount = (int)RuntimeAssembly.GetStaticField(gridType, "CellCount");
                int freshCells = 0;
                for (int cell = 0; cell < cellCount; cell++)
                {
                    if ((float)RuntimeAssembly.Invoke(grid, "GetFreshness", cell, 0.25f) > 0f)
                    {
                        freshCells++;
                    }
                }

                Assert.That(freshCells, Is.EqualTo(cellCount),
                    "A 40-unit vision radius centered on a 32x32 arena should mark every exploration cell fresh.");

                RuntimeAssembly.SetField(ship, "Sight", 80);
                Assert.That((int)RuntimeAssembly.InvokeStatic(visionType, "GetEffectiveRange", ship), Is.EqualTo(80),
                    "An explicit Sight value must take precedence over MaxRange.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(shipObject);
                UnityEngine.Object.DestroyImmediate(levelObject);
            }
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
                new Vector2(2f, 2f));

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
                "A small obstacle centered on the ship should occupy only the center 6x6 navigation cell.");
        }

        [Test]
        public void HealingActionSuppressesWeaponFire()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            int healingAction = (int)RuntimeAssembly.GetStaticField(agentType, "HealingAction");
            int noSpecialAction = (int)RuntimeAssembly.GetStaticField(agentType, "NoSpecialAction");

            Assert.That((bool)RuntimeAssembly.InvokeStatic(
                agentType,
                "SpecialActionAllowsWeaponFire",
                healingAction), Is.False,
                "Choosing the healing action must suppress every weapon fire branch for that decision.");
            Assert.That((bool)RuntimeAssembly.InvokeStatic(
                agentType,
                "SpecialActionAllowsWeaponFire",
                noSpecialAction), Is.True);
        }

    }
}
