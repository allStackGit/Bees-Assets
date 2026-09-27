using System;
using System.IO;
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

            StringAssert.Contains("Where(s => !s.IsLockedOn && s.CanAcceptUserInput)", source);
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

            Assert.That(RuntimeAssembly.Invoke(session, "ResolveSquadOwner", clonedSquad, 1), Is.EqualTo(2));
        }

        [Test]
        public void SavedSquadOwnershipDoesNotGuessAcrossIdCollision()
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
        public void LocalMovementUsesSameAuthorizationGatewayAsRemoteCommands()
        {
            string inputPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "LevelInputManager.cs");
            string source = File.ReadAllText(inputPath);

            StringAssert.Contains("Level.State.TryPlayerMoveSquad(", source);
            StringAssert.DoesNotContain("_moveSquads_selectedSquads[_moveSquads_i].Move(", source);
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
        public void TacticalInputUsesPlayerAuthorizationGateway()
        {
            string inputPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "LevelInputManager.cs");
            string source = File.ReadAllText(inputPath);

            StringAssert.Contains("TryPlayerGuardSquad(", source);
            StringAssert.Contains("TryPlayerPatrolSquad(", source);
            StringAssert.Contains("TryPlayerFullRetreat(", source);
            StringAssert.Contains("TryPlayerHealSquad(", source);
            StringAssert.DoesNotContain("squad.UserGuard(ship.Squad)", source);
            StringAssert.DoesNotContain("squad.UserPatrol(_checkForSelectingPatrolArea_startingPosition", source);
        }
    }
}