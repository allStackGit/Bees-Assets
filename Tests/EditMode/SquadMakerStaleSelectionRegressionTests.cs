using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerStaleSelectionRegressionTests
    {
        private static string ReadSquadMaker()
        {
            return File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Scenes",
                "SquadMaker.cs"))
                .Replace("\r\n", "\n");
        }

        [Test]
        public void DelayedSquadSelectionIgnoresMissingOrStaleSquads()
        {
            string source = ReadSquadMaker();

            StringAssert.Contains(
                "private bool TryGetSavedSquadFromLabel(GameObject label, out SavedSquad squad)",
                source);
            StringAssert.Contains(
                "!long.TryParse(label.name.Substring(hashIndex + 1), out long id)",
                source);
            StringAssert.Contains("squad = ConfigData.CurrentShips.GetSavedSquad(id);", source);
            StringAssert.Contains("if (_squadToLoad == null)", source);
            StringAssert.Contains("if (_squadToChoose == null)", source);
        }

        [Test]
        public void SquadInfoHoverIgnoresStaleLabelsAndMissingIcons()
        {
            string source = ReadSquadMaker();

            StringAssert.Contains(
                "if (!TryGetSavedSquadFromLabel(label, out SavedSquad squad))",
                source);
            StringAssert.Contains(
                "Transform squadIconTransform = label.transform.Find(\"Icon Container/Ship Icon\");",
                source);
            StringAssert.Contains(
                "UnityEngine.UI.Image squadIconImage = squadIconTransform != null ? squadIconTransform.GetComponent<UnityEngine.UI.Image>() : null;",
                source);
            StringAssert.Contains("if (image == null || squadIconImage == null)", source);
        }
    }
}
