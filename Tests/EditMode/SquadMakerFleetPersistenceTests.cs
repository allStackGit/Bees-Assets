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
        public void LeavingSquadMakerForLevelDoesNotDiscardPreparedLevelOptions()
        {
            string source = ReadSource();
            int unloadStart = source.IndexOf("private static void HandleSceneUnloaded");
            int loadStart = source.IndexOf("private static void HandleSceneLoaded");
            string unloadHandler = source.Substring(unloadStart, loadStart - unloadStart);

            Assert.That(unloadHandler, Does.Not.Contain("ConfigData.LevelOptions = null;"),
                "Normal Squad Maker -> Space transitions must preserve the prepared level options.");
        }

        [Test]
        public void BackingOutOfCustomEnemySelectionDiscardsPendingTransactionOnReturnToSquadMaker()
        {
            string source = ReadSource();

            Assert.That(source, Does.Contain("SceneManager.sceneLoaded += HandleSceneLoaded;"));
            Assert.That(source, Does.Contain("private static void HandleSceneLoaded"));
            Assert.That(source, Does.Contain("ConfigData.IsUserLoadingCustomEnemySquads &&"));
            Assert.That(source, Does.Contain("ConfigData.SquadMakerSide == ConfigData.Configuration.SquadMakerFirstSide"));
            Assert.That(source, Does.Contain("ConfigData.IsUserLoadingCustomEnemySquads = false;"));
            Assert.That(source, Does.Contain("ConfigData.LevelOptions = null;"));
        }
    }
}
