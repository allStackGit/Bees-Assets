using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadAuditInvariantTests
    {
        private string _squadSource;
        private string _strikerSource;
        private string _warpGateSource;
        private string _healSource;
        private string _beehiveSource;

        [SetUp]
        public void SetUp()
        {
            _squadSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.cs"));
            _strikerSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Striker.cs"));
            _warpGateSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "WarpGate.cs"));
            _healSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Heal.cs"));
            _beehiveSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Beehive.cs"));
        }

        [Test]
        public void ClearDataDoesNotForgetReusableSquadBoxOwnership()
        {
            string method = ExtractMethodBody(_squadSource, "ClearData");
            Assert.That(method, Does.Not.Contain("HasSquadBox = false"));
        }

        [Test]
        public void MatchupShipListsCannotExceedProtocolCap()
        {
            string enemies = ExtractMethodBody(_squadSource, "GetPotentialEnemies");
            string allies = ExtractMethodBody(_squadSource, "GetPotentialAllies");

            Assert.That(enemies, Does.Contain("_enemies.Count < 64"));
            Assert.That(enemies, Does.Not.Contain("_enemies.Count <= 64"));
            Assert.That(allies, Does.Contain("_tempShips.Count < _limit"));
            Assert.That(allies, Does.Not.Contain("_tempShips.Count <= _limit"));
        }

        [Test]
        public void NearbyEnemyInclusionUsesSquadIdentityInsteadOfUnityTruthiness()
        {
            string method = ExtractMethodBody(_squadSource, "GetPotentialEnemies");
            Assert.That(method, Does.Contain("potentialEnemy.Squad != target"));
            Assert.That(method, Does.Not.Contain("!potentialEnemy.Squad == target"));
        }

        [Test]
        public void ComparativeHealthUsesFractionalShipHealthAndHandlesEmptyLists()
        {
            Type squadType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad");
            Type shipType = RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship");
            MethodInfo method = squadType.GetMethod(
                "GetAverageHealthPercentForMatchup",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            object ship = FormatterServices.GetUninitializedObject(shipType);
            shipType.GetField("Health").SetValue(ship, 99);
            shipType.GetField("OriginalHealth").SetValue(ship, 100);

            Type listType = typeof(List<>).MakeGenericType(shipType);
            IList ships = (IList)Activator.CreateInstance(listType);
            ships.Add(ship);

            double damagedPercent = (double)method.Invoke(null, new object[] { ships });
            double emptyPercent = (double)method.Invoke(null, new object[] { Activator.CreateInstance(listType) });

            Assert.That(damagedPercent, Is.EqualTo(99d).Within(0.0001d));
            Assert.That(emptyPercent, Is.EqualTo(0d));
        }

        [Test]
        public void StrikerExitClearsCachedContactWithoutRequiringCurrentTouch()
        {
            string method = ExtractMethodBody(_strikerSource, "OnTriggerExit2D");
            Assert.That(method, Does.Contain("TouchingShip = null"));
            Assert.That(method, Does.Not.Contain("IsTouching(collider)"));
        }

        [Test]
        public void WarpGateAudioIsCreatedOnlyOncePerPooledObject()
        {
            string method = ExtractMethodBody(_warpGateSource, "ClearData");
            Assert.That(method, Does.Contain("Stage.ActivateAudio && !IsAudioLoaded"));
            Assert.That(method, Does.Contain("IsAudioLoaded = true"));
        }

        [Test]
        public void HealReleasesBeehiveCapacityWhenReservedShipBecomesInvalid()
        {
            string release = ExtractMethodBody(_healSource, "ReleaseHealingReservation");
            string move = ExtractMethodBody(_healSource, "MoveToBeehives");
            string heal = ExtractMethodBody(_healSource, "HealShips");

            Assert.That(release, Does.Contain("reservedBeehive.ShipsHealingHere.Remove(ship)"));
            Assert.That(release, Does.Contain("_shipsAndBeehives.Remove(ship.Id)"));
            Assert.That(move, Does.Contain("ReleaseHealingReservation(_shipsThatLostBeehiveOrDied[_index])"));
            Assert.That(heal, Does.Contain("ReleaseHealingReservation(_shipsThatLostBeehiveOrDied[_index])"));
        }

        [Test]
        public void HealOnlyReservesDamagedShipsAndReleasesCompletedShips()
        {
            string execute = ExtractMethodBody(_healSource, "Execute");
            string reached = ExtractMethodBody(_healSource, "ShipReachedBeehive");
            string heal = ExtractMethodBody(_healSource, "HealShips");

            Assert.That(execute, Does.Contain("s.Health < s.MaxHealth"));
            Assert.That(reached, Does.Contain("ShipsWaitingToHeal.Remove(ship)"));
            Assert.That(reached, Does.Contain("!ShipsHealing.Contains(ship)"));
            Assert.That(heal, Does.Contain("_ship.Health >= _ship.MaxHealth"));
            Assert.That(heal, Does.Contain("FinalizeIfAssignedShipsAreDone()"));
        }

        [Test]
        public void BeehiveDestructionKillsOnlyShipsThatActuallyReachedHealingCollider()
        {
            string enter = ExtractMethodBody(_beehiveSource, "OnTriggerEnter2D");
            string kill = ExtractMethodBody(_beehiveSource, "Kill");

            Assert.That(enter, Does.Contain("ShipReachedBeehive(_collidingShip)"));
            Assert.That(kill, Does.Contain("healCommand.IsShipActivelyHealing(s)"));
            Assert.That(kill, Does.Not.Contain("ShipsHealingHere.ToList().ForEach((s) =>\n            {\n                s.Kill"));
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf(" " + methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), $"Could not find method {methodName}.");
            int openingBrace = source.IndexOf('{', signature);
            int depth = 0;
            for (int index = openingBrace; index < source.Length; index++)
            {
                if (source[index] == '{') depth++;
                else if (source[index] == '}' && --depth == 0)
                    return source.Substring(openingBrace, index - openingBrace + 1);
            }

            Assert.Fail($"Method {methodName} has no balanced body.");
            return string.Empty;
        }
    }
}
