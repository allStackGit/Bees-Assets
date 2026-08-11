using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PersistedIdentityBootstrapTests
    {
        [Test]
        public void FleetFallbackIsGeneratedLazilyAfterIdentitySynchronization()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "FleetData.cs"));

            int constructorStart = source.IndexOf("public FleetData(", StringComparison.Ordinal);
            int getDefaultStart = source.IndexOf("public override string GetDefaultJson()", StringComparison.Ordinal);
            Assert.That(constructorStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(getDefaultStart, Is.GreaterThan(constructorStart));

            string constructor = source.Substring(constructorStart, getDefaultStart - constructorStart);
            Assert.That(constructor, Does.Not.Contain("MakeDefaultList(startingShips)"),
                "Constructing a fallback fleet must not consume FleetIds before user progress has loaded.");

            int syncStart = source.IndexOf("private bool TrySynchronizeFleetIdAllocator()", getDefaultStart, StringComparison.Ordinal);
            Assert.That(syncStart, Is.GreaterThan(getDefaultStart));
            string getDefault = source.Substring(getDefaultStart, syncStart - getDefaultStart);
            Assert.That(getDefault, Does.Contain("TrySynchronizeFleetIdAllocator()"));
            Assert.That(getDefault, Does.Contain("return ConfigData.WaitingMessage;"),
                "A missing fleet response that beats user progress must defer rather than allocate from an uninitialized counter.");

            int makeDefaultStart = source.IndexOf("private string MakeDefaultList", syncStart, StringComparison.Ordinal);
            Assert.That(makeDefaultStart, Is.GreaterThan(syncStart));
            string synchronizer = source.Substring(syncStart, makeDefaultStart - syncStart);
            Assert.That(synchronizer, Does.Contain("progressJson[\"FleetId\"]"));
            Assert.That(synchronizer, Does.Contain("ConfigData.UserProgressData.FleetId = persistedFleetId;"));
        }

        [Test]
        public void WaitingSentinelCannotBePersistedAsUserData()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "DataFile.cs"));

            int methodStart = source.IndexOf("public object WriteData(string data)", StringComparison.Ordinal);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));

            int sentinelCheck = source.IndexOf("data == ConfigData.WaitingMessage", methodStart, StringComparison.Ordinal);
            int serverWrite = source.IndexOf("WriteServerData(data);", sentinelCheck, StringComparison.Ordinal);
            Assert.That(sentinelCheck, Is.GreaterThan(methodStart));
            Assert.That(serverWrite, Is.GreaterThan(sentinelCheck),
                "The waiting sentinel must return before any server write can occur.");
        }

        [Test]
        public void PersistedCounterReconciliationUsesMaximumExistingId()
        {
            Type sceneType = RuntimeAssembly.GetType("Assets.Scripts.Scenes.Scene");
            object result = RuntimeAssembly.InvokeStatic(
                sceneType,
                "ReconcileCounterWithIds",
                1521,
                new long[] { 4, 1522, 1533, 100 });

            Assert.That(result, Is.EqualTo(1533));
        }
    }
}
