using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class YellowJacketDamageAccountingTests
    {
        [Test]
        public void DetonationDelegatesCommandTsvAccountingToLogHitStatsOnce()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "YellowJacket.cs"));

            Assert.That(source, Does.Contain("LogHitStats(attacker, attacker.FleetShip, attacker.Squad.SavedSquad, target, target.Squad, -_targetTSVLoss);"));
            Assert.That(source, Does.Not.Contain("attacker.Squad.GetCommand().Tsv += -_targetTSVLoss;"));
            Assert.That(source, Does.Not.Contain("target.Squad.GetCommand().Tsv += _targetTSVLoss;"));
        }
    }
}
