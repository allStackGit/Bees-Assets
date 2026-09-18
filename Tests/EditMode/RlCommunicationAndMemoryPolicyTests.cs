using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlCommunicationAndMemoryPolicyTests
    {
        private static string Read(params string[] parts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < parts.Length; i++)
            {
                path = Path.Combine(path, parts[i]);
            }
            return File.ReadAllText(path);
        }

        [Test]
        public void PolicyAbiIncludesExactShipIdentityAndPrivateAlliedCommunication()
        {
            Type agent = RuntimeAssembly.GetType("RlOneVsOneAgent");
            Assert.That(RuntimeAssembly.GetStaticField(agent, "ShipIdBitCount"), Is.EqualTo(64));
            Assert.That(RuntimeAssembly.GetStaticField(agent, "CommunicationObservationSize"), Is.EqualTo(4));
            Assert.That(RuntimeAssembly.GetStaticField(agent, "CommunicationContinuousActionCount"), Is.EqualTo(4));
            Assert.That(RuntimeAssembly.GetStaticField(agent, "ContinuousActionCount"), Is.EqualTo(16));
            Assert.That(RuntimeAssembly.GetStaticField(agent, "ObservationSize"), Is.EqualTo(24356));

            string perception = Read("Scripts", "Scenes", "RlCombatPerception.cs");
            Assert.That(perception, Does.Contain("AddShipIdBits(sensor, ship.Id);"));
            Assert.That(perception, Does.Contain("AddShipIdBits(sensor, observed.Id);"));
            Assert.That(perception, Does.Contain("AddAllySlots(sensor, _allyCandidates"));
            Assert.That(perception, Does.Contain("RlOneVsOneAgent.AddCommunicationObservations(sensor, ally);"));

            int enemyCollection = perception.IndexOf("CollectVisibleEnemies", StringComparison.Ordinal);
            int enemySlots = perception.IndexOf("AddEntitySlots(sensor, _enemyCandidates", enemyCollection, StringComparison.Ordinal);
            Assert.That(enemySlots, Is.GreaterThan(enemyCollection),
                "Enemy observations must use entity slots without the private allied communication tail.");
        }

        [Test]
        public void CommunicationActionsAreStoredPerBoundShipAndClearedWithBinding()
        {
            string agent = Read("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            Assert.That(agent, Does.Contain("ShipCommunications[_ship] = new Vector4("));
            Assert.That(agent, Does.Contain("ShipCommunications[_ship] = Vector4.zero;"));
            Assert.That(agent, Does.Contain("ShipCommunications.Remove(_ship);"));
            Assert.That(agent, Does.Contain("ShipCommunications.TryGetValue(ally, out Vector4 communication)"));
        }

        [Test]
        public void CanonicalTrainerUsesRequestedLargerRecurrentPolicy()
        {
            string trainer = Read("Training", "rl_1v1_config.yaml");
            string schema = Read("Scripts", "Scenes", "RlPolicySchema.cs");

            Assert.That(trainer, Does.Contain("hidden_units: 1024"));
            Assert.That(trainer, Does.Contain("num_layers: 4"));
            Assert.That(trainer, Does.Contain("sequence_length: 256"));
            Assert.That(trainer, Does.Contain("memory_size: 512"));
            Assert.That(schema, Does.Contain("network=lstm-1024x4-mem512-seq256"));
            Assert.That(schema, Does.Contain("ship-id=exact-64bit-categorical"));
            Assert.That(schema, Does.Contain("ally=176-with-private-comm4"));
        }
    }
}
