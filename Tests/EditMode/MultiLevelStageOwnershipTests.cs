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
            string stage = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Scenes", "Stage.cs"));
            string levelReset = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Reset.cs"));

            Assert.That(stage, Does.Contain("SetConfigOptionsAndOverrides(Level level)"));
            Assert.That(stage, Does.Contain("level.CurrentLevelOptions.EnemySquadGenerationCount"));
            Assert.That(stage, Does.Contain("level.CurrentLevelOptions.EnemyShipTypeOption"));
            Assert.That(levelReset, Does.Contain("Stage.SetConfigOptionsAndOverrides(this);"));
        }

        [Test]
        public void PrimaryCameraUsesPrimaryLevelWorldOffset()
        {
            string stage = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Scenes", "Stage.cs"));
            Assert.That(stage, Does.Contain("_camera_localizedPosition = DefaultCameraPosition + PrimaryLevel.GetPosition();"));
        }
    }
}
