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

            Assert.That(scene, Does.Contain("value: Welcome Commander!"));
            Assert.That(scene, Does.Contain("value: Choose Commander Name"));
            Assert.That(scene, Does.Contain("propertyPath: m_IsActive\n      value: 0"));
        }
    }
}
