using System;
using System.IO;
using System.Collections.Generic;
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
        public void PolicyAbiIncludesEpisodeLocalShipIdentityAndPrivateAlliedCommunication()
        {
            Type agent = RuntimeAssembly.GetType("RlOneVsOneAgent");
            Assert.That(RuntimeAssembly.GetStaticField(agent, "ShipIdentityObservationSize"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetStaticField(agent, "CommunicationObservationSize"), Is.EqualTo(4));
            Assert.That(RuntimeAssembly.GetStaticField(agent, "CommunicationContinuousActionCount"), Is.EqualTo(4));
            Assert.That(RuntimeAssembly.GetStaticField(agent, "ContinuousActionCount"), Is.EqualTo(16));
            Assert.That(RuntimeAssembly.GetStaticField(agent, "ObservationSize"), Is.EqualTo(16166));

            string perception = Read("Scripts", "Scenes", "RlCombatPerception.cs");
            Assert.That(perception, Does.Contain("AddShipIdentityObservation(sensor, ship);"));
            Assert.That(perception, Does.Contain("AddShipIdentityObservation(sensor, observed);"));
            Assert.That(perception, Does.Contain("AddAllySlots(sensor, _allyCandidates"));
            Assert.That(perception, Does.Contain("RlOneVsOneAgent.AddCommunicationObservations(sensor, ally);"));

            int enemyCollection = perception.IndexOf("CollectVisibleEnemies", StringComparison.Ordinal);
            int enemySlots = perception.IndexOf("AddEntitySlots(sensor, _enemyCandidates", enemyCollection, StringComparison.Ordinal);
            Assert.That(enemySlots, Is.GreaterThan(enemyCollection),
                "Enemy observations must use entity slots without the private allied communication tail.");
        }

        [Test]
        public void EpisodeShipIdentityUsesOneStableCollisionFreeScalarPerObservedShip()
        {
            Type identity = RuntimeAssembly.GetType("RlEpisodeShipIdentity");
            HashSet<float> values = new HashSet<float>();

            for (int ordinal = 0; ordinal < 130; ordinal++)
            {
                float value = (float)RuntimeAssembly.InvokeStatic(identity, "EncodeOrdinal", ordinal, 5, 17);
                Assert.That(value, Is.InRange(-1f, 1f));
                Assert.That(values.Add(value), Is.True, $"Duplicate scalar identity at ordinal {ordinal}.");
            }

            float stable = (float)RuntimeAssembly.InvokeStatic(identity, "EncodeOrdinal", 7, 5, 17);
            float repeated = (float)RuntimeAssembly.InvokeStatic(identity, "EncodeOrdinal", 7, 5, 17);
            float nextEpisodePermutation = (float)RuntimeAssembly.InvokeStatic(identity, "EncodeOrdinal", 7, 11, 29);
            Assert.That(repeated, Is.EqualTo(stable));
            Assert.That(nextEpisodePermutation, Is.Not.EqualTo(stable));

            string identitySource = Read("Scripts", "Scenes", "RlEpisodeShipIdentity.cs");
            Assert.That(identitySource, Does.Contain("EpisodeStates.Remove(level);"));
            Assert.That(identitySource, Does.Contain("RlOneVsOneScenarioSeed.IdentityStreamSalt"));
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
            Assert.That(schema, Does.Contain("ship-id=episode-permuted-scalar23"));
            Assert.That(schema, Does.Contain("ally=113-with-private-comm4"));
        }
    }
}
