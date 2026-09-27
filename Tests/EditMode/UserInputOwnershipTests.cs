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

            byte[] oversized = new byte[16385];
            object[] oversizedArgs = { oversized, null };
            Assert.That((bool)deserialize.Invoke(null, oversizedArgs), Is.False);
        }
    }
}