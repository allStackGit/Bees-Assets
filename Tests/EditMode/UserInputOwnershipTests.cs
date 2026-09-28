using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class UserInputOwnershipTests
    {
        private Type _configDataType;
        private object _originalConfiguration;
        private bool _installedTestConfiguration;

        [SetUp]
        public void EnsureSideConfiguration()
        {
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _originalConfiguration = RuntimeAssembly.GetStaticField(
                _configDataType,
                "Configuration");
            _installedTestConfiguration = _originalConfiguration == null;
            if (!_installedTestConfiguration)
            {
                return;
            }

            object configuration = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.Configuration");
            RuntimeAssembly.SetField(configuration, "BeeSide", 1);
            RuntimeAssembly.SetField(configuration, "HumanSide", 2);
            RuntimeAssembly.SetField(configuration, "UserSide", 1);
            RuntimeAssembly.SetField(configuration, "AISide", 2);
            RuntimeAssembly.SetField(configuration, "MaxSquadSize", 16);
            RuntimeAssembly.SetStaticField(
                _configDataType,
                "Configuration",
                configuration);
        }

        [TearDown]
        public void RestoreSideConfiguration()
        {
            if (_installedTestConfiguration && _configDataType != null)
            {
                RuntimeAssembly.SetStaticField(
                    _configDataType,
                    "Configuration",
                    _originalConfiguration);
            }

            _configDataType = null;
            _originalConfiguration = null;
            _installedTestConfiguration = false;
        }

        [Test]
        public void RawMovementRespectsCampaignInputLock()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "LevelInputManager.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("Where(s => !s.IsLockedOn && s.CanAcceptInputFrom(PlayerId))", source);

            string squadPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.cs");
            string squadSource = File.ReadAllText(squadPath);
            StringAssert.Contains("return CanAcceptUserInput && IsOwnedByPlayer(playerId);", squadSource);
        }

        [Test]
        public void SelectedSquadInputAccessorExcludesLockedOrDeadSquads()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Selection.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("squad != null && !squad.IsDead && squad.CanAcceptInputFrom(playerId)", source);
            StringAssert.Contains("GetSelectedSquadsForPlayer(GetPrimaryInputPlayerId())", source);
        }

        [Test]
        public void MatchSessionSoloOwnerMatchesPrimaryLocalPlayer()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = RuntimeAssembly.InvokeStatic(sessionType, "CreateSolo", 1);

            Assert.That(RuntimeAssembly.Invoke(session, "GetSolePlayerIdForSide", 1), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.Invoke(session, "IsPrimaryLocalPlayer", 1), Is.EqualTo(true));
        }

        [Test]
        public void MatchSessionSameSidePlayersRequireExplicitSquadOwnership()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = Activator.CreateInstance(sessionType);

            Assert.That(RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "AddPlayer", 2, 1, false), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "GetSolePlayerIdForSide", 1), Is.EqualTo(0));
        }

        [Test]
        public void MultiplayerSessionIsFreePlayOnlyAndSquadOwnershipResetsWithPoolLifetime()
        {
            string stagePath = Path.Combine(Application.dataPath, "Scripts", "Scenes", "Stage.cs");
            string squadPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.cs");
            string stageSource = File.ReadAllText(stagePath);
            string squadSource = File.ReadAllText(squadPath);

            StringAssert.Contains("ConfigData.CurrentGameMode != ConfigData.GameModes.FreePlay", stageSource);
            StringAssert.Contains("OwnerPlayerId = MatchSession.UnownedPlayerId;", squadSource);
        }

        [Test]
        public void SameSideSquadAssignmentsResolveBeforeRuntimeOwnership()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = Activator.CreateInstance(sessionType);
            object firstSquad = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad");
            object secondSquad = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad");
            RuntimeAssembly.SetField(firstSquad, "Side", 1);
            RuntimeAssembly.SetField(secondSquad, "Side", 1);

            RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
            RuntimeAssembly.Invoke(session, "AddPlayer", 2, 1, false);

            Assert.That(RuntimeAssembly.Invoke(session, "TryAssignSavedSquadOwner", firstSquad, 1), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "TryAssignSavedSquadOwner", secondSquad, 2), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "ResolveSquadOwner", firstSquad, 1), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.Invoke(session, "ResolveSquadOwner", secondSquad, 1), Is.EqualTo(2));
        }

        [Test]
        public void SavedSquadCannotBeAssignedAcrossSides()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = Activator.CreateInstance(sessionType);
            object squad = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad");
            RuntimeAssembly.SetField(squad, "Side", 2);

            RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);

            Assert.That(RuntimeAssembly.Invoke(session, "TryAssignSavedSquadOwner", squad, 1), Is.EqualTo(false));
            Assert.That(RuntimeAssembly.Invoke(session, "ResolveSquadOwner", squad, 2), Is.EqualTo(0));
        }

        [Test]
        public void RemotePlayerOwnedSquadAcceptsOnlyItsOwnersCommandsAndIsNotHiveMindControlled()
        {
            GameObject stageObject = new GameObject("Multiplayer Ownership Stage");
            GameObject levelObject = new GameObject("Multiplayer Ownership Level");
            GameObject squadObject = new GameObject("Multiplayer Ownership Squad");
            try
            {
                Component stage = stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
                Component level = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
                Component squad = squadObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad"));
                Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
                object session = Activator.CreateInstance(sessionType);

                RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
                RuntimeAssembly.Invoke(session, "AddPlayer", 2, 2, false);
                RuntimeAssembly.SetField(stage, "MatchSession", session);
                RuntimeAssembly.SetField(level, "Stage", stage);
                RuntimeAssembly.SetField(level, "HasPlayer", true);
                RuntimeAssembly.SetField(squad, "Level", level);
                RuntimeAssembly.SetField(squad, "Stage", stage);
                RuntimeAssembly.SetField(squad, "Side", 2);

                RuntimeAssembly.Invoke(squad, "SetOwnerPlayerId", 2);

                Assert.That(RuntimeAssembly.GetField(squad, "IsPlayerControlled"), Is.True);
                Assert.That(RuntimeAssembly.GetField(squad, "IsUserControlled"), Is.False);
                Assert.That(RuntimeAssembly.GetField(squad, "IsHiveMindControlled"), Is.False);
                Assert.That(RuntimeAssembly.GetField(squad, "CanAcceptUserInput"), Is.True);
                Assert.That(RuntimeAssembly.Invoke(squad, "CanAcceptInputFrom", 2), Is.EqualTo(true));
                Assert.That(RuntimeAssembly.Invoke(squad, "CanAcceptInputFrom", 1), Is.EqualTo(false));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(squadObject);
                UnityEngine.Object.DestroyImmediate(levelObject);
                UnityEngine.Object.DestroyImmediate(stageObject);
            }
        }

        [Test]
        public void HiveMindSchedulingUsesAuthoritativePlayerOwnership()
        {
            string levelSetupPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Setup.cs");
            string commandStatePath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs");
            string levelSetupSource = File.ReadAllText(levelSetupPath);
            string commandStateSource = File.ReadAllText(commandStatePath);

            StringAssert.Contains("if (squad.IsPlayerControlled || squad.IsImmobile || squad.HasCommandQueue)", levelSetupSource);
            StringAssert.Contains("!squad.IsHiveMindControlled", commandStateSource);
            StringAssert.Contains("squad.IsHiveMindControlled", commandStateSource);
        }

        [Test]
        public void SavedSquadOwnershipSurvivesUnambiguousLevelOptionClone()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = Activator.CreateInstance(sessionType);
            object sourceSquad = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad");
            object clonedSquad = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad");
            RuntimeAssembly.SetField(sourceSquad, "Id", 77L);
            RuntimeAssembly.SetField(sourceSquad, "Side", 1);
            RuntimeAssembly.SetField(clonedSquad, "Id", 77L);
            RuntimeAssembly.SetField(clonedSquad, "Side", 1);

            RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
            RuntimeAssembly.Invoke(session, "AddPlayer", 2, 1, false);
            RuntimeAssembly.Invoke(session, "TryAssignSavedSquadOwner", sourceSquad, 2);
            RuntimeAssembly.SetField(
                clonedSquad,
                "MatchOwnershipToken",
                RuntimeAssembly.GetField(sourceSquad, "MatchOwnershipToken"));

            Assert.That(RuntimeAssembly.Invoke(session, "ResolveSquadOwner", clonedSquad, 1), Is.EqualTo(2));
        }

        [Test]
        public void UnassignedSavedSquadDoesNotGuessOwnerFromPersistentIdCollision()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = Activator.CreateInstance(sessionType);
            object firstSquad = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad");
            object secondSquad = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad");
            object clonedSquad = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad");

            foreach (object squad in new[] { firstSquad, secondSquad, clonedSquad })
            {
                RuntimeAssembly.SetField(squad, "Id", 88L);
                RuntimeAssembly.SetField(squad, "Side", 1);
            }

            RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
            RuntimeAssembly.Invoke(session, "AddPlayer", 2, 1, false);
            RuntimeAssembly.Invoke(session, "TryAssignSavedSquadOwner", firstSquad, 1);
            RuntimeAssembly.Invoke(session, "TryAssignSavedSquadOwner", secondSquad, 2);

            Assert.That(RuntimeAssembly.Invoke(session, "ResolveSquadOwner", clonedSquad, 1), Is.EqualTo(0));
        }

        [Test]
        public void NonPrimaryPlayerSelectionIsIsolatedFromPrimarySelection()
        {
            GameObject stageObject = new GameObject("Selection Ownership Stage");
            GameObject levelObject = new GameObject("Selection Ownership Level");
            GameObject stateObject = new GameObject("Selection Ownership State");
            GameObject squadObject = new GameObject("Selection Ownership Squad");
            try
            {
                Component stage = stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
                Component level = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
                Component state = stateObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
                Component squad = squadObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad"));
                Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
                object session = Activator.CreateInstance(sessionType);

                RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
                RuntimeAssembly.Invoke(session, "AddPlayer", 2, 1, false);
                RuntimeAssembly.SetField(stage, "MatchSession", session);
                RuntimeAssembly.SetField(level, "Stage", stage);
                RuntimeAssembly.SetField(level, "State", state);
                RuntimeAssembly.SetField(level, "HasPlayer", true);
                RuntimeAssembly.SetField(state, "Level", level);
                RuntimeAssembly.SetField(state, "Stage", stage);
                RuntimeAssembly.SetField(squad, "Level", level);
                RuntimeAssembly.SetField(squad, "Stage", stage);
                RuntimeAssembly.SetField(squad, "Side", 1);
                RuntimeAssembly.SetField(squad, "IsDead", false);
                RuntimeAssembly.Invoke(squad, "SetOwnerPlayerId", 2);

                RuntimeAssembly.Invoke(state, "AddSelectedSquadForPlayer", 2, squad);

                Assert.That(RuntimeAssembly.GetCount(
                    RuntimeAssembly.Invoke(state, "GetSelectedSquadsForPlayer", 2)), Is.EqualTo(1));
                Assert.That(RuntimeAssembly.GetCount(
                    RuntimeAssembly.Invoke(state, "GetSelectedSquadsForPlayer", 1)), Is.Zero);
                Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(state, "SelectedSquads")), Is.Zero);
                Assert.That(RuntimeAssembly.GetField(squad, "IsSelected"), Is.False,
                    "Legacy IsSelected remains the primary-local visual selection state.");

                RuntimeAssembly.Invoke(state, "ForgetSquadSelectionForRelease", squad);
                Assert.That(RuntimeAssembly.GetCount(
                    RuntimeAssembly.Invoke(state, "GetSelectedSquadsForPlayer", 2)), Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(squadObject);
                UnityEngine.Object.DestroyImmediate(stateObject);
                UnityEngine.Object.DestroyImmediate(levelObject);
                UnityEngine.Object.DestroyImmediate(stageObject);
            }
        }

        [Test]
        public void InputAndClickDispatchCarryIssuingPlayerIdentity()
        {
            string inputPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "LevelInputManager.cs");
            string selectorPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "Selector.cs");
            string interactionPath = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Interaction.cs");
            string inputSource = File.ReadAllText(inputPath);
            string selectorSource = File.ReadAllText(selectorPath);
            string interactionSource = File.ReadAllText(interactionPath);

            StringAssert.Contains("GetSelectedSquadsForPlayer(PlayerId)", inputSource);
            StringAssert.Contains("SelectSquadsForPlayer(PlayerId, squads)", selectorSource);
            StringAssert.Contains("GetSelectedSquadsForPlayer(playerId)", interactionSource);
            StringAssert.Contains("_lastEnemyRightClickPlayerId == playerId", interactionSource);
            StringAssert.Contains("return;", interactionSource);
        }

        [Test]
        public void SelectorStartupDoesNotDependOnLevelStateExisting()
        {
            string selectorPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "Selector.cs");
            string stagePath = Path.Combine(Application.dataPath, "Scripts", "Scenes", "Stage.cs");
            string selectorSource = File.ReadAllText(selectorPath);
            string stageSource = File.ReadAllText(stagePath);

            StringAssert.DoesNotContain("Level.State.GetPrimaryInputPlayerId()", selectorSource);
            StringAssert.Contains("Selector.Setup(PrimaryLevel, SelectionBox, primaryPlayerId)", stageSource);
        }

        [Test]
        public void PlayerCommandAuthorizationRejectsOtherAndUnknownPlayers()
        {
            GameObject stageObject = new GameObject("Command Authorization Stage");
            GameObject levelObject = new GameObject("Command Authorization Level");
            GameObject stateObject = new GameObject("Command Authorization State");
            GameObject squadObject = new GameObject("Command Authorization Squad");
            try
            {
                Component stage = stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
                Component level = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
                Component state = stateObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
                Component squad = squadObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad"));
                Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
                object session = Activator.CreateInstance(sessionType);

                RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
                RuntimeAssembly.Invoke(session, "AddPlayer", 2, 1, false);
                RuntimeAssembly.SetField(stage, "MatchSession", session);
                RuntimeAssembly.SetField(level, "Stage", stage);
                RuntimeAssembly.SetField(level, "State", state);
                RuntimeAssembly.SetField(level, "HasPlayer", true);
                RuntimeAssembly.SetField(state, "Level", level);
                RuntimeAssembly.SetField(state, "Stage", stage);
                RuntimeAssembly.SetField(squad, "Level", level);
                RuntimeAssembly.SetField(squad, "Stage", stage);
                RuntimeAssembly.SetField(squad, "Side", 1);
                RuntimeAssembly.SetField(squad, "IsDead", false);
                RuntimeAssembly.Invoke(squad, "SetOwnerPlayerId", 1);
                RuntimeAssembly.AddToCollection(RuntimeAssembly.GetField(state, "Squads"), squad);

                Assert.That(RuntimeAssembly.Invoke(state, "CanPlayerCommandSquad", 1, squad), Is.EqualTo(true));
                Assert.That(RuntimeAssembly.Invoke(state, "CanPlayerCommandSquad", 2, squad), Is.EqualTo(false));
                Assert.That(RuntimeAssembly.Invoke(state, "CanPlayerCommandSquad", 999, squad), Is.EqualTo(false));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(squadObject);
                UnityEngine.Object.DestroyImmediate(stateObject);
                UnityEngine.Object.DestroyImmediate(levelObject);
                UnityEngine.Object.DestroyImmediate(stageObject);
            }
        }

        [Test]
        public void UnknownPlayerSelectionDoesNotCreateSelectionRegistry()
        {
            GameObject stageObject = new GameObject("Unknown Selection Stage");
            GameObject levelObject = new GameObject("Unknown Selection Level");
            GameObject stateObject = new GameObject("Unknown Selection State");
            try
            {
                Component stage = stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
                Component level = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
                Component state = stateObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
                Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
                object session = Activator.CreateInstance(sessionType);

                RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
                RuntimeAssembly.SetField(stage, "MatchSession", session);
                RuntimeAssembly.SetField(level, "Stage", stage);
                RuntimeAssembly.SetField(state, "Level", level);
                RuntimeAssembly.SetField(state, "Stage", stage);

                object result = RuntimeAssembly.Invoke(state, "GetSelectedSquadsForPlayer", 999);

                Assert.That(RuntimeAssembly.GetCount(result), Is.Zero);
                Assert.That(RuntimeAssembly.GetCount(
                    RuntimeAssembly.GetField(state, "_selectedSquadsByNonPrimaryPlayer")), Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(stateObject);
                UnityEngine.Object.DestroyImmediate(levelObject);
                UnityEngine.Object.DestroyImmediate(stageObject);
            }
        }

        [Test]
        public void LocalMovementUsesSameCommandEnvelopeAsRemoteCommands()
        {
            string inputPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "LevelInputManager.cs");
            string commandPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs");
            string inputSource = File.ReadAllText(inputPath);
            string commandSource = File.ReadAllText(commandPath);

            StringAssert.Contains("Level.State.TryIssuePlayerCommand(", inputSource);
            StringAssert.Contains("PlayerCommandKind.Move", inputSource);
            StringAssert.Contains("return TryPlayerMoveSquad(", commandSource);
            StringAssert.DoesNotContain("_moveSquads_selectedSquads[_moveSquads_i].Move(", inputSource);
        }

        [Test]
        public void PlayerOwnedCommandCompletionClearsLockRegardlessOfLocalPerspective()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Command.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("GetSquad().IsPlayerControlled && GetSquad().IsLockedOn", source);
            StringAssert.DoesNotContain("GetSquad().IsUserControlled && GetSquad().IsLockedOn", source);
        }

        [Test]
        public void TacticalInputUsesPlayerCommandEnvelope()
        {
            string inputPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "LevelInputManager.cs");
            string commandPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs");
            string inputSource = File.ReadAllText(inputPath);
            string commandSource = File.ReadAllText(commandPath);

            StringAssert.Contains("PlayerCommandKind.Guard", inputSource);
            StringAssert.Contains("PlayerCommandKind.Patrol", inputSource);
            StringAssert.Contains("PlayerCommandKind.FullRetreat", inputSource);
            StringAssert.Contains("PlayerCommandKind.Heal", inputSource);
            StringAssert.Contains("return TryPlayerGuardSquad(", commandSource);
            StringAssert.Contains("return TryPlayerPatrolSquad(", commandSource);
            StringAssert.Contains("return TryPlayerFullRetreat(", commandSource);
            StringAssert.Contains("return TryPlayerHealSquad(", commandSource);
            StringAssert.DoesNotContain("squad.UserGuard(ship.Squad)", inputSource);
            StringAssert.DoesNotContain("squad.UserPatrol(_checkForSelectingPatrolArea_startingPosition", inputSource);
        }

        [Test]
        public void MatchSquadIdsAreUniqueWithinSession()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = Activator.CreateInstance(sessionType);

            Assert.That(RuntimeAssembly.Invoke(session, "AllocateMatchSquadId"), Is.EqualTo(1L));
            Assert.That(RuntimeAssembly.Invoke(session, "AllocateMatchSquadId"), Is.EqualTo(2L));
            Assert.That(RuntimeAssembly.Invoke(session, "AllocateMatchSquadId"), Is.EqualTo(3L));
        }

        [Test]
        public void MatchSquadIdentityResetsOnPooledSquadLifetime()
        {
            string squadPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.cs");
            string squadSource = File.ReadAllText(squadPath);

            StringAssert.Contains("MatchSquadId = 0;", squadSource);
            StringAssert.Contains("MatchSquadId = matchSession == null ? 0 : matchSession.AllocateMatchSquadId();", squadSource);
            StringAssert.Contains("public long CommandSquadId => MatchSquadId != 0 ? MatchSquadId : ItemId;", squadSource);
        }

        [Test]
        public void PlayerCommandBoundaryUsesMatchScopedSquadIdentity()
        {
            string commandPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs");
            string inputPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "LevelInputManager.cs");
            string interactionPath = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Interaction.cs");
            string commandSource = File.ReadAllText(commandPath);
            string inputSource = File.ReadAllText(inputPath);
            string interactionSource = File.ReadAllText(interactionPath);

            StringAssert.Contains("squad.CommandSquadId == squadCommandId", commandSource);
            StringAssert.Contains(".CommandSquadId", inputSource);
            StringAssert.Contains("selectedSquad.CommandSquadId", interactionSource);
            StringAssert.Contains("Squad.CommandSquadId", interactionSource);
        }

        [Test]
        public void MatchLobbyLocksPlayerAndLoadoutConfigurationOnceBattleBegins()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = Activator.CreateInstance(sessionType);
            object squad = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad");
            RuntimeAssembly.SetField(squad, "Side", 1);

            Assert.That(RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "AddPlayer", 2, 2, false), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "TryAssignSavedSquadOwner", squad, 1), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "TryBeginBattle"), Is.EqualTo(true));

            Assert.That(RuntimeAssembly.Invoke(session, "AddPlayer", 3, 1, false), Is.EqualTo(false));
            Assert.That(RuntimeAssembly.Invoke(session, "RemovePlayer", 2), Is.EqualTo(false));
            Assert.That(RuntimeAssembly.Invoke(session, "TrySetPlayerSide", 1, 2), Is.EqualTo(false));
            Assert.That(RuntimeAssembly.Invoke(session, "TryAssignSavedSquadOwner", squad, 1), Is.EqualTo(false));
            Assert.That(RuntimeAssembly.Invoke(session, "EndBattle"), Is.EqualTo(true));
        }

        [Test]
        public void MatchCommandSequenceRejectsDuplicatesAndNonBattleCommands()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = Activator.CreateInstance(sessionType);

            RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
            Assert.That(RuntimeAssembly.Invoke(session, "AllocatePlayerCommandSequence", 1), Is.EqualTo(0L));
            Assert.That(RuntimeAssembly.Invoke(session, "TryAcceptPlayerCommandSequence", 1, 1L), Is.EqualTo(false));

            Assert.That(RuntimeAssembly.Invoke(session, "TryBeginBattle"), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "AllocatePlayerCommandSequence", 1), Is.EqualTo(1L));
            Assert.That(RuntimeAssembly.Invoke(session, "AllocatePlayerCommandSequence", 1), Is.EqualTo(2L));
            Assert.That(RuntimeAssembly.Invoke(session, "TryAcceptPlayerCommandSequence", 1, 1L), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "TryAcceptPlayerCommandSequence", 1, 1L), Is.EqualTo(false));
            Assert.That(RuntimeAssembly.Invoke(session, "TryAcceptPlayerCommandSequence", 1, 3L), Is.EqualTo(false));
            Assert.That(RuntimeAssembly.Invoke(session, "TryAcceptPlayerCommandSequence", 1, 2L), Is.EqualTo(true));
        }

        [Test]
        public void FreePlayMatchSessionHandoffIsOneShotAndSoloEntryClearsStaleLobby()
        {
            string configPath = Path.Combine(Application.dataPath, "Scripts", "ConfigData.cs");
            string stagePath = Path.Combine(Application.dataPath, "Scripts", "Scenes", "Stage.cs");
            string menuPath = Path.Combine(Application.dataPath, "Scripts", "Scenes", "MainMenu.cs");
            string configSource = File.ReadAllText(configPath);
            string stageSource = File.ReadAllText(stagePath);
            string menuSource = File.ReadAllText(menuPath);

            StringAssert.Contains("_pendingFreePlayMatchSession = null;", configSource);
            StringAssert.Contains("ConfigData.ConsumePendingFreePlayMatchSession()", stageSource);
            StringAssert.Contains("ConfigData.ClearPendingFreePlayMatchSession();", menuSource);
            StringAssert.Contains("MatchSession.TryBeginBattle()", stageSource);
        }

        [Test]
        public void PlayerCommandEnvelopeIsSequencedAndDispatchesOnlyThroughAuthorizedGateways()
        {
            string commandPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs");
            string source = File.ReadAllText(commandPath);

            StringAssert.Contains("[Serializable]", source);
            StringAssert.Contains("public long Sequence;", source);
            StringAssert.Contains("TryAcceptPlayerCommandSequence(command.PlayerId, command.Sequence)", source);
            StringAssert.Contains("return TryPlayerMoveSquad(", source);
            StringAssert.Contains("return TryPlayerTargetEnemy(", source);
            StringAssert.Contains("return TryPlayerGuardSquad(", source);
            StringAssert.Contains("return TryPlayerPatrolSquad(", source);
            StringAssert.Contains("return TryPlayerFullRetreat(", source);
            StringAssert.Contains("return TryPlayerHealSquad(", source);
        }

        [Test]
        public void ReceivedPlayerCommandsAreCopiedBoundedAndDrainedOnMainThread()
        {
            string commandPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs");
            string stagePath = Path.Combine(Application.dataPath, "Scripts", "Scenes", "Stage.cs");
            string statePath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.cs");
            string commandSource = File.ReadAllText(commandPath);
            string stageSource = File.ReadAllText(stagePath);
            string stateSource = File.ReadAllText(statePath);

            StringAssert.Contains("public const int MaxQueuedPlayerCommands = 1024;", commandSource);
            StringAssert.Contains("new PlayerCommandEnvelope(", commandSource);
            StringAssert.Contains("lock (_queuedPlayerCommandsLock)", commandSource);
            StringAssert.Contains("ProcessQueuedPlayerCommands()", stageSource);
            StringAssert.Contains("ClearQueuedPlayerCommands();", stateSource);
        }

        [Test]
        public void ReceivedPlayerCommandQueueRejectsMalformedEnvelopeBeforeQueueing()
        {
            GameObject stateObject = new GameObject("Queued Command State");
            try
            {
                Component state = stateObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
                Type commandType = RuntimeAssembly.GetType("Assets.Scripts.Levels.PlayerCommandEnvelope");
                object command = Activator.CreateInstance(commandType);

                RuntimeAssembly.SetField(command, "PlayerId", 0);
                RuntimeAssembly.SetField(command, "Sequence", 1L);
                RuntimeAssembly.SetField(command, "SquadCommandId", 1L);

                Assert.That(RuntimeAssembly.Invoke(state, "QueueReceivedPlayerCommand", 10, command), Is.EqualTo(false));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(stateObject);
            }
        }

        [Test]
        public void RuntimeShipsInheritPlayerAndHiveMindOwnershipFromSquadBeforeRegistration()
        {
            string lifecyclePath = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Lifecycle.cs");
            string source = File.ReadAllText(lifecyclePath);

            int playerControlledIndex = source.IndexOf("IsPlayerControlled = squad.IsPlayerControlled;");
            int userControlledIndex = source.IndexOf("IsUserControlled = squad.IsUserControlled;");
            int hiveMindControlledIndex = source.IndexOf("IsHiveMindControlled = Stage.IsTrainingNueralNetwork || !IsPlayerControlled;");
            int addShipIndex = source.IndexOf("Level.State.AddShip(this);");

            Assert.That(playerControlledIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(userControlledIndex, Is.GreaterThan(playerControlledIndex));
            Assert.That(hiveMindControlledIndex, Is.GreaterThan(userControlledIndex));
            Assert.That(addShipIndex, Is.GreaterThan(hiveMindControlledIndex),
                "Ship ownership must be correct before GameState decides whether to register Hive Mind vision.");
        }

        [Test]
        public void FreePlaySessionAndPrimarySideResolveBeforePoolCreatesShips()
        {
            string stagePath = Path.Combine(Application.dataPath, "Scripts", "Scenes", "Stage.cs");
            string source = File.ReadAllText(stagePath);

            int sessionIndex = source.IndexOf("SetupMatchSession();");
            int poolIndex = source.IndexOf("Pool.Setup(this);");

            Assert.That(sessionIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(poolIndex, Is.GreaterThan(sessionIndex));
            StringAssert.Contains("primaryLocalSide != ConfigData.Configuration.UserSide", source);
        }

        [Test]
        public void ShipPlayerControlStateResetsAcrossPooledLifetimes()
        {
            string lifecyclePath = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Lifecycle.cs");
            string source = File.ReadAllText(lifecyclePath);

            StringAssert.Contains("IsPlayerControlled = false;", source);
            StringAssert.Contains("IsUserControlled = false;", source);
            StringAssert.Contains("IsHiveMindControlled = false;", source);
        }

        [Test]
        public void MatchOwnershipTokenIsTransientAndNotPersisted()
        {
            string savedSquadPath = Path.Combine(Application.dataPath, "Scripts", "Data", "SavedSquad.cs");
            string source = File.ReadAllText(savedSquadPath);
            int toJsonIndex = source.IndexOf("public string ToJson()");
            Assert.That(toJsonIndex, Is.GreaterThanOrEqualTo(0));

            string toJsonSource = source.Substring(toJsonIndex);
            StringAssert.Contains("public Guid MatchOwnershipToken;", source);
            StringAssert.DoesNotContain("[\"MatchOwnershipToken\"]", toJsonSource);
        }

        [Test]
        public void MultipleLocalPlayersShareOneTransportPeer()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = Activator.CreateInstance(sessionType);

            Assert.That(RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "AddPlayer", 2, 1, true), Is.EqualTo(true));

            Assert.That(RuntimeAssembly.Invoke(session, "GetPlayerPeerId", 1), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.Invoke(session, "GetPlayerPeerId", 2), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.Invoke(session, "DoesPeerOwnPlayer", 1, 1), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "DoesPeerOwnPlayer", 1, 2), Is.EqualTo(true));
        }

        [Test]
        public void RemotePeerCannotClaimAnotherPeersPlayerId()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = Activator.CreateInstance(sessionType);

            Assert.That(RuntimeAssembly.Invoke(session, "AddPeer", 10, false, "remote-a"), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "AddPeer", 11, false, "remote-b"), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "AddPlayerToPeer", 2, 1, 10), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "AddPlayerToPeer", 3, 2, 11), Is.EqualTo(true));

            Assert.That(RuntimeAssembly.Invoke(session, "DoesPeerOwnPlayer", 10, 2), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "DoesPeerOwnPlayer", 10, 3), Is.EqualTo(false));
            Assert.That(RuntimeAssembly.Invoke(session, "DoesPeerOwnPlayer", 11, 2), Is.EqualTo(false));
        }

        [Test]
        public void ReceivedCommandExecutionChecksSourcePeerBeforeRawEnvelopeExecution()
        {
            string commandPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs");
            string source = File.ReadAllText(commandPath);

            StringAssert.Contains("TryExecuteReceivedPlayerCommand(queuedCommand.SourcePeerId, queuedCommand.Command)", source);
            StringAssert.Contains("!matchSession.DoesPeerOwnPlayer(sourcePeerId, command.PlayerId)", source);
            StringAssert.Contains("private bool TryExecutePlayerCommand(PlayerCommandEnvelope command)", source);
        }

        [Test]
        public void MatchIdCanBeAdoptedOnlyBeforeBattle()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = Activator.CreateInstance(sessionType);
            Guid adopted = Guid.NewGuid();
            PropertyInfo matchIdProperty = sessionType.GetProperty(
                "MatchId",
                BindingFlags.Instance | BindingFlags.Public);

            Assert.That(matchIdProperty, Is.Not.Null);
            Assert.That(RuntimeAssembly.Invoke(session, "TrySetMatchId", adopted), Is.EqualTo(true));
            Assert.That(matchIdProperty.GetValue(session), Is.EqualTo(adopted));
            Assert.That(RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "TryBeginBattle"), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "TrySetMatchId", Guid.NewGuid()), Is.EqualTo(false));
            Assert.That(matchIdProperty.GetValue(session), Is.EqualTo(adopted));
        }

        [Test]
        public void MultiplayerCommandProtocolRoundTripsOnlyForExpectedMatch()
        {
            Type protocolType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MultiplayerProtocol");
            Type commandType = RuntimeAssembly.GetType("Assets.Scripts.Levels.PlayerCommandEnvelope");
            Type kindType = RuntimeAssembly.GetType("Assets.Scripts.Levels.PlayerCommandKind");
            object command = Activator.CreateInstance(commandType);
            RuntimeAssembly.SetField(command, "PlayerId", 2);
            RuntimeAssembly.SetField(command, "Sequence", 7L);
            RuntimeAssembly.SetField(command, "Kind", Enum.Parse(kindType, "Move"));
            RuntimeAssembly.SetField(command, "SquadCommandId", 41L);
            RuntimeAssembly.SetField(command, "TargetSquadCommandId", 0L);
            RuntimeAssembly.SetField(command, "PointA", new Vector2(12.5f, -9f));
            RuntimeAssembly.SetField(command, "PointB", Vector2.zero);

            Guid matchId = Guid.NewGuid();
            MethodInfo serialize = protocolType.GetMethod("TrySerializeCommand", BindingFlags.Public | BindingFlags.Static);
            object[] serializeArgs = { matchId, 3, command, null };
            Assert.That((bool)serialize.Invoke(null, serializeArgs), Is.True);
            byte[] payload = (byte[])serializeArgs[3];
            Assert.That(payload, Is.Not.Null.And.Not.Empty);

            MethodInfo deserialize = protocolType.GetMethod("TryDeserializeCommand", BindingFlags.Public | BindingFlags.Static);
            object[] deserializeArgs = { payload, matchId, 0, null };
            Assert.That((bool)deserialize.Invoke(null, deserializeArgs), Is.True);
            Assert.That(deserializeArgs[2], Is.EqualTo(3));
            object parsed = deserializeArgs[3];
            Assert.That(RuntimeAssembly.GetField(parsed, "PlayerId"), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetField(parsed, "Sequence"), Is.EqualTo(7L));
            Assert.That(RuntimeAssembly.GetField(parsed, "SquadCommandId"), Is.EqualTo(41L));

            object[] wrongMatchArgs = { payload, Guid.NewGuid(), 0, null };
            Assert.That((bool)deserialize.Invoke(null, wrongMatchArgs), Is.False);
        }

        [Test]
        public void MultiplayerCommandProtocolRejectsOversizedAndUnknownFieldPackets()
        {
            Type protocolType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MultiplayerProtocol");
            MethodInfo deserialize = protocolType.GetMethod("TryDeserializeCommand", BindingFlags.Public | BindingFlags.Static);
            Guid matchId = Guid.NewGuid();

            byte[] oversized = new byte[4097];
            object[] oversizedArgs = { oversized, matchId, 0, null };
            Assert.That((bool)deserialize.Invoke(null, oversizedArgs), Is.False);

            string unknownFieldJson =
                "{\"v\":1,\"match\":\"" + matchId.ToString("N") +
                "\",\"type\":\"command\",\"level\":1,\"player\":2,\"seq\":1,\"kind\":0," +
                "\"squad\":1,\"target\":0,\"ax\":0,\"ay\":0,\"bx\":0,\"by\":0,\"extra\":1}";
            object[] unknownArgs = { System.Text.Encoding.UTF8.GetBytes(unknownFieldJson), matchId, 0, null };
            Assert.That((bool)deserialize.Invoke(null, unknownArgs), Is.False);
        }

        [Test]
        public void ReceivedPacketMustPassProtocolBeforeEnteringPeerCommandQueue()
        {
            string commandPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs");
            string source = File.ReadAllText(commandPath);

            StringAssert.Contains("MultiplayerProtocol.TryDeserializeCommand(payload, MatchId", source);
            StringAssert.Contains("return QueueReceivedPlayerCommand(sourcePeerId, command);", source);
            StringAssert.Contains("public const int MaxPacketBytes = 4096;", source);
        }

        [Test]
        public void AuthorityPeerCanBeRemoteButFreezesAtBattleStart()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = Activator.CreateInstance(sessionType);

            Assert.That(RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "AddPeer", 10, false, "host"), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "TrySetAuthorityPeer", 10), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "TryBeginBattle"), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(session, "TrySetAuthorityPeer", 1), Is.EqualTo(false));

            PropertyInfo authorityProperty = sessionType.GetProperty(
                "AuthorityPeerId",
                BindingFlags.Instance | BindingFlags.Public);
            PropertyInfo localAuthorityProperty = sessionType.GetProperty(
                "IsLocalAuthority",
                BindingFlags.Instance | BindingFlags.Public);

            Assert.That(authorityProperty.GetValue(session), Is.EqualTo(10));
            Assert.That(localAuthorityProperty.GetValue(session), Is.EqualTo(false));
        }

        [Test]
        public void NonAuthoritativeLocalOrdersQueueForHostInsteadOfMutatingGameplay()
        {
            GameObject stageObject = new GameObject("Client Authority Stage");
            GameObject stateObject = new GameObject("Client Authority State");
            try
            {
                Component stage = stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
                Component state = stateObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
                Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
                object session = Activator.CreateInstance(sessionType);

                RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
                RuntimeAssembly.Invoke(session, "AddPeer", 10, false, "host");
                RuntimeAssembly.Invoke(session, "TrySetAuthorityPeer", 10);
                RuntimeAssembly.Invoke(session, "TryBeginBattle");
                RuntimeAssembly.SetField(stage, "MatchSession", session);
                RuntimeAssembly.SetField(state, "Stage", stage);
                RuntimeAssembly.SetField(state, "<MatchLevelId>k__BackingField", 1);

                Type kindType = RuntimeAssembly.GetType("Assets.Scripts.Levels.PlayerCommandKind");
                object moveKind = Enum.Parse(kindType, "Move");
                Assert.That(RuntimeAssembly.Invoke(
                    state,
                    "TryIssuePlayerCommand",
                    1,
                    moveKind,
                    99L,
                    0L,
                    new Vector2(2f, 3f),
                    Vector2.zero), Is.EqualTo(true));

                object outgoing = RuntimeAssembly.GetField(session, "_outgoingPlayerCommands");
                Assert.That(RuntimeAssembly.GetCount(outgoing), Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(stateObject);
                UnityEngine.Object.DestroyImmediate(stageObject);
            }
        }

        [Test]
        public void NonAuthoritativePeerCannotExecuteReceivedPlayerCommands()
        {
            GameObject stageObject = new GameObject("Client Receive Stage");
            GameObject stateObject = new GameObject("Client Receive State");
            try
            {
                Component stage = stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
                Component state = stateObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
                Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
                object session = Activator.CreateInstance(sessionType);

                RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
                RuntimeAssembly.Invoke(session, "AddPeer", 10, false, "host");
                RuntimeAssembly.Invoke(session, "AddPlayerToPeer", 2, 2, 10);
                RuntimeAssembly.Invoke(session, "TrySetAuthorityPeer", 10);
                RuntimeAssembly.Invoke(session, "TryBeginBattle");
                RuntimeAssembly.SetField(stage, "MatchSession", session);
                RuntimeAssembly.SetField(state, "Stage", stage);

                Type commandType = RuntimeAssembly.GetType("Assets.Scripts.Levels.PlayerCommandEnvelope");
                Type kindType = RuntimeAssembly.GetType("Assets.Scripts.Levels.PlayerCommandKind");
                object command = Activator.CreateInstance(commandType);
                RuntimeAssembly.SetField(command, "PlayerId", 2);
                RuntimeAssembly.SetField(command, "Sequence", 1L);
                RuntimeAssembly.SetField(command, "Kind", Enum.Parse(kindType, "Move"));
                RuntimeAssembly.SetField(command, "SquadCommandId", 9L);

                Assert.That(RuntimeAssembly.Invoke(
                    state,
                    "TryExecuteReceivedPlayerCommand",
                    10,
                    command), Is.EqualTo(false));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(stateObject);
                UnityEngine.Object.DestroyImmediate(stageObject);
            }
        }

        [Test]
        public void OutgoingClientCommandQueueIsMatchOwnedBoundedAndLevelResetAware()
        {
            string commandPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs");
            string statePath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.cs");
            string commandSource = File.ReadAllText(commandPath);
            string stateSource = File.ReadAllText(statePath);

            StringAssert.Contains("matchSession.QueueOutgoingPlayerCommand(MatchLevelId, command)", commandSource);
            StringAssert.Contains("public const int MaxOutgoingPlayerCommands = 1024;", stateSource);
            StringAssert.Contains("TryDequeueOutgoingPlayerCommand(", stateSource);
            StringAssert.Contains("RemoveOutgoingPlayerCommandsForLevel(MatchLevelId)", stateSource);
        }

        [Test]
        public void SteamReceiveUsesFixedArraySizedBatches()
        {
            string steamPath = Path.Combine(Application.dataPath, "Scripts", "Steamworks.NET", "SteamManager.cs");
            string source = File.ReadAllText(steamPath);

            StringAssert.Contains("ReceiveMessagesOnChannel(", source);
            StringAssert.Contains("_receivePointers,", source);
            StringAssert.Contains("ReceiveBatchSize);", source);
            StringAssert.DoesNotContain("_receivePointers,\n                    maxBatch", source);
        }

        [Test]
        public void MatchLevelIdsAreAssignedBeforeGameStateCapturesRoutingIdentity()
        {
            string stagePath = Path.Combine(Application.dataPath, "Scripts", "Scenes", "Stage.cs");
            string levelPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.cs");
            string statePath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.cs");
            string stageSource = File.ReadAllText(stagePath);
            string levelSource = File.ReadAllText(levelPath);
            string stateSource = File.ReadAllText(statePath);

            StringAssert.Contains("Levels[_setup_i].Setup(this, $\"Level - #{_setup_i}\", _setup_i + 1);", stageSource);
            StringAssert.Contains("MatchLevelId = matchLevelId > 0 ? matchLevelId : 1;", levelSource);
            StringAssert.Contains("MatchLevelId = Level != null ? Level.MatchLevelId : 0;", stateSource);
        }

        [Test]
        public void StageRoutesDecodedPacketToMatchingLevelBeforeQueueAdmission()
        {
            string stagePath = Path.Combine(Application.dataPath, "Scripts", "Scenes", "Stage.cs");
            string source = File.ReadAllText(stagePath);

            StringAssert.Contains("out int matchLevelId", source);
            StringAssert.Contains("level.State.MatchLevelId == matchLevelId", source);
            StringAssert.Contains("return level.State.QueueReceivedPlayerCommand(sourcePeerId, command);", source);
        }

        [Test]
        public void ClientPendingCommandsRemainUntilCumulativeAuthorityAck()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            Type commandType = RuntimeAssembly.GetType("Assets.Scripts.Levels.PlayerCommandEnvelope");
            Type kindType = RuntimeAssembly.GetType("Assets.Scripts.Levels.PlayerCommandKind");
            object session = Activator.CreateInstance(sessionType);

            RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
            RuntimeAssembly.Invoke(session, "AddPeer", 10, false, "host");
            RuntimeAssembly.Invoke(session, "TrySetAuthorityPeer", 10);
            RuntimeAssembly.Invoke(session, "TryBeginBattle");

            for (long sequence = 1; sequence <= 2; sequence++)
            {
                object command = Activator.CreateInstance(commandType);
                RuntimeAssembly.SetField(command, "PlayerId", 1);
                RuntimeAssembly.SetField(command, "Sequence", sequence);
                RuntimeAssembly.SetField(command, "Kind", Enum.Parse(kindType, "Move"));
                RuntimeAssembly.SetField(command, "SquadCommandId", 20L + sequence);
                Assert.That(RuntimeAssembly.Invoke(
                    session,
                    "QueueOutgoingPlayerCommand",
                    1,
                    command), Is.EqualTo(true));
            }

            object pending = RuntimeAssembly.GetField(session, "_outgoingPlayerCommands");
            Assert.That(RuntimeAssembly.GetCount(pending), Is.EqualTo(2));

            RuntimeAssembly.Invoke(session, "AcknowledgeOutgoingPlayerCommands", 1, 1L);
            Assert.That(RuntimeAssembly.GetCount(pending), Is.EqualTo(1));

            RuntimeAssembly.Invoke(session, "AcknowledgeOutgoingPlayerCommands", 1, 2L);
            Assert.That(RuntimeAssembly.GetCount(pending), Is.Zero);
        }

        [Test]
        public void DuplicateReceivedCommandIsReacknowledgedWithoutGameplayMutation()
        {
            GameObject stageObject = new GameObject("Duplicate Ack Stage");
            GameObject stateObject = new GameObject("Duplicate Ack State");
            try
            {
                Component stage = stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
                Component state = stateObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
                Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
                Type commandType = RuntimeAssembly.GetType("Assets.Scripts.Levels.PlayerCommandEnvelope");
                Type kindType = RuntimeAssembly.GetType("Assets.Scripts.Levels.PlayerCommandKind");
                object session = Activator.CreateInstance(sessionType);

                RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
                RuntimeAssembly.Invoke(session, "AddPeer", 10, false, "client");
                RuntimeAssembly.Invoke(session, "AddPlayerToPeer", 2, 2, 10);
                RuntimeAssembly.Invoke(session, "TryBeginBattle");
                Assert.That(RuntimeAssembly.Invoke(
                    session,
                    "TryAcceptPlayerCommandSequence",
                    2,
                    1L), Is.EqualTo(true));

                RuntimeAssembly.SetField(stage, "MatchSession", session);
                RuntimeAssembly.SetField(state, "Stage", stage);

                object duplicate = Activator.CreateInstance(commandType);
                RuntimeAssembly.SetField(duplicate, "PlayerId", 2);
                RuntimeAssembly.SetField(duplicate, "Sequence", 1L);
                RuntimeAssembly.SetField(duplicate, "Kind", Enum.Parse(kindType, "Move"));
                RuntimeAssembly.SetField(duplicate, "SquadCommandId", 50L);

                Assert.That(RuntimeAssembly.Invoke(
                    state,
                    "TryExecuteReceivedPlayerCommand",
                    10,
                    duplicate), Is.EqualTo(true));

                object acknowledgements = RuntimeAssembly.GetField(
                    session,
                    "_outgoingCommandAcknowledgements");
                Assert.That(RuntimeAssembly.GetCount(acknowledgements), Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(stateObject);
                UnityEngine.Object.DestroyImmediate(stageObject);
            }
        }

        [Test]
        public void MultiplayerAcknowledgementProtocolIsMatchBound()
        {
            Type protocolType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MultiplayerProtocol");
            MethodInfo serialize = protocolType.GetMethod(
                "TrySerializeAcknowledgement",
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo deserialize = protocolType.GetMethod(
                "TryDeserializeAcknowledgement",
                BindingFlags.Public | BindingFlags.Static);
            Guid matchId = Guid.NewGuid();

            object[] serializeArgs = { matchId, 2, 17L, null };
            Assert.That((bool)serialize.Invoke(null, serializeArgs), Is.True);
            byte[] payload = (byte[])serializeArgs[3];

            object[] deserializeArgs = { payload, matchId, 0, 0L };
            Assert.That((bool)deserialize.Invoke(null, deserializeArgs), Is.True);
            Assert.That(deserializeArgs[2], Is.EqualTo(2));
            Assert.That(deserializeArgs[3], Is.EqualTo(17L));

            object[] wrongMatchArgs = { payload, Guid.NewGuid(), 0, 0L };
            Assert.That((bool)deserialize.Invoke(null, wrongMatchArgs), Is.False);
        }

        [Test]
        public void SteamTransportRetriesUnacknowledgedCommandsAndTrustsOnlyAuthorityAcks()
        {
            string steamPath = Path.Combine(Application.dataPath, "Scripts", "Steamworks.NET", "SteamManager.cs");
            string source = File.ReadAllText(steamPath);

            StringAssert.Contains("CommandResendIntervalSeconds = 0.5f", source);
            StringAssert.Contains("CopyOutgoingPlayerCommands(", source);
            StringAssert.Contains("_lastCommandSendTimes", source);
            StringAssert.Contains("TrySerializeAcknowledgement(", source);
            StringAssert.Contains("sourcePeerId == _session.AuthorityPeerId", source);
            StringAssert.Contains("AcknowledgeOutgoingPlayerCommands(playerId, sequence)", source);
        }

        [Test]
        public void SpoofedPeerPlayerCommandIsRejectedBeforeQueueAdmission()
        {
            GameObject stageObject = new GameObject("Spoof Queue Stage");
            GameObject stateObject = new GameObject("Spoof Queue State");
            try
            {
                Component stage = stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
                Component state = stateObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
                Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
                Type commandType = RuntimeAssembly.GetType("Assets.Scripts.Levels.PlayerCommandEnvelope");
                Type kindType = RuntimeAssembly.GetType("Assets.Scripts.Levels.PlayerCommandKind");
                object session = Activator.CreateInstance(sessionType);

                RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
                RuntimeAssembly.Invoke(session, "AddPeer", 10, false, "peer-a");
                RuntimeAssembly.Invoke(session, "AddPeer", 11, false, "peer-b");
                RuntimeAssembly.Invoke(session, "AddPlayerToPeer", 2, 1, 10);
                RuntimeAssembly.Invoke(session, "AddPlayerToPeer", 3, 2, 11);
                RuntimeAssembly.Invoke(session, "TryBeginBattle");
                RuntimeAssembly.SetField(stage, "MatchSession", session);
                RuntimeAssembly.SetField(state, "Stage", stage);

                object command = Activator.CreateInstance(commandType);
                RuntimeAssembly.SetField(command, "PlayerId", 3);
                RuntimeAssembly.SetField(command, "Sequence", 1L);
                RuntimeAssembly.SetField(command, "Kind", Enum.Parse(kindType, "Move"));
                RuntimeAssembly.SetField(command, "SquadCommandId", 9L);

                Assert.That(RuntimeAssembly.Invoke(
                    state,
                    "QueueReceivedPlayerCommand",
                    10,
                    command), Is.EqualTo(false));
                Assert.That(RuntimeAssembly.GetCount(
                    RuntimeAssembly.GetField(state, "_queuedPlayerCommands")), Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(stateObject);
                UnityEngine.Object.DestroyImmediate(stageObject);
            }
        }

        [Test]
        public void SteamResendBookkeepingPrunesCommandsRemovedByLifecycle()
        {
            string steamPath = Path.Combine(Application.dataPath, "Scripts", "Steamworks.NET", "SteamManager.cs");
            string source = File.ReadAllText(steamPath);

            StringAssert.Contains("PruneCommandSendTimes();", source);
            StringAssert.Contains("_pendingCommandKeys", source);
            StringAssert.Contains("if (!_pendingCommandKeys.Contains(sent.Key))", source);
        }

        [Test]
        public void LobbySnapshotReconstructsLocalPerspectiveFromTransportIdentity()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object host = Activator.CreateInstance(sessionType);

            RuntimeAssembly.Invoke(host, "AddPlayer", 1, 1, true);
            RuntimeAssembly.Invoke(host, "TrySetPeerTransportIdentity", 1, "steam:host");
            RuntimeAssembly.Invoke(host, "AddPeer", 10, false, "steam:client");
            RuntimeAssembly.Invoke(host, "AddPlayerToPeer", 2, 2, 10);

            MethodInfo createSnapshot = sessionType.GetMethod(
                "TryCreateLobbySnapshot",
                BindingFlags.Instance | BindingFlags.Public);
            object[] snapshotArgs = { null };
            Assert.That((bool)createSnapshot.Invoke(host, snapshotArgs), Is.True);
            object snapshot = snapshotArgs[0];

            MethodInfo createSession = sessionType.GetMethod(
                "TryCreateFromLobbySnapshot",
                BindingFlags.Static | BindingFlags.Public);
            object[] clientArgs = { snapshot, "steam:client", null };
            Assert.That((bool)createSession.Invoke(null, clientArgs), Is.True);
            object client = clientArgs[2];

            Assert.That(RuntimeAssembly.Invoke(client, "IsLocalPlayer", 2), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(client, "IsLocalPlayer", 1), Is.EqualTo(false));

            PropertyInfo authority = sessionType.GetProperty(
                "AuthorityPeerId",
                BindingFlags.Instance | BindingFlags.Public);
            PropertyInfo localAuthority = sessionType.GetProperty(
                "IsLocalAuthority",
                BindingFlags.Instance | BindingFlags.Public);
            PropertyInfo matchId = sessionType.GetProperty(
                "MatchId",
                BindingFlags.Instance | BindingFlags.Public);

            Assert.That(authority.GetValue(client), Is.EqualTo(1));
            Assert.That(localAuthority.GetValue(client), Is.EqualTo(false));
            Assert.That(matchId.GetValue(client), Is.EqualTo(matchId.GetValue(host)));
            Assert.That(RuntimeAssembly.Invoke(client, "TryBeginBattle"), Is.EqualTo(true));
        }

        [Test]
        public void LobbySnapshotRejectsDuplicateTransportIdentity()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object host = Activator.CreateInstance(sessionType);

            RuntimeAssembly.Invoke(host, "AddPlayer", 1, 1, true);
            RuntimeAssembly.Invoke(host, "TrySetPeerTransportIdentity", 1, "steam:duplicate");
            RuntimeAssembly.Invoke(host, "AddPeer", 10, false, "steam:duplicate");
            RuntimeAssembly.Invoke(host, "AddPlayerToPeer", 2, 2, 10);

            MethodInfo createSnapshot = sessionType.GetMethod(
                "TryCreateLobbySnapshot",
                BindingFlags.Instance | BindingFlags.Public);
            object[] snapshotArgs = { null };
            Assert.That((bool)createSnapshot.Invoke(host, snapshotArgs), Is.False);
            Assert.That(snapshotArgs[0], Is.Null);
        }

        [Test]
        public void LobbySnapshotIsAvailableOnlyWhileSessionIsConfiguring()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object host = Activator.CreateInstance(sessionType);

            RuntimeAssembly.Invoke(host, "AddPlayer", 1, 1, true);
            RuntimeAssembly.Invoke(host, "TrySetPeerTransportIdentity", 1, "steam:host");
            RuntimeAssembly.Invoke(host, "AddPeer", 10, false, "steam:client");
            RuntimeAssembly.Invoke(host, "AddPlayerToPeer", 2, 2, 10);

            MethodInfo createSnapshot = sessionType.GetMethod(
                "TryCreateLobbySnapshot",
                BindingFlags.Instance | BindingFlags.Public);
            object[] beforeBattle = { null };
            Assert.That((bool)createSnapshot.Invoke(host, beforeBattle), Is.True);

            Assert.That(RuntimeAssembly.Invoke(host, "TryBeginBattle"), Is.EqualTo(true));

            object[] afterBattle = { null };
            Assert.That((bool)createSnapshot.Invoke(host, afterBattle), Is.False);
            Assert.That(afterBattle[0], Is.Null);
        }

        [Test]
        public void LobbySnapshotProtocolRoundTripsWithExactSchema()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            Type protocolType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MultiplayerProtocol");
            object host = Activator.CreateInstance(sessionType);

            RuntimeAssembly.Invoke(host, "AddPlayer", 1, 1, true);
            RuntimeAssembly.Invoke(host, "TrySetPeerTransportIdentity", 1, "steam:host");
            RuntimeAssembly.Invoke(host, "AddPeer", 10, false, "steam:client");
            RuntimeAssembly.Invoke(host, "AddPlayerToPeer", 2, 2, 10);

            MethodInfo createSnapshot = sessionType.GetMethod(
                "TryCreateLobbySnapshot",
                BindingFlags.Instance | BindingFlags.Public);
            object[] snapshotArgs = { null };
            Assert.That((bool)createSnapshot.Invoke(host, snapshotArgs), Is.True);

            MethodInfo serialize = protocolType.GetMethod(
                "TrySerializeLobbySnapshot",
                BindingFlags.Public | BindingFlags.Static);
            object[] serializeArgs = { snapshotArgs[0], null };
            Assert.That((bool)serialize.Invoke(null, serializeArgs), Is.True);
            byte[] payload = (byte[])serializeArgs[1];

            MethodInfo deserialize = protocolType.GetMethod(
                "TryDeserializeLobbySnapshot",
                BindingFlags.Public | BindingFlags.Static);
            object[] deserializeArgs = { payload, null };
            Assert.That((bool)deserialize.Invoke(null, deserializeArgs), Is.True);

            object parsed = deserializeArgs[1];
            Assert.That(RuntimeAssembly.GetField(parsed, "AuthorityPeerId"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(parsed, "Peers")), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(parsed, "Players")), Is.EqualTo(2));
        }

        [Test]
        public void LobbySnapshotProtocolRejectsUnknownNestedFieldsAndOversize()
        {
            Type protocolType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MultiplayerProtocol");
            MethodInfo deserialize = protocolType.GetMethod(
                "TryDeserializeLobbySnapshot",
                BindingFlags.Public | BindingFlags.Static);

            string matchId = Guid.NewGuid().ToString("N");
            string invalid =
                "{\"v\":1,\"type\":\"lobby\",\"match\":\"" + matchId +
                "\",\"authority\":1,\"peers\":[{\"id\":1,\"identity\":\"steam:host\",\"extra\":1}]," +
                "\"players\":[{\"id\":1,\"peer\":1,\"side\":1}]}";
            object[] invalidArgs = { System.Text.Encoding.UTF8.GetBytes(invalid), null };
            Assert.That((bool)deserialize.Invoke(null, invalidArgs), Is.False);

            byte[] oversized = new byte[65537];
            object[] oversizedArgs = { oversized, null };
            Assert.That((bool)deserialize.Invoke(null, oversizedArgs), Is.False);
        }

        [Test]
        public void LobbyLoadoutSnapshotUsesTransientIdsInsteadOfPersistentFleetIdentity()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object host = Activator.CreateInstance(sessionType);
            Type savedSquadType = RuntimeAssembly.GetType("Assets.Scripts.Data.SavedSquad");
            Type strategyType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShootingStrategyTypes");
            Type statsType = RuntimeAssembly.GetType("Assets.Scripts.Data.SquadStatBlock");
            object stats = Activator.CreateInstance(
                statsType,
                "Persistent Commander",
                10, 7, 3, 100, 80, 4);
            object squad = Activator.CreateInstance(
                savedSquadType,
                9001L,
                1,
                "Persistent Squad",
                Vector2.zero,
                false,
                false,
                Enum.Parse(strategyType, "FirstSeen"),
                Color.white,
                stats);

            object fleetShip = Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Data.FleetShip"),
                12345L,
                Enum.Parse(RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes"), "Wasp"),
                false,
                false,
                0, 0, 0, 0, 0, 0, 0,
                "Persistent Ship");
            object squadShip = Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Data.SquadShip"),
                fleetShip,
                new Vector2(1f, 2f));
            RuntimeAssembly.Invoke(squad, "AddShipToSquad", squadShip);

            RuntimeAssembly.Invoke(host, "AddPlayer", 1, 1, true);
            RuntimeAssembly.Invoke(host, "TrySetPeerTransportIdentity", 1, "steam:host");
            RuntimeAssembly.Invoke(host, "AddPeer", 10, false, "steam:client");
            RuntimeAssembly.Invoke(host, "AddPlayerToPeer", 2, 2, 10);
            Assert.That(RuntimeAssembly.Invoke(
                host,
                "TryAssignSavedSquadOwner",
                squad,
                1), Is.EqualTo(true));

            MethodInfo createSnapshot = sessionType.GetMethod(
                "TryCreateLobbySnapshot",
                BindingFlags.Instance | BindingFlags.Public);
            object[] args = { null };
            Assert.That((bool)createSnapshot.Invoke(host, args), Is.True);

            object snapshot = args[0];
            object squads = RuntimeAssembly.GetField(snapshot, "Squads");
            Assert.That(RuntimeAssembly.GetCount(squads), Is.EqualTo(1));

            object firstSquad = ((System.Collections.IList)squads)[0];
            Assert.That((long)RuntimeAssembly.GetField(firstSquad, "TransientSquadId"), Is.LessThan(0));
            Assert.That((long)RuntimeAssembly.GetField(firstSquad, "TransientSquadId"), Is.Not.EqualTo(9001L));

            object ships = RuntimeAssembly.GetField(firstSquad, "Ships");
            object firstShip = ((System.Collections.IList)ships)[0];
            Assert.That((long)RuntimeAssembly.GetField(firstShip, "TransientFleetId"), Is.LessThan(0));
            Assert.That((long)RuntimeAssembly.GetField(firstShip, "TransientFleetId"), Is.Not.EqualTo(12345L));
        }

        [Test]
        public void ReconstructedLobbyLoadoutRetainsOwnerTokenWithoutPersistentIds()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            Type snapshotType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchLobbySnapshot");
            Type peerType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchLobbyPeerSnapshot");
            Type playerType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchLobbyPlayerSnapshot");
            Type squadType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchLobbySquadSnapshot");
            Type shipType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchLobbyShipSnapshot");
            object snapshot = Activator.CreateInstance(snapshotType);

            RuntimeAssembly.SetField(snapshot, "MatchId", Guid.NewGuid().ToString("N"));
            RuntimeAssembly.SetField(snapshot, "AuthorityPeerId", 1);
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(snapshot, "Peers"),
                Activator.CreateInstance(peerType, 1, "steam:host"));
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(snapshot, "Peers"),
                Activator.CreateInstance(peerType, 10, "steam:client"));
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(snapshot, "Players"),
                Activator.CreateInstance(playerType, 1, 1, 1));
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(snapshot, "Players"),
                Activator.CreateInstance(playerType, 2, 10, 2));

            object squadSnapshot = Activator.CreateInstance(squadType);
            Guid token = Guid.NewGuid();
            RuntimeAssembly.SetField(squadSnapshot, "OwnershipToken", token.ToString("N"));
            RuntimeAssembly.SetField(squadSnapshot, "OwnerPlayerId", 2);
            RuntimeAssembly.SetField(squadSnapshot, "TransientSquadId", -1L);
            RuntimeAssembly.SetField(squadSnapshot, "Side", 2);
            RuntimeAssembly.SetField(squadSnapshot, "Name", "Remote Squad");
            RuntimeAssembly.SetField(squadSnapshot, "ColorR", 1f);
            RuntimeAssembly.SetField(squadSnapshot, "ColorG", 1f);
            RuntimeAssembly.SetField(squadSnapshot, "ColorB", 1f);
            RuntimeAssembly.SetField(squadSnapshot, "ColorA", 1f);
            RuntimeAssembly.SetField(
                squadSnapshot,
                "ShootingStrategy",
                (int)Enum.Parse(
                    RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShootingStrategyTypes"),
                    "FirstSeen"));

            object shipSnapshot = Activator.CreateInstance(shipType);
            RuntimeAssembly.SetField(shipSnapshot, "TransientFleetId", -1L);
            RuntimeAssembly.SetField(
                shipSnapshot,
                "ShipType",
                (int)Enum.Parse(RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes"), "Gunship"));
            RuntimeAssembly.SetField(shipSnapshot, "Name", "Remote Ship");
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(squadSnapshot, "Ships"),
                shipSnapshot);
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(snapshot, "Squads"),
                squadSnapshot);

            MethodInfo createSession = sessionType.GetMethod(
                "TryCreateFromLobbySnapshot",
                BindingFlags.Static | BindingFlags.Public);
            object[] args = { snapshot, "steam:client", null };
            Assert.That((bool)createSession.Invoke(null, args), Is.True);
            object session = args[2];

            MethodInfo canonicalMethod = sessionType.GetMethod(
                "TryCreateCanonicalLobbySquads",
                BindingFlags.Instance | BindingFlags.Public);
            object[] canonicalArgs = { null };
            Assert.That((bool)canonicalMethod.Invoke(session, canonicalArgs), Is.True);
            object assigned = canonicalArgs[0];
            Assert.That(RuntimeAssembly.GetCount(assigned), Is.EqualTo(1));
            object reconstructed = ((System.Collections.IList)assigned)[0];
            Assert.That(RuntimeAssembly.GetField(reconstructed, "Id"), Is.EqualTo(-1L));
            Assert.That(RuntimeAssembly.GetField(reconstructed, "MatchOwnershipToken"), Is.EqualTo(token));
            Assert.That(RuntimeAssembly.Invoke(
                session,
                "ResolveSquadOwner",
                reconstructed,
                2), Is.EqualTo(2));
        }

        [Test]
        public void LobbyWireProtocolPreservesTransientSquadLoadout()
        {
            Type protocolType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MultiplayerProtocol");
            Type snapshotType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchLobbySnapshot");
            Type peerType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchLobbyPeerSnapshot");
            Type playerType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchLobbyPlayerSnapshot");
            Type squadType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchLobbySquadSnapshot");
            Type shipType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchLobbyShipSnapshot");
            object snapshot = Activator.CreateInstance(snapshotType);

            RuntimeAssembly.SetField(snapshot, "MatchId", Guid.NewGuid().ToString("N"));
            RuntimeAssembly.SetField(snapshot, "AuthorityPeerId", 1);
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(snapshot, "Peers"),
                Activator.CreateInstance(peerType, 1, "steam:host"));
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(snapshot, "Players"),
                Activator.CreateInstance(playerType, 1, 1, 1));

            object squad = Activator.CreateInstance(squadType);
            RuntimeAssembly.SetField(squad, "OwnershipToken", Guid.NewGuid().ToString("N"));
            RuntimeAssembly.SetField(squad, "OwnerPlayerId", 1);
            RuntimeAssembly.SetField(squad, "TransientSquadId", -1L);
            RuntimeAssembly.SetField(squad, "Side", 1);
            RuntimeAssembly.SetField(squad, "Name", "Canonical Squad");
            RuntimeAssembly.SetField(squad, "ColorR", 1f);
            RuntimeAssembly.SetField(squad, "ColorG", 1f);
            RuntimeAssembly.SetField(squad, "ColorB", 1f);
            RuntimeAssembly.SetField(squad, "ColorA", 1f);
            RuntimeAssembly.SetField(
                squad,
                "ShootingStrategy",
                (int)Enum.Parse(
                    RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShootingStrategyTypes"),
                    "FirstSeen"));

            object ship = Activator.CreateInstance(shipType);
            RuntimeAssembly.SetField(ship, "TransientFleetId", -1L);
            RuntimeAssembly.SetField(
                ship,
                "ShipType",
                (int)Enum.Parse(
                    RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes"),
                    "Wasp"));
            RuntimeAssembly.SetField(ship, "Name", "Canonical Ship");
            RuntimeAssembly.SetField(ship, "OffsetX", 3f);
            RuntimeAssembly.SetField(ship, "OffsetY", -2f);
            RuntimeAssembly.AddToCollection(RuntimeAssembly.GetField(squad, "Ships"), ship);
            RuntimeAssembly.AddToCollection(RuntimeAssembly.GetField(snapshot, "Squads"), squad);

            MethodInfo serialize = protocolType.GetMethod(
                "TrySerializeLobbySnapshot",
                BindingFlags.Public | BindingFlags.Static);
            object[] serializeArgs = { snapshot, null };
            Assert.That((bool)serialize.Invoke(null, serializeArgs), Is.True);

            MethodInfo deserialize = protocolType.GetMethod(
                "TryDeserializeLobbySnapshot",
                BindingFlags.Public | BindingFlags.Static);
            object[] deserializeArgs = { serializeArgs[1], null };
            Assert.That((bool)deserialize.Invoke(null, deserializeArgs), Is.True);

            object parsed = deserializeArgs[1];
            object parsedSquads = RuntimeAssembly.GetField(parsed, "Squads");
            Assert.That(RuntimeAssembly.GetCount(parsedSquads), Is.EqualTo(1));
            object parsedSquad = ((System.Collections.IList)parsedSquads)[0];
            Assert.That(RuntimeAssembly.GetField(parsedSquad, "TransientSquadId"), Is.EqualTo(-1L));
            object parsedShips = RuntimeAssembly.GetField(parsedSquad, "Ships");
            Assert.That(RuntimeAssembly.GetCount(parsedShips), Is.EqualTo(1));
            object parsedShip = ((System.Collections.IList)parsedShips)[0];
            Assert.That(RuntimeAssembly.GetField(parsedShip, "TransientFleetId"), Is.EqualTo(-1L));
        }

        [Test]
        public void HostCanonicalLobbySquadsDoNotExposePersistentIdsOrStats()
        {
            string statePath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.cs");
            string source = File.ReadAllText(statePath);

            StringAssert.Contains("TryCreateCanonicalLobbySquads", source);
            StringAssert.Contains("snapshot.TransientSquadId", source);
            StringAssert.Contains("shipSnapshot.TransientFleetId", source);
            StringAssert.Contains("new SquadStatBlock(", source);
            StringAssert.Contains("\"Multiplayer\"", source);
            StringAssert.DoesNotContain("return _squadOwnerAssignments", source);
        }

        [Test]
        public void SteamLobbyTransportUsesDedicatedPreBattleChannelAndRepeatedSnapshots()
        {
            string steamPath = Path.Combine(Application.dataPath, "Scripts", "Steamworks.NET", "SteamManager.cs");
            string source = File.ReadAllText(steamPath);

            StringAssert.Contains("private const int LobbyChannel = 46;", source);
            StringAssert.Contains("BroadcastIntervalSeconds = 0.5f", source);
            StringAssert.Contains("TryCreateLobbySnapshot(out MatchLobbySnapshot snapshot)", source);
            StringAssert.Contains("TrySerializeLobbySnapshot(snapshot", source);
            StringAssert.Contains("TryDeserializeLobbySnapshot(", source);
            StringAssert.Contains("TryCreateFromLobbySnapshot(", source);
        }

        [Test]
        public void SteamLobbyClientAcceptsSnapshotsOnlyFromConfiguredAuthorityIdentity()
        {
            string steamPath = Path.Combine(Application.dataPath, "Scripts", "Steamworks.NET", "SteamManager.cs");
            string source = File.ReadAllText(steamPath);

            StringAssert.Contains("_authorityCanonicalIdentity", source);
            StringAssert.Contains("return string.Equals(", source);
            StringAssert.Contains("canonicalIdentity,", source);
            StringAssert.Contains("_authorityCanonicalIdentity,", source);
            StringAssert.Contains("StringComparison.Ordinal", source);
        }

        [Test]
        public void LobbyAndBattleSteamChannelsRemainSeparate()
        {
            string steamPath = Path.Combine(Application.dataPath, "Scripts", "Steamworks.NET", "SteamManager.cs");
            string source = File.ReadAllText(steamPath);

            StringAssert.Contains("private const int LobbyChannel = 46;", source);
            StringAssert.Contains("private const int CommandChannel = 47;", source);
        }

        [Test]
        public void SquadMakerPumpsLobbyTransportOnlyThroughFreePlayEntryPoints()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Scenes", "SquadMaker.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("StartHostingMultiplayerLobby(MatchSession session)", source);
            StringAssert.Contains("StartJoiningMultiplayerLobby(string authorityTransportIdentity)", source);
            Assert.That(
                source.Split(new[] { "ConfigData.CurrentGameMode != ConfigData.GameModes.FreePlay" },
                    StringSplitOptions.None).Length - 1,
                Is.GreaterThanOrEqualTo(2));
            StringAssert.Contains("protected override void Update()", source);
            StringAssert.Contains("base.Update();", source);
            StringAssert.Contains("_multiplayerLobbyTransport?.Update();", source);
            StringAssert.Contains("TryTakeReceivedSession(", source);
        }

        [Test]
        public void SquadMakerHostBindsCanonicalLocalSteamIdentityBeforeBroadcast()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Scenes", "SquadMaker.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("TryGetLocalTransportIdentity(", source);
            StringAssert.Contains("session.TrySetPeerTransportIdentity(", source);
            StringAssert.Contains("SteamMultiplayerLobbyTransportFactory.CreateHost(session)", source);
        }

        [Test]
        public void SquadMakerDisposesLobbyTransportOnSceneTeardown()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Scenes", "SquadMaker.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("private void OnDestroy()", source);
            StringAssert.Contains("StopMultiplayerLobbyTransport();", source);
            StringAssert.Contains("_multiplayerLobbyTransport?.Dispose();", source);
        }

        [Test]
        public void LobbySnapshotPreservesMatchSetupSeed()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object host = Activator.CreateInstance(sessionType);
            RuntimeAssembly.Invoke(host, "AddPlayer", 1, 1, true);
            RuntimeAssembly.Invoke(host, "TrySetPeerTransportIdentity", 1, "steam:host");

            PropertyInfo seedProperty = sessionType.GetProperty(
                "SetupSeed",
                BindingFlags.Instance | BindingFlags.Public);
            int hostSeed = (int)seedProperty.GetValue(host);
            Assert.That(hostSeed, Is.GreaterThan(0));

            MethodInfo createSnapshot = sessionType.GetMethod(
                "TryCreateLobbySnapshot",
                BindingFlags.Instance | BindingFlags.Public);
            object[] snapshotArgs = { null };
            Assert.That((bool)createSnapshot.Invoke(host, snapshotArgs), Is.True);
            Assert.That(RuntimeAssembly.GetField(snapshotArgs[0], "SetupSeed"), Is.EqualTo(hostSeed));
        }

        [Test]
        public void MatchSetupRandomIsStablePerLevelAndSeparatedAcrossLevels()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            Type randomType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSetupRandom");
            object session = Activator.CreateInstance(sessionType);

            int levelOneSeed = (int)RuntimeAssembly.Invoke(session, "GetLevelSetupSeed", 1);
            int levelTwoSeed = (int)RuntimeAssembly.Invoke(session, "GetLevelSetupSeed", 2);
            Assert.That(levelOneSeed, Is.GreaterThan(0));
            Assert.That(levelTwoSeed, Is.GreaterThan(0));
            Assert.That(levelTwoSeed, Is.Not.EqualTo(levelOneSeed));

            object first = Activator.CreateInstance(randomType, levelOneSeed);
            object second = Activator.CreateInstance(randomType, levelOneSeed);
            for (int i = 0; i < 16; i++)
            {
                Assert.That(
                    RuntimeAssembly.Invoke(first, "NextInt", 100000),
                    Is.EqualTo(RuntimeAssembly.Invoke(second, "NextInt", 100000)));
            }
        }

        [Test]
        public void OnlineRandomSquadSetupUsesMatchRngAndMatchOnlyIds()
        {
            string levelPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "Level.RandomSquadSetup.cs");
            string savedSquadPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Data",
                "SavedSquad.cs");
            string levelSource = File.ReadAllText(levelPath);
            string savedSquadSource = File.ReadAllText(savedSquadPath);

            StringAssert.Contains("UsesDeterministicMultiplayerSetupRandom", levelSource);
            StringAssert.Contains("SetupUnityRandomRange", levelSource);
            StringAssert.Contains("SetupUtilityRandomInt(4)", levelSource);
            StringAssert.Contains("AllocateSetupSavedSquadId()", levelSource);
            StringAssert.Contains("AllocateSetupFleetShipId", levelSource);
            StringAssert.Contains("Func<int, int, int> randomRange = null", savedSquadSource);
            StringAssert.Contains("Func<long> fleetIdFactory = null", savedSquadSource);
        }

        [Test]
        public void LegacyRandomSquadSetupStillUsesOriginalRandomSourcesWhenNoOnlineMatch()
        {
            string levelPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "Level.RandomSquadSetup.cs");
            string savedSquadPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Data",
                "SavedSquad.cs");
            string levelSource = File.ReadAllText(levelPath);
            string savedSquadSource = File.ReadAllText(savedSquadPath);

            StringAssert.Contains("Random.Range(0, Stage.BeeShipTypes.Count)", levelSource);
            StringAssert.Contains("Utilities.GetNegativeSavedSquadId()", levelSource);
            StringAssert.Contains("UnityEngine.Random.Range(minInclusive, maxExclusive)", savedSquadSource);
            StringAssert.Contains("Utilities.GetNegativeFleetshipId()", savedSquadSource);
        }

        [Test]
        public void ConfigDataCanPeekLobbySessionWithoutConsumingStageHandoff()
        {
            string configPath = Path.Combine(Application.dataPath, "Scripts", "ConfigData.cs");
            string source = File.ReadAllText(configPath);

            StringAssert.Contains("PeekPendingFreePlayMatchSession()", source);
            StringAssert.Contains("return CurrentGameMode == GameModes.FreePlay", source);
            StringAssert.Contains("_pendingFreePlayMatchSession", source);
            StringAssert.Contains("ConsumePendingFreePlayMatchSession()", source);
        }

        [Test]
        public void SquadMakerPersistsAndResumesConfiguringLobbyAcrossSceneChanges()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Scenes", "SquadMaker.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("ConfigData.TrySetPendingFreePlayMatchSession(session);", source);
            StringAssert.Contains("ConfigData.TrySetPendingFreePlayMatchSession(receivedSession);", source);
            StringAssert.Contains("ResumePendingMultiplayerLobby();", source);
            StringAssert.Contains("ConfigData.PeekPendingFreePlayMatchSession()", source);
            StringAssert.Contains("pendingSession.IsLocalAuthority", source);
            StringAssert.Contains("SteamMultiplayerLobbyTransportFactory.CreateClient(", source);
        }

        [Test]
        public void LobbySnapshotCarriesHostRandomShipTypePools()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            Type shipType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes");
            object session = Activator.CreateInstance(sessionType);
            Array bees = Array.CreateInstance(shipType, 1);
            bees.SetValue(Enum.Parse(shipType, "Wasp"), 0);
            Array humans = Array.CreateInstance(shipType, 1);
            humans.SetValue(Enum.Parse(shipType, "Gunship"), 0);

            Assert.That(RuntimeAssembly.Invoke(
                session,
                "TrySetRandomShipTypes",
                bees,
                humans), Is.EqualTo(true));
            RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
            RuntimeAssembly.Invoke(session, "TrySetPeerTransportIdentity", 1, "steam:host");

            MethodInfo createSnapshot = sessionType.GetMethod(
                "TryCreateLobbySnapshot",
                BindingFlags.Instance | BindingFlags.Public);
            object[] args = { null };
            Assert.That((bool)createSnapshot.Invoke(session, args), Is.True);

            Assert.That(RuntimeAssembly.GetCount(
                RuntimeAssembly.GetField(args[0], "BeeRandomShipTypes")), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetCount(
                RuntimeAssembly.GetField(args[0], "HumanRandomShipTypes")), Is.EqualTo(1));
        }

        [Test]
        public void SquadMakerContinuouslyRefreshesHostRandomShipPoolsDuringLobby()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Scenes", "SquadMaker.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("RefreshHostingRandomShipTypes(session)", source);
            StringAssert.Contains("RefreshHostingRandomShipTypes(MultiplayerLobbySession)", source);
            StringAssert.Contains("session.TrySetRandomShipTypes(beeTypes, humanTypes)", source);
        }

        [Test]
        public void StageUsesLobbyRandomShipPoolsBeforeLevelSetup()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Scenes", "Stage.cs");
            string source = File.ReadAllText(path);

            int poolIndex = source.IndexOf("MatchSession.TryGetRandomShipTypes(");
            int setupLevelsIndex = source.IndexOf("SetupLevels();");
            Assert.That(poolIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(setupLevelsIndex, Is.GreaterThan(poolIndex));
            StringAssert.Contains("ConfigData.BeeShipTypes = beeRandomShipTypes.ToHashSet();", source);
            StringAssert.Contains("ConfigData.HumanShipTypes = humanRandomShipTypes.ToHashSet();", source);
        }

        [Test]
        public void CanonicalLevelOptionsRemapPhysicalStartPositionsPerLocalPerspective()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            Type levelOptionsType = RuntimeAssembly.GetType("Assets.Scripts.Data.LevelOptions");
            object session = Activator.CreateInstance(sessionType);
            RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
            RuntimeAssembly.Invoke(session, "TrySetPeerTransportIdentity", 1, "steam:host");

            object options = Activator.CreateInstance(levelOptionsType, 7, 2, "Multiplayer Level");
            RuntimeAssembly.SetField(options, "MapIndex", 0);
            RuntimeAssembly.SetField(options, "UserStartingPosition", new Vector2(10f, 20f));
            RuntimeAssembly.SetField(options, "AIStartingPosition", new Vector2(-10f, -20f));

            Assert.That(RuntimeAssembly.Invoke(
                session,
                "TrySetLevelOptions",
                options,
                1,
                2), Is.EqualTo(true));

            MethodInfo materialize = sessionType.GetMethod(
                "TryCreateCanonicalLevelOptions",
                BindingFlags.Instance | BindingFlags.Public);

            object[] beeArgs = { 1, 2, null };
            Assert.That((bool)materialize.Invoke(session, beeArgs), Is.True);
            object beeView = beeArgs[2];
            Assert.That(
                RuntimeAssembly.GetField(beeView, "UserStartingPosition"),
                Is.EqualTo(new Vector2(10f, 20f)));
            Assert.That(
                RuntimeAssembly.GetField(beeView, "AIStartingPosition"),
                Is.EqualTo(new Vector2(-10f, -20f)));

            object[] humanArgs = { 2, 1, null };
            Assert.That((bool)materialize.Invoke(session, humanArgs), Is.True);
            object humanView = humanArgs[2];
            Assert.That(
                RuntimeAssembly.GetField(humanView, "UserStartingPosition"),
                Is.EqualTo(new Vector2(-10f, -20f)));
            Assert.That(
                RuntimeAssembly.GetField(humanView, "AIStartingPosition"),
                Is.EqualTo(new Vector2(10f, 20f)));
        }

        [Test]
        public void LobbyWireProtocolRoundTripsCanonicalLevelAndObstacleGeometry()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            Type levelOptionsType = RuntimeAssembly.GetType("Assets.Scripts.Data.LevelOptions");
            Type protocolType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MultiplayerProtocol");
            object session = Activator.CreateInstance(sessionType);
            RuntimeAssembly.Invoke(session, "AddPlayer", 1, 1, true);
            RuntimeAssembly.Invoke(session, "TrySetPeerTransportIdentity", 1, "steam:host");

            object options = Activator.CreateInstance(levelOptionsType, 9, 2, "Wire Level");
            RuntimeAssembly.SetField(options, "MapIndex", 1);
            RuntimeAssembly.SetField(options, "FogOfWar", 1);
            RuntimeAssembly.SetField(options, "Mining", 0);
            RuntimeAssembly.SetField(options, "AsteroidOption", 2);
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(options, "ObstacleList"),
                (new Vector2(3f, 4f), new Vector2(20f, 30f)));

            Assert.That(RuntimeAssembly.Invoke(
                session,
                "TrySetLevelOptions",
                options,
                1,
                2), Is.EqualTo(true));

            MethodInfo createSnapshot = sessionType.GetMethod(
                "TryCreateLobbySnapshot",
                BindingFlags.Instance | BindingFlags.Public);
            object[] snapshotArgs = { null };
            Assert.That((bool)createSnapshot.Invoke(session, snapshotArgs), Is.True);

            MethodInfo serialize = protocolType.GetMethod(
                "TrySerializeLobbySnapshot",
                BindingFlags.Public | BindingFlags.Static);
            object[] serializeArgs = { snapshotArgs[0], null };
            Assert.That((bool)serialize.Invoke(null, serializeArgs), Is.True);

            MethodInfo deserialize = protocolType.GetMethod(
                "TryDeserializeLobbySnapshot",
                BindingFlags.Public | BindingFlags.Static);
            object[] deserializeArgs = { serializeArgs[1], null };
            Assert.That((bool)deserialize.Invoke(null, deserializeArgs), Is.True);

            object parsedLevel = RuntimeAssembly.GetField(deserializeArgs[1], "Level");
            Assert.That(parsedLevel, Is.Not.Null);
            Assert.That(RuntimeAssembly.GetField(parsedLevel, "MapIndex"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(parsedLevel, "FogOfWar"), Is.EqualTo(1));
            Assert.That(
                RuntimeAssembly.GetCount(RuntimeAssembly.GetField(parsedLevel, "ObstacleList")),
                Is.EqualTo(1));
        }

        [Test]
        public void LobbyLevelValidationRejectsUnsafeMapAndObstacleValues()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("level.MapIndex < -1", source);
            StringAssert.Contains("level.MapIndex >= locationCount", source);
            StringAssert.Contains("level.EnemySquadGenerationCount > MaxLobbySquads", source);
            StringAssert.Contains("obstacle.ScaleX <= 0f", source);
            StringAssert.Contains("obstacle.ScaleY <= 0f", source);
        }

        [Test]
        public void SquadMakerStagesCanonicalOnlineLevelBeforeSpaceScene()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Scenes", "SquadMaker.cs");
            string source = File.ReadAllText(path);

            int stageIndex = source.IndexOf("TryStageMultiplayerLaunchConfiguration()");
            int spaceIndex = source.LastIndexOf("_nextScene = \"Space\"");
            Assert.That(stageIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(spaceIndex, Is.GreaterThan(stageIndex));
            StringAssert.Contains("session.TrySetLevelOptions(", source);
            StringAssert.Contains("ConfigData.ChooseRandomLevel = false;", source);
        }

        [Test]
        public void StageFailsClosedWhenCanonicalOnlineLevelIsMissing()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Scenes", "Stage.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("MatchSession.TryCreateCanonicalLevelOptions(", source);
            StringAssert.Contains(
                "Configured online Free Play match is missing canonical level data.",
                source);
            StringAssert.Contains(
                "Configured online Free Play match could not enter battle phase.",
                source);
        }

        [Test]
        public void AiLobbyRolesDoNotRequirePlayerOwnershipTokens()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("MatchLobbySquadRole.AiInitial", source);
            StringAssert.Contains("MatchLobbySquadRole.AiReinforcement", source);
            StringAssert.Contains("squad.OwnerPlayerId == UnownedPlayerId", source);
            StringAssert.Contains("string.IsNullOrEmpty(squad.OwnershipToken)", source);
        }

        [Test]
        public void MatchShipIdsAreUniqueWithinSession()
        {
            Type sessionType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MatchSession");
            object session = Activator.CreateInstance(sessionType);

            Assert.That(RuntimeAssembly.Invoke(session, "AllocateMatchShipId"), Is.EqualTo(1L));
            Assert.That(RuntimeAssembly.Invoke(session, "AllocateMatchShipId"), Is.EqualTo(2L));
            Assert.That(RuntimeAssembly.Invoke(session, "AllocateMatchShipId"), Is.EqualTo(3L));
        }

        [Test]
        public void MatchShipIdentityResetsAndAllocatesAfterPooledClear()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Ship.Lifecycle.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("MatchShipId = 0;", source);
            int clearIndex = source.IndexOf("ClearData();");
            int allocateIndex = source.IndexOf(
                "MatchShipId = matchSession == null ? 0 : matchSession.AllocateMatchShipId();");
            Assert.That(clearIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(allocateIndex, Is.GreaterThan(clearIndex));
        }

        [Test]
        public void GameStateMaintainsSeparateMatchShipRegistry()
        {
            string statePath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.cs");
            string registryPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Registry.cs");
            string stateSource = File.ReadAllText(statePath);
            string registrySource = File.ReadAllText(registryPath);

            StringAssert.Contains("Dictionary<long, Ship> ShipsByMatchId", stateSource);
            StringAssert.Contains("ShipsByMatchId.Clear();", stateSource);
            StringAssert.Contains("ShipsByMatchId.Add(ship.MatchShipId, ship);", registrySource);
            StringAssert.Contains("ShipsByMatchId.Remove(ship.MatchShipId);", registrySource);
            StringAssert.Contains("Duplicate match ship id", registrySource);
        }

        [Test]
        public void BattleStateProtocolRoundTripsMatchScopedShipIdentity()
        {
            Type protocolType = RuntimeAssembly.GetType("Assets.Scripts.Levels.MultiplayerProtocol");
            Type snapshotType = RuntimeAssembly.GetType("Assets.Scripts.Levels.BattleStateSnapshot");
            Type shipStateType = RuntimeAssembly.GetType("Assets.Scripts.Levels.BattleShipStateSnapshot");
            Type shipType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes");
            Guid matchId = Guid.NewGuid();

            object snapshot = Activator.CreateInstance(snapshotType);
            RuntimeAssembly.SetField(snapshot, "MatchLevelId", 3);
            RuntimeAssembly.SetField(snapshot, "Sequence", 17L);

            object ship = Activator.CreateInstance(shipStateType);
            RuntimeAssembly.SetField(ship, "MatchShipId", 11L);
            RuntimeAssembly.SetField(ship, "MatchSquadId", 5L);
            RuntimeAssembly.SetField(ship, "Side", 1);
            RuntimeAssembly.SetField(ship, "ShipType", (int)Enum.Parse(shipType, "Wasp"));
            RuntimeAssembly.SetField(ship, "X", 12.5f);
            RuntimeAssembly.SetField(ship, "Y", -8.25f);
            RuntimeAssembly.SetField(ship, "Rotation", 270f);
            RuntimeAssembly.SetField(ship, "VelocityX", 4f);
            RuntimeAssembly.SetField(ship, "VelocityY", -2f);
            RuntimeAssembly.SetField(ship, "Health", 9);
            RuntimeAssembly.SetField(ship, "IsDead", false);
            RuntimeAssembly.AddToCollection(RuntimeAssembly.GetField(snapshot, "Ships"), ship);

            MethodInfo serialize = protocolType.GetMethod(
                "TrySerializeBattleState",
                BindingFlags.Public | BindingFlags.Static);
            object[] serializeArgs = { matchId, snapshot, null };
            Assert.That((bool)serialize.Invoke(null, serializeArgs), Is.True);

            MethodInfo deserialize = protocolType.GetMethod(
                "TryDeserializeBattleState",
                BindingFlags.Public | BindingFlags.Static);
            object[] deserializeArgs = { serializeArgs[2], matchId, null };
            Assert.That((bool)deserialize.Invoke(null, deserializeArgs), Is.True);

            object parsed = deserializeArgs[2];
            Assert.That(RuntimeAssembly.GetField(parsed, "MatchLevelId"), Is.EqualTo(3));
            Assert.That(RuntimeAssembly.GetField(parsed, "Sequence"), Is.EqualTo(17L));
            object parsedShips = RuntimeAssembly.GetField(parsed, "Ships");
            Assert.That(RuntimeAssembly.GetCount(parsedShips), Is.EqualTo(1));
            object parsedShip = ((System.Collections.IList)parsedShips)[0];
            Assert.That(RuntimeAssembly.GetField(parsedShip, "MatchShipId"), Is.EqualTo(11L));
            Assert.That(RuntimeAssembly.GetField(parsedShip, "MatchSquadId"), Is.EqualTo(5L));
        }

        [Test]
        public void BattleStateProtocolRejectsDuplicateShipIdsAndCrossMatchPackets()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("HashSet<long> matchShipIds", source);
            StringAssert.Contains("!matchShipIds.Add(matchShipId)", source);
            StringAssert.Contains("matchId != expectedMatchId", source);
            StringAssert.Contains("MaxBattleStatePacketBytes", source);
            StringAssert.Contains("MaxBattleStateShips", source);
        }

        [Test]
        public void AuthorityBattleSnapshotUsesMatchShipAndSquadIds()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("TryCreateAuthoritativeBattleStateSnapshot", source);
            StringAssert.Contains("ship.MatchShipId", source);
            StringAssert.Contains("ship.Squad.CommandSquadId", source);
            StringAssert.Contains("matchSession.IsLocalAuthority", source);
        }

        [Test]
        public void ClientBattleStateApplicationValidatesWholeWorldBeforeMutation()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string source = File.ReadAllText(path);

            int applyIndex = source.IndexOf("TryApplyAuthoritativeBattleStateSnapshot(");
            Assert.That(applyIndex, Is.GreaterThanOrEqualTo(0));
            string applySource = source.Substring(applyIndex);

            int validateCountIndex = applySource.IndexOf(
                "snapshot.Ships.Count != ShipsByMatchId.Count");
            int validationLoopIndex = applySource.IndexOf(
                "for (int i = 0; i < snapshot.Ships.Count; i++)");
            int mutationIndex = applySource.IndexOf("ship.Transform.localPosition =");
            Assert.That(validateCountIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(validationLoopIndex, Is.GreaterThan(validateCountIndex));
            Assert.That(mutationIndex, Is.GreaterThan(validationLoopIndex));
            StringAssert.Contains("ship.IsDead != state.IsDead", applySource);
            StringAssert.Contains("ship.Squad.CommandSquadId != state.MatchSquadId", applySource);
        }

        [Test]
        public void ClientBattleStateCorrectionDoesNotInvokeGameplayDamageOrKillPaths()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string source = File.ReadAllText(path);
            int applyIndex = source.IndexOf("TryApplyAuthoritativeBattleStateSnapshot(");
            int cloneIndex = source.IndexOf("CloneBattleStateSnapshot(", applyIndex);
            string applySource = source.Substring(
                applyIndex,
                cloneIndex - applyIndex);

            StringAssert.Contains("ship.Health = state.Health;", applySource);
            StringAssert.Contains("ship.UpdateHealthBar();", applySource);
            StringAssert.DoesNotContain(".Kill(", applySource);
            StringAssert.DoesNotContain("LogDamage(", applySource);
            StringAssert.DoesNotContain("LogAttackingDamage(", applySource);
        }

        [Test]
        public void BattleStateQueueKeepsLatestBoundedAuthoritySnapshots()
        {
            string commandPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string statePath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.cs");
            string commandSource = File.ReadAllText(commandPath);
            string stateSource = File.ReadAllText(statePath);

            StringAssert.Contains("MaxQueuedBattleStateSnapshots = 4", commandSource);
            StringAssert.Contains("sourcePeerId != matchSession.AuthorityPeerId", commandSource);
            StringAssert.Contains("_queuedBattleStateSnapshots.Dequeue();", commandSource);
            StringAssert.Contains("candidate.Sequence > newest.Sequence", commandSource);
            StringAssert.Contains("ClearQueuedBattleStateSnapshots();", stateSource);
        }

        [Test]
        public void SteamBattleStateUsesDedicatedAuthorityToClientChannel()
        {
            string steamPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Steamworks.NET",
                "SteamManager.cs");
            string source = File.ReadAllText(steamPath);

            StringAssert.Contains("private const int StateChannel = 48;", source);
            StringAssert.Contains("StateBroadcastIntervalSeconds = 0.1f", source);
            StringAssert.Contains("TryCreateAuthoritativeBattleStateSnapshot(", source);
            StringAssert.Contains("TrySerializeBattleState(", source);
            StringAssert.Contains("sourcePeerId != _session.AuthorityPeerId", source);
            StringAssert.Contains("SendState(peer.Value, payload)", source);
        }

        [Test]
        public void StageRoutesBattleStateOnlyFromAuthorityAndProcessesOnMainThread()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Scenes",
                "Stage.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("TryRouteReceivedBattleStatePacket(", source);
            StringAssert.Contains("sourcePeerId != MatchSession.AuthorityPeerId", source);
            StringAssert.Contains("QueueReceivedBattleStateSnapshot(", source);
            StringAssert.Contains("state.ProcessQueuedBattleStateSnapshots();", source);
            StringAssert.Contains("state.ProcessQueuedPlayerCommands();", source);
        }

        [Test]
        public void MultiplayerLobbyCommandAndStateChannelsRemainDistinct()
        {
            string steamPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Steamworks.NET",
                "SteamManager.cs");
            string source = File.ReadAllText(steamPath);

            StringAssert.Contains("private const int LobbyChannel = 46;", source);
            StringAssert.Contains("private const int CommandChannel = 47;", source);
            StringAssert.Contains("private const int StateChannel = 48;", source);
        }

        [Test]
        public void ReplicaShipDespawnSkipsAuthoritativeKillSideEffects()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Ship.Combat.cs");
            string source = File.ReadAllText(path);
            int start = source.IndexOf("public void ReplicaDespawn()");
            int end = source.IndexOf("public virtual void Kill(", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string replicaSource = source.Substring(start, end - start);

            StringAssert.Contains("Level.State.RemoveShip(this);", replicaSource);
            StringAssert.Contains("CancelOwnedTimers();", replicaSource);
            StringAssert.Contains("Deactivate();", replicaSource);
            StringAssert.DoesNotContain("LogKilledStats(", replicaSource);
            StringAssert.DoesNotContain("LogKillerStats(", replicaSource);
            StringAssert.DoesNotContain("RecordShipDeath(", replicaSource);
            StringAssert.DoesNotContain("DropExplosionAnimation(", replicaSource);
            StringAssert.DoesNotContain("PlayerScore", replicaSource);
        }

        [Test]
        public void ReplicaSquadDespawnSkipsGameOverAndNormalKillPath()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "Squad.Combat.cs");
            string source = File.ReadAllText(path);
            int start = source.IndexOf("public void ReplicaDespawn()");
            int end = source.IndexOf("public void Kill(bool", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string replicaSource = source.Substring(start, end - start);

            StringAssert.Contains("Level.State.RemoveSquad(this);", replicaSource);
            StringAssert.Contains("SetCommandNull();", replicaSource);
            StringAssert.DoesNotContain("GameOver", replicaSource);
            StringAssert.DoesNotContain("IsSideKilled", replicaSource);
        }

        [Test]
        public void AuthoritySnapshotRemovesReplicaShipsMissingFromWorld()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("HashSet<long> authoritativeShipIds", source);
            StringAssert.Contains("shipsToDespawn", source);
            StringAssert.Contains("ship.ReplicaDespawn();", source);
            StringAssert.Contains("snapshot.Ships.Count != ShipsByMatchId.Count", source);
        }

        [Test]
        public void GameStateMaintainsSeparateMatchSquadRegistry()
        {
            string statePath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.cs");
            string registryPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Registry.cs");
            string stateSource = File.ReadAllText(statePath);
            string registrySource = File.ReadAllText(registryPath);

            StringAssert.Contains("Dictionary<long, Squad> SquadsByMatchId", stateSource);
            StringAssert.Contains("SquadsByMatchId.Clear();", stateSource);
            StringAssert.Contains("SquadsByMatchId.Add(squad.MatchSquadId, squad);", registrySource);
            StringAssert.Contains("SquadsByMatchId.Remove(squad.MatchSquadId);", registrySource);
            StringAssert.Contains("Duplicate match squad id", registrySource);
        }

        [Test]
        public void ReplicaShipSetupAdoptsAuthorityIdWithoutDerivedSetupDispatch()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Ship.Lifecycle.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("public bool SetupReplica(", source);
            StringAssert.Contains("SetupCore(", source);
            StringAssert.Contains("authoritativeMatchShipId", source);
            StringAssert.Contains("ReserveReplicaMatchShipId(authoritativeMatchShipId)", source);
            StringAssert.Contains("EnterNetworkReplicaMode();", source);
        }

        [Test]
        public void ReplicaShipModeCancelsAutonomousGameplayActivityButKeepsObjectPresented()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Ship.Lifecycle.cs");
            string source = File.ReadAllText(path);
            int start = source.IndexOf("private void EnterNetworkReplicaMode()");
            int end = source.IndexOf("public virtual void ClearData()", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string replicaMode = source.Substring(start, end - start);

            StringAssert.Contains("CancelOwnedTimers();", replicaMode);
            StringAssert.Contains("StopAllCoroutines();", replicaMode);
            StringAssert.Contains("Weapons[i].Deactivate();", replicaMode);
            StringAssert.Contains("ProximityCollider.Deactivate();", replicaMode);
            StringAssert.Contains("enabled = false;", replicaMode);
            StringAssert.DoesNotContain("\n            Deactivate();", replicaMode);
        }

        [Test]
        public void ReplicaMatchIdsReserveAllocatorSpaceWithoutAuthorityMutation()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("ReserveReplicaMatchSquadId", source);
            StringAssert.Contains("ReserveReplicaMatchShipId", source);
            StringAssert.Contains("IsLocalAuthority", source);
            StringAssert.Contains("Phase != MatchSessionPhase.Battle", source);
        }

        [Test]
        public void BattleStateSnapshotCarriesCompleteSquadAndSpawnMetadata()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("public sealed class BattleSquadStateSnapshot", source);
            StringAssert.Contains("public List<BattleSquadStateSnapshot> Squads", source);
            StringAssert.Contains("OwnerPlayerId = squad.OwnerPlayerId", source);
            StringAssert.Contains("ShouldChase = squad.ShouldChase()", source);
            StringAssert.Contains("OffsetX = ship.OffsetFromCenter.x", source);
            StringAssert.Contains("IsMinionShip = ship.IsMinionShip", source);
            StringAssert.Contains("IsCarrierShip = ship.IsCarrierShip", source);
        }

        [Test]
        public void BattleStateValidationRequiresShipsToReferenceDeclaredSameSideSquads()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("MaxBattleStateSquads = 512", source);
            StringAssert.Contains("Dictionary<long, BattleSquadStateSnapshot> squads", source);
            StringAssert.Contains("!squads.TryGetValue(", source);
            StringAssert.Contains("squad.Side != ship.Side", source);
            StringAssert.Contains("ship.IsDead", source);
        }

        [Test]
        public void ReplicaLifecycleCreatesMissingNormalSquadsAndShipsFromAuthorityMetadata()
        {
            string commandPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string squadPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "Squad.cs");
            string shipPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Ship.Lifecycle.cs");
            string commandSource = File.ReadAllText(commandPath);
            string squadSource = File.ReadAllText(squadPath);
            string shipSource = File.ReadAllText(shipPath);

            StringAssert.Contains("TryReconcileReplicaLifecycle(snapshot)", commandSource);
            StringAssert.Contains("TryEnsureReplicaSquad(", commandSource);
            StringAssert.Contains("TryEnsureReplicaShip(", commandSource);
            StringAssert.Contains("new SquadStatBlock(", commandSource);
            StringAssert.Contains("new FleetShip(", commandSource);
            StringAssert.Contains("squad.SetupReplica(", commandSource);
            StringAssert.Contains("ship.SetupReplica(", commandSource);
            StringAssert.Contains("public bool SetupReplica(", squadSource);
            StringAssert.Contains("public bool SetupReplica(", shipSource);
        }

        [Test]
        public void ReplicaLifecycleCarriesAndRestoresCarrierParentRelationships()
        {
            string commandPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string carrierSquadPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "CarrierSquad.cs");
            string commandSource = File.ReadAllText(commandPath);
            string carrierSquadSource = File.ReadAllText(carrierSquadPath);

            StringAssert.Contains("ApplyReplicaCarrierRelationships(snapshot)", commandSource);
            StringAssert.Contains("TryResolveReplicaCarrier(", commandSource);
            StringAssert.Contains("GetCarrierSquadFromPool()", commandSource);
            StringAssert.Contains("CarrierShipSetup(", commandSource);
            StringAssert.Contains("SetReplicaCarrierRelationship(", carrierSquadSource);
            StringAssert.DoesNotContain(
                "carrierSquad.SetupCarrierSquad(",
                commandSource);
        }

        [Test]
        public void ReplicaSquadSetupCancelsAutonomousCommandAndChaseActivity()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "Squad.cs");
            string source = File.ReadAllText(path);
            int start = source.IndexOf("public bool SetupReplica(");
            int end = source.IndexOf("public void SetSquadCeaseFire(", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string replicaSetup = source.Substring(start, end - start);

            StringAssert.Contains("ReserveReplicaMatchSquadId", replicaSetup);
            StringAssert.Contains("Level.CancelTimer(_checkChaseTimer);", replicaSetup);
            StringAssert.Contains("CancelScriptedCommandQueue();", replicaSetup);
            StringAssert.Contains("SetCommandNull();", replicaSetup);
            StringAssert.Contains("enabled = false;", replicaSetup);
        }

        [Test]
        public void ReplicaLifecyclePreflightsUnsupportedOrConflictingWorldBeforeMutation()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string source = File.ReadAllText(path);

            int apply = source.IndexOf("TryApplyAuthoritativeBattleStateSnapshot(");
            int preflight = source.IndexOf(
                "CanReconcileReplicaLifecycle(snapshot)",
                apply);
            int mutate = source.IndexOf(
                "TryReconcileReplicaLifecycle(snapshot)",
                apply);
            Assert.That(preflight, Is.GreaterThan(apply));
            Assert.That(mutate, Is.GreaterThan(preflight));

            int preflightMethod = source.IndexOf(
                "private bool CanReconcileReplicaLifecycle(");
            int reconcileMethod = source.IndexOf(
                "private bool TryReconcileReplicaLifecycle(");
            string preflightSource = source.Substring(
                preflightMethod,
                reconcileMethod - preflightMethod);
            StringAssert.Contains("state.CarrierSquadType", preflightSource);
            StringAssert.Contains("state.IsCarrierShip", preflightSource);
            StringAssert.DoesNotContain("ReplicaDespawn();", preflightSource);
            StringAssert.DoesNotContain("SetupReplica(", preflightSource);
        }

        [Test]
        public void BattleStateCarrierMetadataIsVersionedAndStrictlyValidated()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("public const int Version = 6;", source);
            StringAssert.Contains("ParentCarrierMatchShipId", source);
            StringAssert.Contains("CarrierSquadType", source);
            StringAssert.Contains("Utilities.ConvertShipTypeToSide.TryGetValue(", source);
            StringAssert.Contains("shipTypeSide != ship.Side", source);
            StringAssert.Contains("squad.ParentCarrierMatchShipId != ship.ParentCarrierMatchShipId", source);
            StringAssert.Contains("parent.ShipType != (int)ConfigData.ShipTypes.Carrier", source);
        }

        [Test]
        public void CarrierReplicaCreationDoesNotInvokeNormalAutoSpawnPath()
        {
            string commandPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string source = File.ReadAllText(commandPath);

            StringAssert.Contains(
                "Stage.Pool.GetCarrierSquadFromPool()",
                source);
            StringAssert.Contains(
                "SetReplicaCarrierRelationship(",
                source);
            StringAssert.DoesNotContain(
                ".SetupCarrierSquad(",
                source);
        }

        [Test]
        public void InitialMultiplayerClientWorldEntersReplicaModeBeforeHiveMindStartup()
        {
            string levelPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "Level.Reset.cs");
            string commandPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string levelSource = File.ReadAllText(levelPath);
            string commandSource = File.ReadAllText(commandPath);

            int setupShips = levelSource.IndexOf("SetupShips();");
            int replica = levelSource.IndexOf(
                "State.EnterInitialNetworkReplicaMode()",
                setupShips);
            int hiveMind = levelSource.IndexOf("SetupHivemind();", setupShips);
            Assert.That(replica, Is.GreaterThan(setupShips));
            Assert.That(hiveMind, Is.GreaterThan(replica));

            StringAssert.Contains("squad.EnterNetworkReplicaMode()", commandSource);
            StringAssert.Contains("ship.EnterNetworkReplicaMode()", commandSource);
        }

        [Test]
        public void NonAuthorityMultiplayerDoesNotStartHiveMindOrPlayerChaseTimers()
        {
            string squadPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "Squad.cs");
            string setupPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "Level.Setup.cs");
            string squadSource = File.ReadAllText(squadPath);
            string setupSource = File.ReadAllText(setupPath);

            StringAssert.Contains(
                "matchSession.IsLocalAuthority && !IsPlayerControlled",
                squadSource);
            StringAssert.Contains(
                "(matchSession == null || matchSession.IsLocalAuthority)",
                squadSource);
            StringAssert.Contains(
                "if (matchSession != null && !matchSession.IsLocalAuthority)",
                setupSource);
            StringAssert.Contains(
                "State.ClearSquadsAwaitingHiveMindCommands();",
                setupSource);
        }

        [Test]
        public void ReplicaShipsKeepSelectionCallbacksWhileGameplayUpdatesAreInert()
        {
            string shipPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Ship.Lifecycle.cs");
            string queenPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Queen.cs");
            string strikerPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Striker.cs");
            string beaconPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Beacon.cs");
            string shipSource = File.ReadAllText(shipPath);

            StringAssert.Contains("public bool EnterNetworkReplicaMode()", shipSource);
            StringAssert.Contains("if (IsNetworkReplica)", shipSource);
            StringAssert.Contains("enabled = true;", shipSource);
            StringAssert.Contains("PrepareForNetworkReplica();", shipSource);
            StringAssert.Contains(
                "Level.CancelTimer(_spawnMinionsTimer);",
                File.ReadAllText(queenPath));
            StringAssert.Contains(
                "Level.CancelTimer(_checkCarrierReloadTimer);",
                File.ReadAllText(strikerPath));
            StringAssert.Contains(
                "Level.CancelTimer(_beaconStatusTimer);",
                File.ReadAllText(beaconPath));
        }

        [Test]
        public void ReplicaShipTriggerOverridesPreserveSelectionButBlockGameplayEffects()
        {
            string[] guardedFiles =
            {
                "Striker.cs",
                "YellowJacket.cs",
                "Barge.cs",
                "WarpGate.cs",
                "Beehive.cs"
            };

            foreach (string fileName in guardedFiles)
            {
                string path = Path.Combine(
                    Application.dataPath,
                    "Scripts",
                    "Entities",
                    "Ships",
                    fileName);
                string source = File.ReadAllText(path);

                StringAssert.Contains("if (IsNetworkReplica)", source);
                StringAssert.Contains("base.OnTriggerEnter2D(collider);", source);
            }

            string bargeSource = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Barge.cs"));
            StringAssert.Contains(
                "public IEnumerator ChargeForward(Ship target = null)",
                bargeSource);
            StringAssert.Contains("yield break;", bargeSource);
        }

        [Test]
        public void PlayerShipSpecialCommandsCarryStrictMatchShipIdentity()
        {
            string commandPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string statePath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.cs");
            string source = File.ReadAllText(commandPath);
            string stateSource = File.ReadAllText(statePath);

            StringAssert.Contains("public const int Version = 6;", source);
            StringAssert.Contains("ScoutDropBeacon", source);
            StringAssert.Contains("BargeCharge", source);
            StringAssert.Contains("FireBargeDetonate", source);
            StringAssert.Contains("public long MatchShipId;", source);
            StringAssert.Contains("[\"ship\"] = command.MatchShipId", source);
            StringAssert.Contains("requiresShip", source);
            StringAssert.Contains("TryPlayerScoutDropBeacon(", source);
            StringAssert.Contains("TryPlayerBargeCharge(", source);
            StringAssert.Contains("TryPlayerFireBargeDetonate(", source);
            StringAssert.Contains("source.MatchShipId", stateSource);
        }

        [Test]
        public void FreePlaySessionSpecialButtonsUseAuthorityCommandsButLegacyModesStayDirect()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "UI Components",
                "SquadActionBox.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("if (Level.Stage.MatchSession == null)", source);
            StringAssert.Contains("PlayerCommandKind.ScoutDropBeacon", source);
            StringAssert.Contains("PlayerCommandKind.BargeCharge", source);
            StringAssert.Contains("PlayerCommandKind.FireBargeDetonate", source);
            StringAssert.Contains("matchShipId: ship.MatchShipId", source);
        }

        [Test]
        public void BattleStateReplicatesAuthorityGameOverWithoutReplicatingLevelEnded()
        {
            string commandPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string source = File.ReadAllText(commandPath);

            StringAssert.Contains("public const int Version = 6;", source);
            StringAssert.Contains("public bool GameOver;", source);
            StringAssert.Contains("public int WinningSide;", source);
            StringAssert.Contains("ResolveAuthoritativeWinningSideForSnapshot()", source);
            StringAssert.Contains("[\"gameOver\"] = snapshot.GameOver", source);
            StringAssert.Contains("[\"winner\"] = snapshot.WinningSide", source);
            StringAssert.Contains("Level.WinningSide = snapshot.WinningSide;", source);
            StringAssert.Contains("GameOver = snapshot.GameOver;", source);
            StringAssert.DoesNotContain("snapshot.LevelEnded", source);
        }

        [Test]
        public void SquadActionBoxDiscreteControlsUseAuthorityCommandPath()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Scripts",
                "UI Components",
                "SquadActionBox.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("PlayerCommandKind.SetChase", source);
            StringAssert.Contains("PlayerCommandKind.SetCeaseFire", source);
            StringAssert.Contains("PlayerCommandKind.SetMatchSpeed", source);
            StringAssert.Contains("PlayerCommandKind.SetShootingStrategy", source);
            StringAssert.Contains("PlayerCommandKind.SetLockOn", source);
            StringAssert.Contains("GetSelectedSquadsForPlayer(playerId)", source);

            StringAssert.DoesNotContain("squad.StopChasing();", source);
            StringAssert.DoesNotContain("squad.SetChase(true);", source);
            StringAssert.DoesNotContain("squad.SetSquadCeaseFire(", source);
            StringAssert.DoesNotContain("squad.IsLockedOn =", source);
            StringAssert.DoesNotContain("squad.UnmatchSpeed();", source);
            StringAssert.DoesNotContain("squad.MatchSpeed(", source);
            StringAssert.DoesNotContain("squad.SetShootingStrategy(", source);
        }

        [Test]
        public void MiningAsteroidsUseDedicatedMatchScopedRegistry()
        {
            string statePath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.cs");
            string registryPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Registry.cs");
            string asteroidPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "MiningAsteroid.cs");
            string stateSource = File.ReadAllText(statePath);
            string registrySource = File.ReadAllText(registryPath);
            string asteroidSource = File.ReadAllText(asteroidPath);

            StringAssert.Contains(
                "Dictionary<long, MiningAsteroid> MiningAsteroidsByMatchId",
                stateSource);
            StringAssert.Contains("_nextMatchMiningAsteroidId = 1;", stateSource);
            StringAssert.Contains("AllocateMatchMiningAsteroidId()", registrySource);
            StringAssert.Contains("Duplicate match mining asteroid id", registrySource);
            StringAssert.Contains("MiningAsteroidsByMatchId.Remove", registrySource);
            StringAssert.Contains("public long MatchMiningAsteroidId;", asteroidSource);
            StringAssert.Contains(
                "Level.State.AddMiningAsteroid(this);",
                asteroidSource);
            StringAssert.Contains(
                "Level.State.RemoveMiningAsteroid(this);",
                asteroidSource);
        }

        [Test]
        public void MiningInputUsesSequencedAuthorityCommandWithMatchAsteroidIdentity()
        {
            string commandPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string inputPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "LevelInputManager.cs");
            string commandSource = File.ReadAllText(commandPath);
            string inputSource = File.ReadAllText(inputPath);

            StringAssert.Contains("public const int Version = 6;", commandSource);
            StringAssert.Contains("PlayerCommandKind.Mine", commandSource);
            StringAssert.Contains("public long MatchMiningAsteroidId;", commandSource);
            StringAssert.Contains("[\"asteroid\"] = command.MatchMiningAsteroidId", commandSource);
            StringAssert.Contains("TryPlayerMineSquad(", commandSource);
            StringAssert.Contains("MiningAsteroidsByMatchId.TryGetValue(", commandSource);
            StringAssert.Contains("PlayerCommandKind.Mine", inputSource);
            StringAssert.Contains(
                "matchMiningAsteroidId: asteroid.MatchMiningAsteroidId",
                inputSource);
            StringAssert.DoesNotContain(
                "squad.UserMining(asteroid)",
                inputSource);
        }

        [Test]
        public void BattleStateReplicatesLiveMiningAsteroidHealthAndAuthorityAbsence()
        {
            string commandPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string asteroidPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "MiningAsteroid.cs");
            string commandSource = File.ReadAllText(commandPath);
            string asteroidSource = File.ReadAllText(asteroidPath);

            StringAssert.Contains(
                "public sealed class BattleMiningAsteroidStateSnapshot",
                commandSource);
            StringAssert.Contains(
                "public List<BattleMiningAsteroidStateSnapshot> MiningAsteroids",
                commandSource);
            StringAssert.Contains(
                "MaxBattleStateMiningAsteroids = 256",
                commandSource);
            StringAssert.Contains(
                "[\"miningAsteroids\"] = miningAsteroids",
                commandSource);
            StringAssert.Contains(
                "CanApplyAuthoritativeMiningAsteroidState(snapshot)",
                commandSource);
            StringAssert.Contains(
                "TryApplyAuthoritativeMiningAsteroidState(snapshot)",
                commandSource);
            StringAssert.Contains(
                "!authorityIds.Add(state.MatchMiningAsteroidId)",
                commandSource);
            StringAssert.Contains(
                "!MiningAsteroidsByMatchId.TryGetValue(",
                commandSource);
            StringAssert.Contains(
                "!asteroid.ReplicaDespawn()",
                commandSource);
            StringAssert.Contains(
                "public bool ReplicaDespawn()",
                asteroidSource);
            StringAssert.Contains("Kill(true);", asteroidSource);
        }

        [Test]
        public void MultiplayerManualFireCarriesPlayerTargetThroughAuthority()
        {
            string commandPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Commands.cs");
            string inputPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "LevelInputManager.cs");
            string turretPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Weapons",
                "Turret.cs");
            string aimingPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Weapons",
                "Turret.Aiming.cs");
            string commandSource = File.ReadAllText(commandPath);
            string inputSource = File.ReadAllText(inputPath);
            string turretSource = File.ReadAllText(turretPath);
            string aimingSource = File.ReadAllText(aimingPath);

            StringAssert.Contains("PlayerCommandKind.SetManualFire", commandSource);
            StringAssert.Contains("TryPlayerSetManualFire(", commandSource);
            StringAssert.Contains("turret.SetManualFire(", commandSource);
            StringAssert.Contains("useExplicitTarget: true", commandSource);
            StringAssert.Contains("float.IsNaN(targetPoint.x)", commandSource);

            StringAssert.Contains(
                "Stage.MatchSession != null && Stage.MatchSession.IsMultiplayer",
                inputSource);
            StringAssert.Contains(
                "private readonly List<long> _multiplayerManualFireSquadIds",
                inputSource);
            StringAssert.Contains(
                "MultiplayerManualFireUpdateInterval = 0.1f",
                inputSource);
            StringAssert.Contains(
                "return _mousePosition - Level.GetPosition();",
                inputSource);
            StringAssert.Contains(
                "PlayerCommandKind.SetManualFire",
                inputSource);
            StringAssert.Contains(
                "_multiplayerManualFireSquadIds[i]",
                inputSource);

            StringAssert.Contains("HasExplicitManualFireTarget", turretSource);
            StringAssert.Contains("ManualFireTargetPoint", turretSource);
            StringAssert.Contains("IsFiringManually = false;", turretSource);
            StringAssert.Contains(
                "HasExplicitManualFireTarget\n                    ? ManualFireTargetPoint",
                aimingSource);
        }

        [Test]
        public void SoloManualFireRetainsLegacyPerFrameMouseAiming()
        {
            string inputPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "LevelInputManager.cs");
            string aimingPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Weapons",
                "Turret.Aiming.cs");
            string inputSource = File.ReadAllText(inputPath);
            string aimingSource = File.ReadAllText(aimingPath);

            StringAssert.Contains("BeginLegacyManualFire();", inputSource);
            StringAssert.Contains("EndLegacyManualFire();", inputSource);
            StringAssert.Contains(
                ": Stage.InputManager.GetMousePosition();",
                aimingSource);
        }

        [Test]
        public void ProjectilesUseDedicatedMatchScopedIdentityAndRegistry()
        {
            string statePath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.cs");
            string registryPath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Registry.cs");
            string projectilePath = Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Projectiles",
                "Projectile.cs");
            string stateSource = File.ReadAllText(statePath);
            string registrySource = File.ReadAllText(registryPath);
            string projectileSource = File.ReadAllText(projectilePath);

            StringAssert.Contains("Dictionary<long, Projectile> ProjectilesByMatchId", stateSource);
            StringAssert.Contains("_nextMatchProjectileId = 1;", stateSource);
            StringAssert.Contains("AllocateMatchProjectileId()", stateSource);
            StringAssert.Contains("ReserveReplicaMatchProjectileId", stateSource);
            StringAssert.Contains("Duplicate match projectile id", registrySource);
            StringAssert.Contains("ProjectilesByMatchId.Remove", registrySource);
            StringAssert.Contains("public long MatchProjectileId;", projectileSource);
            StringAssert.Contains("matchSession.AllocateMatchProjectileId()", projectileSource);
            StringAssert.Contains("MatchProjectileId = 0;", projectileSource);
        }
    }
}