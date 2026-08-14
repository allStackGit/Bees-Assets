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
        private string _bargeSource;
        private string _chargingBarSource;
        private string _carrierSource;
        private string _warpGateSource;
        private string _fullRetreatSource;
        private string _retreatSource;
        private string _healSource;
        private string _beehiveSource;
        private string _miningSource;
        private string _miningAsteroidSource;
        private string _strikerBombSource;
        private string _bombingRunSource;

        [SetUp]
        public void SetUp()
        {
            _squadSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.cs")) +
                File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.Combat.cs"));
            _strikerSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Striker.cs"));
            _bargeSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Barge.cs"));
            _chargingBarSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "ChargingBar.cs"));
            _carrierSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Carrier.cs"));
            _warpGateSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "WarpGate.cs"));
            _fullRetreatSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "FullRetreat.cs"));
            _retreatSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Retreat.cs"));
            _healSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Heal.cs"));
            _beehiveSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Beehive.cs"));
            _miningSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Mining.cs"));
            _miningAsteroidSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "MiningAsteroid.cs"));
            _strikerBombSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "StrikerBomb.cs"));
            _bombingRunSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "BombingRun.cs"));
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
            Assert.That(allies, Does.Contain("if (_allies.Count > _limit)"));
            Assert.That(allies, Does.Contain("_allies.RemoveRange(_limit, _allies.Count - _limit)"));
            Assert.That(allies, Does.Not.Contain("_allies.Count <= _limit"));
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
            MethodInfo method = squadType.GetMethod("GetAverageHealthPercentForMatchup", BindingFlags.Static | BindingFlags.NonPublic);
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
        public void InterruptedBargeChargeCoroutinesCannotResumeIntoNewOrders()
        {
            string charge = ExtractMethodBody(_bargeSource, "ChargeForward");
            string stop = ExtractMethodBody(_bargeSource, "StopCharge");
            string reset = ExtractMethodBody(_bargeSource, "ResetCharge");
            string clear = ExtractMethodBody(_bargeSource, "ClearData");

            Assert.That(_bargeSource, Does.Contain("private int _chargeLifecycleId"));
            Assert.That(charge, Does.Contain("int lifecycleId = ++_chargeLifecycleId"));
            Assert.That(charge, Does.Contain("lifecycleId != _chargeLifecycleId"));
            Assert.That(stop, Does.Contain("lifecycleId == _chargeLifecycleId"));
            Assert.That(reset, Does.Contain("_chargeLifecycleId++"));
            Assert.That(clear, Does.Contain("_chargeLifecycleId++"));
        }

        [Test]
        public void PooledChargingBarCancelsOldRechargeAndSaturatesAtOneHundredPercent()
        {
            string setup = ExtractMethodBody(_chargingBarSource, "Setup");
            string charge = ExtractMethodBody(_chargingBarSource, "ChargeBar");
            string drain = ExtractMethodBody(_chargingBarSource, "DrainBar");

            Assert.That(setup, Does.Contain("Ship.Level.CancelTimer(_chargeBarTimer)"));
            Assert.That(setup, Does.Contain("IsCharging = false"));
            Assert.That(charge, Does.Contain("math.min(100, PercentCharged + ChargingIncrement)"));
            Assert.That(charge, Does.Contain("PercentCharged >= 100"));
            Assert.That(drain, Does.Contain("math.max(0, PercentCharged - percent)"));
        }

        [Test]
        public void CarrierDeathDetachesCarrierShipsBeforeCarrierCanBePooled()
        {
            string kill = ExtractMethodBody(_carrierSource, "Kill");
            string reload = ExtractMethodBody(_strikerSource, "CheckCarrierReload");
            string returnToCarrier = ExtractMethodBody(_strikerSource, "ReturnToCarrierIfNecessary");

            Assert.That(kill, Does.Contain("List<Ship> levelShips = Level.State.Ships"));
            Assert.That(kill, Does.Contain("candidate.Side != Side"));
            Assert.That(kill, Does.Contain("carrierShip.Carrier != this"));
            Assert.That(kill, Does.Contain("carrierShip.Carrier = replacementCarrier"));
            Assert.That(kill, Does.Contain("carrierShip.Carrier = null"));
            Assert.That(kill, Does.Contain("carrierSquad.Carrier = replacementCarrier"));
            Assert.That(reload, Does.Contain("Carrier != null && !Carrier.IsDead"));
            Assert.That(returnToCarrier, Does.Contain("Carrier != null && !Carrier.IsDead"));
        }

        [Test]
        public void RetreatAlreadyAtSafeDistanceUsesOriginalThreeSecondCompletionDelay()
        {
            string execute = ExtractMethodBody(_retreatSource, "Execute");
            Assert.That(execute, Does.Contain("_delayedSetFinalizeTimer.Reuse(3f, DelaySetFinalize)"));
            Assert.That(execute, Does.Not.Contain("_delayedSetFinalizeTimer.Reuse(50"));
        }

        [Test]
        public void WarpGateAudioIsCreatedOnlyOncePerPooledObject()
        {
            string method = ExtractMethodBody(_warpGateSource, "ClearData");
            Assert.That(method, Does.Contain("Stage.ActivateAudio && !IsAudioLoaded"));
            Assert.That(method, Does.Contain("IsAudioLoaded = true"));
        }

        [Test]
        public void FullRetreatUsesPerCommandParticipantsInsteadOfGateGlobalCount()
        {
            string execute = ExtractMethodBody(_fullRetreatSource, "Execute");
            string warpKill = ExtractMethodBody(_fullRetreatSource, "WarpKill");
            string gateEnter = ExtractMethodBody(_warpGateSource, "OnTriggerEnter2D");
            Assert.That(_fullRetreatSource, Does.Contain("private readonly HashSet<long> _shipIdsWarping"));
            Assert.That(execute, Does.Contain("_shipIdsWarping.Add(ship.Id)"));
            Assert.That(warpKill, Does.Contain("_shipIdsWarping.Count == 0"));
            Assert.That(warpKill, Does.Not.Contain("TargetWarpGate.ShipsWarpingHere.Count == 0"));
            Assert.That(gateEnter, Does.Contain("QueueShipForWarp(_collidingShip)"));
        }

        [Test]
        public void FullRetreatPrunesDeadParticipantsAndDeduplicatesWarpQueue()
        {
            string queue = ExtractMethodBody(_fullRetreatSource, "QueueShipForWarp");
            string prune = ExtractMethodBody(_fullRetreatSource, "RemoveUnavailableWarpParticipants");
            string warpKill = ExtractMethodBody(_fullRetreatSource, "WarpKill");
            Assert.That(queue, Does.Contain("!ShipsWaitingToWarp.Contains(ship)"));
            Assert.That(prune, Does.Contain("ship == null || ship.IsDead"));
            Assert.That(prune, Does.Contain("TargetWarpGate.ShipsWarpingHere.Remove(shipId)"));
            Assert.That(warpKill, Does.Contain("!_shipIdsWarping.Contains(ship.Id)"));
        }

        [Test]
        public void HealReleasesBeehiveCapacityWhenReservedShipBecomesInvalid()
        {
            string release = ExtractMethodBody(_healSource, "ReleaseHealingReservation");
            string move = ExtractMethodBody(_healSource, "MoveToBeehives");
            string heal = ExtractMethodBody(_healSource, "HealShips");
            Assert.That(release, Does.Contain("reservedBeehive.ShipsHealingHere.Remove(ship)"));
            Assert.That(release, Does.Contain("_shipsAndBeehives.Remove(ship.Id)"));
            Assert.That(move, Does.Contain("ReleaseHealingReservation(_ship"));
            Assert.That(heal, Does.Contain("ReleaseHealingReservation(_ship"));
            Assert.That(move, Does.Contain("_ship.Health < _ship.MaxHealth"));
            Assert.That(heal, Does.Contain("_ship.Health < _ship.MaxHealth"));
        }

        [Test]
        public void HealOnlyReservesDamagedShipsAndReleasesCompletedShips()
        {
            string execute = ExtractMethodBody(_healSource, "Execute");
            string reached = ExtractMethodBody(_healSource, "ShipReachedBeehive");
            string heal = ExtractMethodBody(_healSource, "HealShips");
            Assert.That(execute, Does.Contain("ship.Health < ship.MaxHealth"));
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
            Assert.That(kill, Does.Contain("healCommand.IsShipActivelyHealing(ship)"));
        }

        [Test]
        public void MiningCountsOnlyLiveMiningCapableShips()
        {
            string execute = ExtractMethodBody(_miningSource, "Execute");
            string found = ExtractMethodBody(_miningSource, "FoundAsteroid");
            string mine = ExtractMethodBody(_miningSource, "Mine");
            Assert.That(execute, Does.Contain("ship.IsMiningShip && !ship.IsDead"));
            Assert.That(execute, Does.Contain("for (int i = 0; i < MiningShips.Count; i++)"));
            Assert.That(found, Does.Contain("!ship.IsMiningShip"));
            Assert.That(found, Does.Contain("!MiningShips.Contains(ship)"));
            Assert.That(mine, Does.Contain("ship == null || ship.IsDead || !ship.IsMiningShip"));
        }

        [Test]
        public void MiningAsteroidFinalizesCommandsWithoutMutatingEnumeration()
        {
            string kill = ExtractMethodBody(_miningAsteroidSource, "Kill");
            Assert.That(kill, Does.Contain("while (SquadsMining.Count > 0)"));
            Assert.That(kill, Does.Contain("CommandTypes.Mining"));
            Assert.That(kill, Does.Contain("int previousCount = SquadsMining.Count"));
            Assert.That(kill, Does.Contain("SquadsMining.RemoveAt(lastIndex)"));
            Assert.That(kill, Does.Not.Contain("SquadsMining.ForEach"));
        }

        [Test]
        public void StrikerBombStaysOutOfPoolUntilDelayedDamageResolves()
        {
            string sequence = ExtractMethodBody(_strikerBombSource, "KillSequence");
            string delayed = ExtractMethodBody(_strikerBombSource, "DamageAndKill");
            string kill = ExtractMethodBody(_strikerBombSource, "Kill");
            string clear = ExtractMethodBody(_strikerBombSource, "ClearData");

            Assert.That(sequence, Does.Contain("Deactivate()"));
            Assert.That(sequence, Does.Contain("_damageTimer.Reuse(.5f, DamageAndKill)"));
            Assert.That(sequence, Does.Not.Contain("Kill();\n            }\n            else"));
            Assert.That(delayed, Does.Contain("Damage()"));
            Assert.That(delayed, Does.Contain("Kill()"));
            Assert.That(kill, Does.Contain("Level.CancelTimer(_damageTimer)"));
            Assert.That(clear, Does.Contain("ContactedShip = null"));
        }

        [Test]
        public void BombingRunHandlesLostCarriersAndChainedFireBargeDeaths()
        {
            string finished = ExtractMethodBody(_bombingRunSource, "HaveAllShipsFinished");
            string timer = ExtractMethodBody(_bombingRunSource, "Timer");
            string clear = ExtractMethodBody(_bombingRunSource, "ClearData");

            Assert.That(finished, Does.Contain("_finishingStriker.Carrier != null && !_finishingStriker.Carrier.IsDead"));
            Assert.That(timer, Does.Contain("is FireBarge fireBarge && !fireBarge.IsDead"));
            Assert.That(timer, Does.Contain("if (IsDead)"));
            Assert.That(clear, Does.Contain("_timerLoops = 0"));
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = FindMethodDeclaration(source, methodName);
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

        private static int FindMethodDeclaration(string source, string methodName)
        {
            string token = methodName + "(";
            int searchFrom = 0;
            while (searchFrom < source.Length)
            {
                int occurrence = source.IndexOf(token, searchFrom, StringComparison.Ordinal);
                if (occurrence < 0) return -1;

                int lineStart = source.LastIndexOf('\n', occurrence);
                lineStart = lineStart < 0 ? 0 : lineStart + 1;
                string prefix = source.Substring(lineStart, occurrence - lineStart);
                if (prefix.Contains("public ") || prefix.Contains("private ") ||
                    prefix.Contains("protected ") || prefix.Contains("internal "))
                {
                    return occurrence;
                }

                searchFrom = occurrence + token.Length;
            }
            return -1;
        }
    }
}
