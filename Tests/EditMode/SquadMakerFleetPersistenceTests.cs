using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerFleetPersistenceTests
    {
        private static string ReadSource() => File.ReadAllText(Path.Combine(
            Application.dataPath, "Scripts", "Scenes", "SquadMakerPersistence.cs"));

        [Test]
        public void LeavingSquadMakerPersistsDirectFleetEdits()
        {
            string source = ReadSource();

            Assert.That(source, Does.Contain("SceneManager.sceneUnloaded += HandleSceneUnloaded;"));
            Assert.That(source, Does.Contain("if (scene.name != SquadMakerScene)"));
            Assert.That(source, Does.Contain("ConfigData.CurrentShips?.SaveFleetData();"));
        }

        [Test]
        public void BackingOutOfCustomEnemySelectionDiscardsPendingTransaction()
        {
            string source = ReadSource();

            Assert.That(source, Does.Contain("ConfigData.IsUserLoadingCustomEnemySquads &&"));
            Assert.That(source, Does.Contain("ConfigData.SquadMakerSide == ConfigData.Configuration.SquadMakerFirstSide"));
            Assert.That(source, Does.Contain("ConfigData.IsUserLoadingCustomEnemySquads = false;"));
            Assert.That(source, Does.Contain("ConfigData.LevelOptions = null;"));
        }
    }
}
