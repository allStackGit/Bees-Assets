using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ShipBuilderCloseButtonRegressionTests
    {
        [Test]
        public void TextCloseButtonsAreNotForcedToIconDimensions()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "UI Components",
                "UiSizingCompatibilityGuard.cs"));

            StringAssert.Contains("bool hasTextLabel =", source);
            StringAssert.Contains("button.gameObject.name == \"Close Button\" && !hasTextLabel", source);
            StringAssert.Contains("button.GetComponentInChildren<TMP_Text>(true)", source);
            StringAssert.Contains("button.GetComponentInChildren<Text>(true)", source);
            StringAssert.Contains("rect.sizeDelta = new Vector2(16f, 16f);", source);
        }
    }
}
