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
        public void CanonicalDesignDocumentMatchesExecutableAbiV6()
        {
            string design = File.ReadAllText(Path.Combine(Application.dataPath, "RL_DESIGN.md"));

            Assert.That(design, Does.Contain("## 4. Canonical Policy ABI v6"));
            Assert.That(design, Does.Contain("- ABI version: `6`"));
            Assert.That(design, Does.Contain("- vector observations: `4685`"));
            Assert.That(design, Does.Contain("- continuous actions: `34`"));
            Assert.That(design, Does.Contain("`512` hidden units, `3` hidden layers"));
            Assert.That(design, Does.Contain("`hidden_units: 512`"));
            Assert.That(design, Does.Contain("`num_layers: 3`"));
            Assert.That(design, Does.Contain("distinct random quarter-turn coordinate frame"));
            Assert.That(design, Does.Contain("RL weapon readiness is latched"));
            Assert.That(design, Does.Not.Contain("Canonical Policy ABI v4"));
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
            Assert.That(coordinator, Does.Contain("coordinator.ApplyImmediateTsvReward(attacker.Side, reward);"),
                "Only enemy damage should grant positive attacker credit.");
            Assert.That(coordinator, Does.Contain("coordinator.ApplyImmediateTsvReward(target.Side, -reward);"),
                "Every attributed TSV loss must penalize the damaged side.");
            Assert.That(coordinator, Does.Contain("internal static void RecordUnattributedTsvLoss"));
            Assert.That(combat, Does.Contain("RecordUnattributedTsvLoss(this, -_tsvChange);"));
            Assert.That(fireBarge, Does.Contain("LogDamage(Health, \"FireBarge\", true);"),
                "Fire Barge self-destruction must flow through unattributed TSV loss accounting while preserving self-damage diagnostics.");

            Assert.That(yellowJacket, Does.Contain("LogDetonationDamage(Bomb.Power, this, ContactedShip, this);"));
            Assert.That(yellowJacket, Does.Contain("LogDetonationDamage(Bomb.Power, ContactedShip, this, this);"));
            Assert.That(yellowJacket, Does.Not.Contain("LogDamage("),
                "Yellow Jacket uses explicit directed hit accounting and must not also enter the unattributed path.");

            Assert.That(coordinator, Does.Not.Contain("CalculateTsvDeltaReward"),
                "Impact TSV shaping must not be applied a second time at episode completion.");
        }

        [Test]
        public void SampledMultiShipCurriculumUsesUniformCompositionsWhileOneVsOneKeepsCartesianSampler()
        {
            string source = Read("Scripts", "Scenes", "RlOneVsOneMatchupSampler.cs");

            Assert.That(source, Does.Contain("if (_options.ShipsPerSide == 1)"));
            Assert.That(source, Does.Contain("_sampler = new RlOneVsOneMatchupSampler(options.BeeShipTypes, options.HumanShipTypes, seed);"),
                "Seeded 1v1 must keep the existing shuffled Cartesian sampler.");
            Assert.That(source, Does.Contain("internal sealed class RlShipCompositionSampler"));
            Assert.That(source, Does.Contain("CombinationCount = Choose(_shipTypes.Length + shipsPerSide - 1, shipsPerSide);"));
            Assert.That(source, Does.Contain("ValidCombinationCount = CombinationCount - weaponlessCombinationCount;"));
            Assert.That(source, Does.Contain("if (!RlShipCombatCapability.HasAnyWeapon(composition))"),
                "Entirely weaponless sampled sides must be rejected.");
            Assert.That(source, Does.Contain("ShuffleSlots(composition);"),
                "Accepted unordered compositions must not become tied to formation slot order.");
            Assert.That(source, Does.Contain("CopyComposition(_beeCompositionSampler.Next(), _currentBeeComposition);"));
            Assert.That(source, Does.Contain("CopyComposition(_humanCompositionSampler.Next(), _currentHumanComposition);"));
            Assert.That(source, Does.Not.Contain("RlShipTypeShuffleBag"),
                "The old no-repeat shuffle bag is no longer the multi-ship sampling contract.");
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
