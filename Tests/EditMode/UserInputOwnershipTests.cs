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
    }
}