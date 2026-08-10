using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerFleetPersistenceTests
    {
        [Test]
        public void LeavingSquadMakerPersistsDirectFleetEdits()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "SquadMakerPersistence.cs"));

            Assert.That(source, Does.Contain("SceneManager.sceneUnloaded += HandleSceneUnloaded;"));
            Assert.That(source, Does.Contain("scene.name != SquadMakerScene || ConfigData.CurrentShips == null"));
            Assert.That(source, Does.Contain("ConfigData.CurrentShips.SaveFleetData();"));
        }
    }
}
