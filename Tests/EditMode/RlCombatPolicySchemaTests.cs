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

            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedAllies"), Is.EqualTo(15));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedEnemies"), Is.EqualTo(16));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedMapObjects"), Is.EqualTo(16));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxWeaponSlots"), Is.EqualTo(8));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MapObjectObservationSize"), Is.EqualTo(10));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "ObservationSize"), Is.EqualTo(879));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "ContinuousActionCount"), Is.EqualTo(4));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "WeaponCommandBranchSize"), Is.EqualTo(17));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "SpecialActionBranchSize"), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "AllyTargetBranchSize"), Is.EqualTo(16));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "EnemyTargetBranchSize"), Is.EqualTo(17));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MapObjectTargetBranchSize"), Is.EqualTo(17));
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
            object beacon = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Entities.Ships.Beacon");
            object mobileShip = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Entities.Ships.Ship");
            RuntimeAssembly.SetField(mobileShip, "IsMobile", true);

            Assert.That((bool)RuntimeAssembly.InvokeStatic(agentType, "RequiresPolicyControl", beacon), Is.False);
            Assert.That((bool)RuntimeAssembly.InvokeStatic(agentType, "RequiresPolicyControl", mobileShip), Is.True);
        }

        [Test]
        public void HiveMindMiningKnowledgeIsSideWidePersistentAndResettable()
        {
            GameObject stateObject = new GameObject("RL Hive Mind State Test");
            GameObject observerObject = new GameObject("RL Hive Mind Observer Test");
            GameObject asteroidObject = new GameObject("RL Hive Mind Asteroid Test");
            try
            {
                Component state = stateObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
                Component observer = observerObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
                Component asteroid = asteroidObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.MiningAsteroid"));
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

                Array caches = (Array)RuntimeAssembly.GetField(state, "HiveMindMiningAsteroidCache");
                Assert.That(RuntimeAssembly.GetCount(caches.GetValue(0)), Is.EqualTo(1));
                Assert.That(RuntimeAssembly.GetCount(caches.GetValue(1)), Is.Zero);

                RuntimeAssembly.Invoke(state, "ResetState");
                Assert.That(RuntimeAssembly.GetCount(caches.GetValue(0)), Is.Zero,
                    "Level reset must clear discovered strategic objects from the prior lifecycle.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asteroidObject);
                UnityEngine.Object.DestroyImmediate(observerObject);
                UnityEngine.Object.DestroyImmediate(stateObject);
            }
        }

        [Test]
        public void ObservationCollectionsUseExplicitDeterministicOrdering()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Scenes",
                "RlOneVsOneAgent.cs"));

            Assert.That(source, Does.Contain("SortShipsForObservation(_allyCandidates, origin)"));
            Assert.That(source, Does.Contain("SortShipsForObservation(_enemyCandidates, origin)"));
            Assert.That(source, Does.Contain("((int)left.ShipType).CompareTo((int)right.ShipType)"));
            Assert.That(source, Does.Contain("left.Id.CompareTo(right.Id)"));
            Assert.That(source, Does.Contain("_mapObjectCandidates.Sort"));
            Assert.That(source, Does.Contain("Weapon is an authored List rather than an unordered set"));
        }
    }
}
