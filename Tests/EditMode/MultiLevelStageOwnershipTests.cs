using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class MultiLevelStageOwnershipTests
    {
        [Test]
        public void StageAppliesOptionsToExplicitConfiguringLevel()
        {
            string config = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Scenes", "StageConfigOptions.cs"));
            string levelReset = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Reset.cs"));

            Assert.That(config, Does.Contain("public static void Apply(Stage stage, Level level)"));
            Assert.That(config, Does.Contain("level.CurrentLevelOptions.EnemySquadGenerationCount"));
            Assert.That(config, Does.Contain("level.CurrentLevelOptions.EnemyShipTypeOption"));
            Assert.That(config, Does.Contain("destination.Clear();"));
            Assert.That(config, Does.Contain("destination.AddRange(source);"));
            Assert.That(levelReset, Does.Contain("StageConfigOptions.Apply(Stage, this);"));
        }

        [Test]
        public void PrimaryCameraUsesPrimaryLevelWorldOffset()
        {
            string stage = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Scenes", "Stage.cs"));
            Assert.That(stage, Does.Contain("_camera_localizedPosition = DefaultCameraPosition + PrimaryLevel.GetPosition();"));
        }
    }
}
