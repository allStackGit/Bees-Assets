using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadCommandOwnershipTests
    {
        private string _core;
        private string _commands;
        private string _combat;

        [SetUp]
        public void SetUp()
        {
            string root = Path.Combine(Application.dataPath, "Scripts", "Levels");
            _core = File.ReadAllText(Path.Combine(root, "Squad.cs"));
            _commands = File.ReadAllText(Path.Combine(root, "Squad.Commands.cs"));
            _combat = File.ReadAllText(Path.Combine(root, "Squad.Combat.cs"));
        }

        [Test]
        public void SquadIsSplitIntoFocusedPartials()
        {
            StringAssert.Contains("public partial class Squad", _core);
            Assert.That(File.Exists(Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.Movement.cs")), Is.True);
            Assert.That(File.Exists(Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.Combat.cs")), Is.True);
            Assert.That(File.Exists(Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.Commands.cs")), Is.True);
            Assert.That(File.Exists(Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.Geometry.cs")), Is.True);
            Assert.That(File.Exists(Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.UI.cs")), Is.True);
        }

        [Test]
        public void SetCommandOwnsActiveCommandFlagAndQueueRunnerDoesNotReassertIt()
        {
            StringAssert.Contains("HasCommand = command != null;", _commands);

            int queueStart = _commands.IndexOf("public void RunCommandQueue()");
            int matchupStart = _commands.IndexOf("private HashSet<ConfigData.ShipTypes>", queueStart);
            Assert.That(queueStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(matchupStart, Is.GreaterThan(queueStart));
            string queue = _commands.Substring(queueStart, matchupStart - queueStart);
            StringAssert.DoesNotContain("HasCommand = true", queue);
        }

        [Test]
        public void UserOverrideDiscardsPreparedQueueBeforeFinalizingCurrentCommand()
        {
            int finalizeStart = _commands.IndexOf("public void FinalizeUserCommand()");
            int nearestStart = _commands.IndexOf("public MiningAsteroid GetNearestMiningAsteroid", finalizeStart);
            string finalize = _commands.Substring(finalizeStart, nearestStart - finalizeStart);

            int cancelQueue = finalize.IndexOf("CancelScriptedCommandQueue()");
            int currentLookup = finalize.IndexOf("Command currentCommand = GetCommand()");
            int finalizeCurrent = finalize.IndexOf("currentCommand.SetFinalize(\"New command given\")");
            Assert.That(cancelQueue, Is.GreaterThanOrEqualTo(0));
            Assert.That(currentLookup, Is.GreaterThan(cancelQueue));
            Assert.That(finalizeCurrent, Is.GreaterThan(currentLookup));
            StringAssert.Contains("currentCommand == null", finalize);
        }

        [Test]
        public void PreparedCommandsReturnThroughReleaseQueue()
        {
            StringAssert.Contains("command.ClearData();", _commands);
            StringAssert.Contains("command.IsDead = true;", _commands);
            StringAssert.Contains("Level.State.CommandsToRelease.Add(command);", _commands);
            StringAssert.Contains("while (CommandQueue.Count > 0)", _commands);
        }

        [Test]
        public void SquadDeathCancelsPreparedQueueBeforeCurrentCommandFinalizes()
        {
            int cancel = _combat.IndexOf("CancelScriptedCommandQueue();");
            int killed = _combat.IndexOf("GetCommand().SquadKilled();", cancel);
            Assert.That(cancel, Is.GreaterThanOrEqualTo(0));
            Assert.That(killed, Is.GreaterThan(cancel));
        }

        [Test]
        public void PooledSquadResetsLockOnState()
        {
            int clearStart = _core.IndexOf("public virtual void ClearData()");
            int createStart = _core.IndexOf("public virtual void Create", clearStart);
            string clear = _core.Substring(clearStart, createStart - clearStart);
            StringAssert.Contains("IsLockedOn = false;", clear);
        }

        [Test]
        public void SquadEqualityPreservesUnityDestroyedNullSemantics()
        {
            StringAssert.Contains("(UnityEngine.Object)other == null", _core);
            StringAssert.Contains("(UnityEngine.Object)a == null || (UnityEngine.Object)b == null", _core);
        }
    }
}