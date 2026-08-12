using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class UserDataBootstrapAndDialogueTemplateTests
    {
        [Test]
        public void ServerBackedProfilesAlwaysReadBeforeDefaultsAreCreated()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "UserData.cs"));

            Assert.That(source, Does.Contain("if (!ConfigData.Configuration.UseLocalStorage)"));
            Assert.That(source, Does.Contain("shouldFileExist = true;"));
            Assert.That(source, Does.Contain("json = file.LoadJsonObject();"));
        }

        [Test]
        public void RemoteMissingDataCreationIsTrackedSeparatelyFromExistingBlankProfiles()
        {
            string dataFile = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "DataFile.cs"));
            string mainMenu = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "MainMenu.cs"));

            Assert.That(dataFile, Does.Contain("WasCreatedFromMissingStorage"));
            Assert.That(dataFile, Does.Contain("_request != null && !_isDataLoaded"));
            Assert.That(mainMenu, Does.Contain("progressFile.WasCreatedFromMissingStorage"));
            Assert.That(mainMenu, Does.Contain("CommanderNameDialogue?.SetActive(needsCommanderName);"));
        }

        [Test]
        public void DialogueCloneIsHiddenBeforeHierarchyConfiguration()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "Dialogue.cs"));

            int instantiate = source.IndexOf("_dialogue = GameObject.Instantiate(prefab);");
            int hide = source.IndexOf("_dialogue.SetActive(false);", instantiate);
            int firstLookup = source.IndexOf("_titleBox = _dialogue.transform.Find", instantiate);

            Assert.That(instantiate, Is.GreaterThanOrEqualTo(0));
            Assert.That(hide, Is.GreaterThan(instantiate));
            Assert.That(firstLookup, Is.GreaterThan(hide));
        }

        [Test]
        public void GenericTextPrefabsDoNotCarryExitConfirmationContent()
        {
            string small = File.ReadAllText(Path.Combine(
                Application.dataPath, "Prefabs", "UI", "Text", "Block Text - Small.prefab"));
            string large = File.ReadAllText(Path.Combine(
                Application.dataPath, "Prefabs", "UI", "Text", "Block Text - Large.prefab"));

            const string staleExitWarning = "All progress on the level will be lost";
            Assert.That(small, Does.Not.Contain(staleExitWarning));
            Assert.That(large, Does.Not.Contain(staleExitWarning));
        }

        [Test]
        public void MainMenuCommanderPromptRemainsHiddenByDefaultAndNamedCorrectly()
        {
            string scene = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scenes", "Main Menu.unity"));
            string mainMenu = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "MainMenu.cs"));

            Assert.That(scene, Does.Contain("value: Welcome Commander!"));
            Assert.That(scene, Does.Contain("value: Choose Commander Name"));
            Assert.That(scene, Does.Contain("propertyPath: m_IsActive\n      value: 0"));
            Assert.That(mainMenu, Does.Contain("CommanderNameDialogue?.SetActive(false);"));
        }
    }
}
