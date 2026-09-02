using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlPolicySchemaContractTests
    {
        private static string Read(params string[] parts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < parts.Length; i++)
            {
                path = Path.Combine(path, parts[i]);
            }
            return File.ReadAllText(path);
        }

        [Test]
        public void FrozenPolicyAbiHasExpectedPermanentCapacity()
        {
            string perception = Read("Scripts", "Scenes", "RlCombatPerception.cs");
            string agent = Read("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            string schema = Read("Scripts", "Scenes", "RlPolicySchema.cs");

            Assert.That(perception, Does.Contain("internal const int ShipTypeBitCount = 6;"));
            Assert.That(perception, Does.Contain("internal const int WeaponTypeBitCount = 6;"));
            Assert.That(perception, Does.Contain("internal const int MaxObservedAllies = 64;"));
            Assert.That(perception, Does.Contain("internal const int MaxObservedEnemies = 64;"));
            Assert.That(perception, Does.Contain("internal const int MaxWeaponSlots = 16;"));
            Assert.That(perception, Does.Contain("internal const int MaxObservedEnemyWeaponMounts = 16;"));
            Assert.That(perception, Does.Contain("internal const int ObjectiveObservationSize = 16;"));
            Assert.That(perception, Does.Contain("internal const int ObservationSize = SelfObservationSize +"));

            Assert.That(schema, Does.Contain("internal const int Version = 2;"));
            Assert.That(schema, Does.Contain("internal const int ExpectedObservationSize = 4685;"));
            Assert.That(schema, Does.Contain("disc=33,5,65,65,65"));
            Assert.That(agent, Does.Contain("RlPolicySchema.ValidateOrThrow();"));
        }

        [Test]
        public void CanonicalTrainerNetworkArchitectureIsFrozenAndFeedForward()
        {
            string trainer = Read("Training", "rl_1v1_config.yaml");
            string schema = Read("Scripts", "Scenes", "RlPolicySchema.cs");

            Assert.That(trainer, Does.Contain("normalize: true"));
            Assert.That(trainer, Does.Contain("hidden_units: 128"));
            Assert.That(trainer, Does.Contain("num_layers: 2"));
            Assert.That(trainer, Does.Not.Contain("memory:"));
            Assert.That(schema, Does.Contain("network=ff-128x2"));
            Assert.That(schema, Does.Contain("normalize=true"));
        }

        [Test]
        public void WeaponSlotsNeverAliasOverflowOntoLastAction()
        {
            string agent = Read("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            string schema = Read("Scripts", "Scenes", "RlPolicySchema.cs");

            Assert.That(agent, Does.Not.Contain("Mathf.Min(i, MaxWeaponSlots - 1)"));
            Assert.That(agent, Does.Contain("slot >= _ship.Weapons.Count"));
            Assert.That(agent, Does.Contain("_ship.Weapons[slot] is Turret turret"));
            Assert.That(schema, Does.Contain("ship.Weapons.Count > RlCombatPerception.MaxWeaponSlots"));
            Assert.That(agent, Does.Contain("RlPolicySchema.TryValidateShip(_ship, out string schemaError)"));
        }

        [Test]
        public void CarrierAndFutureObjectiveInformationHaveDedicatedStableBlocks()
        {
            string perception = Read("Scripts", "Scenes", "RlCombatPerception.cs");

            Assert.That(perception, Does.Contain("AddParentCarrierObservations(ship, sensor, origin);"));
            Assert.That(perception, Does.Contain("carrierShip.Carrier"));
            Assert.That(perception, Does.Contain("AddObjectiveObservations(sensor);"));
            Assert.That(perception, Does.Contain("Permanent ABI reservation"));
        }

        [Test]
        public void SpecialAbilityStateIncludesResourceCooldownAndPhase()
        {
            string perception = Read("Scripts", "Scenes", "RlCombatPerception.cs");

            int capabilityStart = perception.IndexOf("private static void AddCapabilityObservations", StringComparison.Ordinal);
            Assert.That(capabilityStart, Is.GreaterThanOrEqualTo(0));
            int nextMethod = perception.IndexOf("private static void AddParentCarrierObservations", capabilityStart, StringComparison.Ordinal);
            Assert.That(nextMethod, Is.GreaterThan(capabilityStart));
            string capability = perception.Substring(capabilityStart, nextMethod - capabilityStart);

            Assert.That(capability, Does.Contain("GetSpecialResourceFraction(ship)"));
            Assert.That(capability, Does.Contain("GetSpecialCooldownFraction(ship)"));
            Assert.That(capability, Does.Contain("GetSpecialPhase(ship)"));
            Assert.That(capability, Does.Contain("CanUseMiningAction(ship)"));
            Assert.That(capability, Does.Contain("CanUseHealingAction(ship)"));
            Assert.That(capability, Does.Contain("CanUseWarpAction(ship)"));
        }

        [Test]
        public void BargeReservesChargeBeforeWindupCanYield()
        {
            string source = Read("Scripts", "Entities", "Ships", "Barge.cs");

            int reserveMethod = source.IndexOf("internal bool TryReserveCharge()", StringComparison.Ordinal);
            Assert.That(reserveMethod, Is.GreaterThanOrEqualTo(0));
            int chargeMethod = source.IndexOf("public IEnumerator ChargeForward", reserveMethod, StringComparison.Ordinal);
            Assert.That(chargeMethod, Is.GreaterThan(reserveMethod));
            string reserve = source.Substring(reserveMethod, chargeMethod - reserveMethod);
            Assert.That(reserve, Does.Contain("HasStartedCharging = true;"));

            int reserveCall = source.IndexOf("if (!TryReserveCharge())", chargeMethod, StringComparison.Ordinal);
            int firstYield = source.IndexOf("yield return _chargeBuildDelay;", chargeMethod, StringComparison.Ordinal);
            Assert.That(reserveCall, Is.GreaterThan(chargeMethod));
            Assert.That(firstYield, Is.GreaterThan(reserveCall));
        }

        [Test]
        public void BargePoolResetClearsRlChargePhase()
        {
            string source = Read("Scripts", "Entities", "Ships", "Barge.cs");
            int methodStart = source.IndexOf("public override void ClearData()", StringComparison.Ordinal);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            int nextMethod = source.IndexOf("public override void Deactivate()", methodStart, StringComparison.Ordinal);
            Assert.That(nextMethod, Is.GreaterThan(methodStart));
            string clearData = source.Substring(methodStart, nextMethod - methodStart);

            Assert.That(clearData, Does.Contain("HasStartedCharging = false;"));
            Assert.That(clearData, Does.Contain("IsCharging = false;"));
            Assert.That(clearData, Does.Contain("SetChargePhase(0);"));
        }

        [Test]
        public void AgentEpisodeBeginClearsTrajectoryLocalState()
        {
            string source = Read("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            int methodStart = source.IndexOf("public override void OnEpisodeBegin()", StringComparison.Ordinal);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            int nextMethod = source.IndexOf("private void FixedUpdate()", methodStart, StringComparison.Ordinal);
            Assert.That(nextMethod, Is.GreaterThan(methodStart));
            string reset = source.Substring(methodStart, nextMethod - methodStart);

            Assert.That(reset, Does.Contain("ReleaseShip();"));
            Assert.That(reset, Does.Contain("_hasBoundShip = false;"));
            Assert.That(reset, Does.Contain("_hasParticipatedThisEpisode = false;"));
            Assert.That(reset, Does.Contain("_boundRuntimeShipId = 0;"));
            Assert.That(reset, Does.Contain("_decisionCounter = 0;"));
            Assert.That(reset, Does.Contain("_nextMiningActionTime = 0f;"));
            Assert.That(reset, Does.Contain("_nextHealingActionTime = 0f;"));
            Assert.That(reset, Does.Contain("_lastAimDirection = Vector2.up;"));
        }

        [Test]
        public void SpawnedAndCapabilityOnlyShipsRemainEligibleForDynamicPolicyAgents()
        {
            string source = Read("Scripts", "Scenes", "RlOneVsOneAgent.cs");

            Assert.That(source, Does.Contain("ProvisionAgentsForSpawnedShips(_stage);"));
            Assert.That(source, Does.Contain("CountPolicyControlledShips(level, beeSide)"));
            Assert.That(source, Does.Contain("CountPolicyControlledShips(level, humanSide)"));
            Assert.That(source, Does.Contain("EnsureAgentCount(stage, beeSide"));
            Assert.That(source, Does.Contain("EnsureAgentCount(stage, humanSide"));

            int methodStart = source.IndexOf("internal static bool RequiresPolicyControl(Ship ship)", StringComparison.Ordinal);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            int nextMethod = source.IndexOf("private static void EnsureAgentCount", methodStart, StringComparison.Ordinal);
            Assert.That(nextMethod, Is.GreaterThan(methodStart));
            string eligibility = source.Substring(methodStart, nextMethod - methodStart);

            Assert.That(eligibility, Does.Contain("CanUseMiningAction(ship)"));
            Assert.That(eligibility, Does.Contain("CanUseHealingAction(ship)"));
            Assert.That(eligibility, Does.Contain("CanUseWarpAction(ship)"));
        }
    }
}
