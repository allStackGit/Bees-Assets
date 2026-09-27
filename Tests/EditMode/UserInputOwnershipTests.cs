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
    }
}