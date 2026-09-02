using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlTrainingRegressionTests
    {
        [Test]
        public void CanonicalDesignDocumentMatchesExecutableAbiV4()
        {
            string design = File.ReadAllText(Path.Combine(Application.dataPath, "RL_DESIGN.md"));

            Assert.That(design, Does.Contain("## 4. Canonical Policy ABI v4"));
            Assert.That(design, Does.Contain("- ABI version: `4`"));
            Assert.That(design, Does.Contain("- vector observations: `4685`"));
            Assert.That(design, Does.Contain("- continuous actions: `34`"));
            Assert.That(design, Does.Contain("`512` hidden units, `3` hidden layers"));
            Assert.That(design, Does.Contain("`hidden_units: 512`"));
            Assert.That(design, Does.Contain("`num_layers: 3`"));
            Assert.That(design, Does.Not.Contain("ABI v3"));
            Assert.That(design, Does.Not.Contain("hidden_units: 128"));
            Assert.That(design, Does.Not.Contain("num_layers: 2"));
        }

        [Test]
        public void CasualtyShapingCoversFriendlyFireAndUnattributedLossWithoutEpisodeDoubleCount()
        {
            string coordinator = Read("Scripts", "Scenes", "RlOneVsOneEpisodeCoordinator.cs");
            string combat = Read("Scripts", "Entities", "Ships", "Ship.Combat.cs");
            string fireBarge = Read("Scripts", "Entities", "Ships", "FireBarge.cs");
            string yellowJacket = Read("Scripts", "Entities", "Ships", "YellowJacket.cs");

            Assert.That(coordinator, Does.Not.Contain("attacker.Side == target.Side)"),
                "Friendly fire must reach TSV penalty routing instead of being discarded.");
            Assert.That(coordinator, Does.Contain("bool isEnemyDamage = attacker.Side != target.Side;"));
            Assert.That(coordinator, Does.Contain("if (isEnemyDamage)\n        {\n            _active.ApplyImmediateTsvReward(attacker.Side, reward);"),
                "Only enemy damage should grant positive attacker credit.");
            Assert.That(coordinator, Does.Contain("_active.ApplyImmediateTsvReward(target.Side, -reward);"),
                "Every attributed TSV loss must penalize the damaged side.");
            Assert.That(coordinator, Does.Contain("internal static void RecordUnattributedTsvLoss"));
            Assert.That(combat, Does.Contain("RecordUnattributedTsvLoss(this, -_tsvChange);"));
            Assert.That(fireBarge, Does.Contain("LogDamage(Health);"),
                "Fire Barge self-destruction must flow through unattributed TSV loss accounting.");

            Assert.That(yellowJacket, Does.Contain("LogDetonationDamage(Bomb.Power, this, ContactedShip);"));
            Assert.That(yellowJacket, Does.Contain("LogDetonationDamage(Bomb.Power, ContactedShip, this);"));
            Assert.That(yellowJacket, Does.Not.Contain("LogDamage("),
                "Yellow Jacket uses explicit directed hit accounting and must not also enter the unattributed path.");

            Assert.That(coordinator, Does.Not.Contain("CalculateTsvDeltaReward"),
                "Impact TSV shaping must not be applied a second time at episode completion.");
        }

        [Test]
        public void SampledMultiShipCurriculumUsesSideShuffleBagsWhileOneVsOneKeepsCartesianSampler()
        {
            string source = Read("Scripts", "Scenes", "RlOneVsOneMatchupSampler.cs");

            Assert.That(source, Does.Contain("if (_options.ShipsPerSide == 1)"));
            Assert.That(source, Does.Contain("_sampler = new RlOneVsOneMatchupSampler(options.BeeShipTypes, options.HumanShipTypes, seed);"),
                "Seeded 1v1 must keep the existing shuffled Cartesian sampler.");
            Assert.That(source, Does.Contain("internal sealed class RlShipTypeShuffleBag"));
            Assert.That(source, Does.Contain("_beeShuffleBag.Next()"));
            Assert.That(source, Does.Contain("_humanShuffleBag.Next()"));
            Assert.That(source, Does.Contain("if (_nextIndex >= _cycle.Count)\n        {\n            ShuffleCycle();"),
                "A side may only reshuffle after its current candidate cycle is exhausted.");
            Assert.That(source, Does.Not.Contain("return _currentMatchup.BeeShipType;\n        }\n        if (side == ConfigData.Configuration.HumanSide)\n        {\n            return _currentMatchup.HumanShipType;\n        }\n        throw"),
                "Sampled multi-ship selection must not reuse one pair for every team slot.");
        }

        private static string Read(params string[] parts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < parts.Length; i++)
            {
                path = Path.Combine(path, parts[i]);
            }
            return File.ReadAllText(path);
        }
    }
}
