using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class MiningInputDelegationTests
    {
        [Test]
        public void MiningClickDelegatesCapabilityAndMovementSemanticsToSquad()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "LevelInputManager.cs"));
            int start = source.IndexOf("private void SetSquadsToMine", StringComparison.Ordinal);
            int end = source.IndexOf("private void SetSquadsToFullRetreat", start, StringComparison.Ordinal);
            string method = source.Substring(start, end - start);

            Assert.That(method, Does.Contain("squad.UserMining(asteroid)"));
            Assert.That(method, Does.Not.Contain("ShipTypes.Factory"));
            Assert.That(method, Does.Not.Contain("squad.Move"));
        }
    }
}
