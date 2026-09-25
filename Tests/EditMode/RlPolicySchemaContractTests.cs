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

            Assert.That(perception, Does.Contain("internal const int ShipTypeObservationSize = 1;"));
            Assert.That(perception, Does.Contain("internal const int WeaponTypeObservationSize = 1;"));
            Assert.That(perception, Does.Contain("11, 2, 19, 7, 22, 4, 15, 0, 17, 9, 23, 5,"));
            Assert.That(perception, Does.Contain("13, 20, 1, 16, 8, 21, 3, 18, 10, 14, 6, 12"));
            Assert.That(perception, Does.Contain("4, 9, 1, 7, 0, 6, 3, 8, 2, 5"));
            Assert.That(perception, Does.Contain("internal const int MaxObservedAllies = 64;"));
            Assert.That(perception, Does.Contain("internal const int MaxObservedEnemies = 64;"));
            Assert.That(perception, Does.Contain("internal const int MaxWeaponSlots = 5;"));
            Assert.That(perception, Does.Contain("internal const int MaxObservedEntityWeaponSlots = MaxWeaponSlots;"));
            Assert.That(perception, Does.Contain("internal const int MaxObservedEnemyWeaponMounts = 0;"));
            Assert.That(perception, Does.Contain("internal const int ShipIdentityObservationSize = 1;"));
            Assert.That(perception, Does.Contain("internal const int CommunicationObservationSize = 4;"));
            Assert.That(perception, Does.Contain("internal const int SelfObservationSize = 24 + ShipIdentityObservationSize;"));
            Assert.That(perception, Does.Contain("AddShipIdentityObservation(sensor, ship);"));
            Assert.That(perception, Does.Contain("AddShipIdentityObservation(sensor, observed);"));
            Assert.That(perception, Does.Contain("internal const int SelfWeaponObservationSize = 15;"));
            Assert.That(perception, Does.Contain("internal const int ObservedWeaponObservationSize = 5;"));
            Assert.That(perception, Does.Contain("AddObservedWeaponObservation(ship.Weapons[slot], sensor);"));
            Assert.That(perception, Does.Contain("internal const int EntityObservationSize = EntityCoreObservationSize + ShipIdentityObservationSize + MaxObservedEntityWeaponSlots * ObservedWeaponObservationSize;"));
            Assert.That(perception, Does.Contain("internal const int AllyObservationSize = EntityObservationSize + CommunicationObservationSize;"));
            Assert.That(perception, Does.Contain("AddEntityWeaponSlots(observed, sensor);"));
            Assert.That(perception, Does.Contain("AddAllySlots(sensor, _allyCandidates, MaxObservedAllies, origin, frameQuarterTurns);"));
            Assert.That(perception, Does.Contain("RlOneVsOneAgent.AddCommunicationObservations(sensor, ally);"));
            Assert.That(perception, Does.Contain("AddSelfWeaponObservation(ship, ship.Weapons[slot], sensor, frameQuarterTurns);"));
            Assert.That(perception, Does.Contain("internal const int ObjectiveObservationSize = 16;"));
            Assert.That(perception, Does.Contain("internal const int ObservationSize = SelfObservationSize +"));

            Assert.That(schema, Does.Contain("internal const int Version = 20;"));
            Assert.That(schema, Does.Contain("internal const int PerceptionObservationSize = 7593;"));
            Assert.That(schema, Does.Contain("internal const int ReservedObservationCount = 20;"));
            Assert.That(schema, Does.Contain("internal const int ExpectedObservationSize = ReservedObservationEndExclusive;"));
            Assert.That(schema, Does.Contain("bees-rl-v20"));
            Assert.That(schema, Does.Contain("obs=7614"));
            Assert.That(schema, Does.Contain("tail=episode-progress+20-reserved"));
            Assert.That(schema, Does.Contain("coord-frame=team-episode-distinct-quarter-turn"));
            Assert.That(schema, Does.Contain("cont=16"));
            Assert.That(schema, Does.Contain("disc=2x5,5"));
            Assert.That(schema, Does.Contain("weapon-aim=slotwise-xy"));
            Assert.That(schema, Does.Contain("weapon-fire=slotwise-cease-or-fire"));
            Assert.That(schema, Does.Contain("ship-id=episode-permuted-scalar23"));
            Assert.That(schema, Does.Contain("ally=44-with-private-comm4"));
            Assert.That(schema, Does.Contain("communication=4-continuous-private-allied"));
            Assert.That(perception, Does.Contain("internal const int ExplorationGridSize = RlTeamExplorationGrid.Size;"));
            Assert.That(perception, Does.Contain("internal const int ExplorationGridCellCount = RlTeamExplorationGrid.CellCount;"));
            Assert.That(schema, Does.Contain("exploration-grid=16x16-team-shared-sight-recency"));
            Assert.That(agent, Does.Contain("RlCombatPerception.SelfWeaponObservationSize"));
            Assert.That(agent, Does.Not.Contain("RlCombatPerception.WeaponObservationSize"));
            Assert.That(agent, Does.Contain("RlPolicySchema.ValidateOrThrow();"));
        }

        [Test]
        public void ContinualLearningConfigTracksFrozenPolicyAbi()
        {
            string config = Read("Training", "continual_learning_config.json");
            string schema = Read("Scripts", "Scenes", "RlPolicySchema.cs");

            Assert.That(config, Does.Contain("\"policy_abi_version\": 19"));
            Assert.That(config, Does.Contain("\"observation_schema_version\": 11"));
            Assert.That(config, Does.Contain("\"telemetry_observation_size\": 7614"));
            Assert.That(config, Does.Contain("bees-rl-v20"));
            Assert.That(config, Does.Contain("obs=7614"));
            Assert.That(config, Does.Contain("grid=21x21-cell6"));
            Assert.That(schema, Does.Contain("internal const int Version = 20;"));
            Assert.That(schema, Does.Contain("internal const int ExpectedObservationSize = ReservedObservationEndExclusive;"));
        }

        [Test]
        public void CanonicalTrainerNetworkArchitectureIsFrozenAndFeedForward()
        {
            string trainer = Read("Training", "rl_1v1_config.yaml");
            string schema = Read("Scripts", "Scenes", "RlPolicySchema.cs");

            Assert.That(trainer, Does.Contain("normalize: true"));
            Assert.That(trainer, Does.Contain("hidden_units: 128"));
            Assert.That(trainer, Does.Contain("num_layers: 3"));
            Assert.That(trainer, Does.Not.Contain("memory:"));
            Assert.That(schema, Does.Contain("network=ff-128x3"));
            Assert.That(schema, Does.Contain("normalize=true"));
        }

        [Test]
        public void WeaponSlotsNeverAliasOverflowOntoLastAction()
        {
            string agent = Read("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            string schema = Read("Scripts", "Scenes", "RlPolicySchema.cs");

            Assert.That(agent, Does.Not.Contain("Mathf.Min(i, MaxWeaponSlots - 1)"));
            Assert.That(agent, Does.Contain("slot >= ship.Weapons.Count"));
            Assert.That(agent, Does.Contain("ship.Weapons[slot] is Turret turret"));
            Assert.That(schema, Does.Contain("ship.Weapons.Count > RlCombatPerception.MaxWeaponSlots"));
            Assert.That(agent, Does.Contain("RlPolicySchema.TryValidateShip(_ship, out string schemaError)"));
        }

        [Test]
        public void EveryWeaponSlotHasIndependentAimAndFireActionsInOneDecision()
        {
            string agent = Read("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            string schema = Read("Scripts", "Scenes", "RlPolicySchema.cs");

            Assert.That(agent, Does.Contain("internal const int CommunicationContinuousActionCount = 4;"));
            Assert.That(agent, Does.Contain("internal const int CommunicationContinuousActionStart = MovementContinuousActionCount + MaxWeaponSlots * WeaponAimContinuousActionsPerSlot;"));
            Assert.That(agent, Does.Contain("internal const int ContinuousActionCount = CommunicationContinuousActionStart + CommunicationContinuousActionCount;"));
            Assert.That(agent, Does.Contain("internal const int WeaponFireBranchCount = MaxWeaponSlots;"));
            Assert.That(agent, Does.Contain("internal const int WeaponFireBranchSize = 2;"));
            Assert.That(agent, Does.Contain("int aimStart = WeaponAimContinuousActionStart + slot * WeaponAimContinuousActionsPerSlot;"));
            Assert.That(agent, Does.Contain("Vector2 policyAim = new Vector2(continuous[aimStart], continuous[aimStart + 1]);"));
            Assert.That(agent, Does.Contain("_weaponAimDirections[slot] = RlPolicyCoordinateFrame.PolicyToWorld("));
            Assert.That(agent, Does.Contain("bool fire = discrete[WeaponFireBranchStart + slot] == FireWeaponAction;"));
            Assert.That(agent, Does.Contain("ApplyWeaponCommand(_ship, slot, _weaponAimDirections[slot], fire);"));
            Assert.That(agent, Does.Not.Contain("_lastAimDirection"),
                "Independent weapon branches must not secretly share one retained aim vector.");
            Assert.That(schema, Does.Contain("ExpectedContinuousActions = 16"));
            Assert.That(schema, Does.Contain("ExpectedWeaponFireBranchCount = 5"));
        }

        [Test]
        public void AlliedCommunicationIsStoredPerBoundShipAndRemainsPrivateToAllies()
        {
            string agent = Read("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            string perception = Read("Scripts", "Scenes", "RlCombatPerception.cs");

            Assert.That(agent, Does.Contain("private static readonly Dictionary<Ship, Vector4> ShipCommunications"));
            Assert.That(agent, Does.Contain("ResetCommunication(_ship);"));
            Assert.That(agent, Does.Contain("SetCommunicationActions(_ship, continuous);"));
            Assert.That(agent, Does.Contain("ClearCommunication(_ship);"));
            Assert.That(agent, Does.Contain("ShipCommunications[ship] = Vector4.zero;"));
            Assert.That(agent, Does.Contain("ShipCommunications[ship] = new Vector4("));
            Assert.That(agent, Does.Contain("ShipCommunications.Remove(ship);"));
            Assert.That(agent, Does.Contain("ShipCommunications.TryGetValue(ally, out Vector4 communication)"));
            Assert.That(perception, Does.Contain("AddAllySlots(sensor, _allyCandidates, MaxObservedAllies, origin, frameQuarterTurns);"));
            Assert.That(perception, Does.Contain("AddEntitySlots(sensor, _enemyCandidates, MaxObservedEnemies, origin, frameQuarterTurns);"),
                "Enemy observations must not receive the private allied communication tail.");
        }

        [Test]
        public void CoordinateFrameTransformsObservationsAndDirectionalActionsTogether()
        {
            string perception = Read("Scripts", "Scenes", "RlCombatPerception.cs");
            string agent = Read("Scripts", "Scenes", "RlOneVsOneAgent.cs");

            Assert.That(perception, Does.Contain("RlPolicyCoordinateFrame.WorldToPolicy("));
            Assert.That(perception, Does.Contain("WorldGridIndexForPolicyIndex("));
            Assert.That(perception, Does.Contain("AddHeading(sensor, ship.Rotation, frameQuarterTurns);"));
            Assert.That(agent, Does.Contain("CollectPolicyObservations(_perception, _ship, _side, sensor, frameQuarterTurns);"));
            Assert.That(agent, Does.Contain("perception.Collect(ship, side, sensor, frameQuarterTurns);"));
            Assert.That(agent, Does.Contain("ApplyMovementCommand(_ship, RlPolicyCoordinateFrame.PolicyToWorld(policyMovement, frameQuarterTurns));"));
            Assert.That(agent, Does.Contain("RlPolicyCoordinateFrame.EndEpisode(level);"));
        }

        [Test]
        public void CarrierAndFutureObjectiveInformationHaveDedicatedStableBlocks()
        {
            string perception = Read("Scripts", "Scenes", "RlCombatPerception.cs");

            Assert.That(perception, Does.Contain("AddParentCarrierObservations(ship, sensor, origin, frameQuarterTurns);"));
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
        public void BargeReservesRlChargePhaseBeforeWindupCanYield()
        {
            string source = Read("Scripts", "Entities", "Ships", "Barge.cs");

            int reserveMethod = source.IndexOf("internal bool TryReserveCharge()", StringComparison.Ordinal);
            Assert.That(reserveMethod, Is.GreaterThanOrEqualTo(0));
            int chargeMethod = source.IndexOf("public IEnumerator ChargeForward", reserveMethod, StringComparison.Ordinal);
            Assert.That(chargeMethod, Is.GreaterThan(reserveMethod));
            string reserve = source.Substring(reserveMethod, chargeMethod - reserveMethod);
            Assert.That(reserve, Does.Contain("SetChargePhase(1);"),
                "RL wind-up must be reserved synchronously before the charge coroutine can yield.");
            Assert.That(reserve, Does.Not.Contain("HasStartedCharging = true;"),
                "RL wind-up reservation must not change the legacy flag that marks active-charge start timing.");

            int reserveCall = source.IndexOf("if (!TryReserveCharge())", chargeMethod, StringComparison.Ordinal);
            int firstYield = source.IndexOf("yield return _chargeBuildDelay;", chargeMethod, StringComparison.Ordinal);
            int activeChargeFlag = source.IndexOf("HasStartedCharging = true;", chargeMethod, StringComparison.Ordinal);
            Assert.That(reserveCall, Is.GreaterThan(chargeMethod));
            Assert.That(firstYield, Is.GreaterThan(reserveCall));
            Assert.That(activeChargeFlag, Is.GreaterThan(firstYield),
                "HasStartedCharging must retain its historical meaning and flip only when the active charge begins.");
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
            int nextMethod = source.IndexOf("private void ResetWeaponAimDirections()", methodStart, StringComparison.Ordinal);
            Assert.That(nextMethod, Is.GreaterThan(methodStart));
            string reset = source.Substring(methodStart, nextMethod - methodStart);

            Assert.That(reset, Does.Contain("ReleaseShip();"));
            Assert.That(reset, Does.Contain("_hasBoundShip = false;"));
            Assert.That(reset, Does.Contain("_hasParticipatedThisEpisode = false;"));
            Assert.That(reset, Does.Contain("_boundRuntimeShipId = 0;"));
            Assert.That(reset, Does.Contain("_decisionCounter = 0;"));
            Assert.That(reset, Does.Contain("_nextMiningActionTime = 0f;"));
            Assert.That(reset, Does.Contain("_nextHealingActionTime = 0f;"));
            Assert.That(reset, Does.Contain("ResetWeaponAimDirections();"));
            Assert.That(source, Does.Contain("Vector2 defaultAim = RlPolicyCoordinateFrame.PolicyToWorld(Vector2.up, frameQuarterTurns);"));
        }

        [Test]
        public void SpawnedAndCapabilityOnlyShipsRemainEligibleForDynamicPolicyAgents()
        {
            string source = Read("Scripts", "Scenes", "RlOneVsOneAgent.cs");

            Assert.That(source, Does.Contain("ProvisionAgentsForSpawnedShips(_stage);"));
            Assert.That(source, Does.Contain("CountPolicyControlledShips(level, beeSide)"));
            Assert.That(source, Does.Contain("CountPolicyControlledShips(level, humanSide)"));
            Assert.That(source, Does.Contain("EnsureAgentCount(stage, level, beeSide"));
            Assert.That(source, Does.Contain("EnsureAgentCount(stage, level, humanSide"));

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
