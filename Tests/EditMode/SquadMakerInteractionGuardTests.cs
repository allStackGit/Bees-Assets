using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerInteractionGuardTests
    {
        private const string GuardTypeName = "Assets.Scripts.UIComponents.SquadMakerInteractionGuard";

        [TestCase("Saved", "Editor", "Load")]
        [TestCase("Saved", "ChosenList", "Choose")]
        [TestCase("Saved", "SavedList", "None")]
        [TestCase("Chosen", "Editor", "UnchooseAndLoad")]
        [TestCase("Chosen", "SavedList", "Unchoose")]
        [TestCase("Chosen", "ChosenList", "None")]
        [TestCase("Chosen", "None", "None")]
        public void DragSourceAndDropTargetResolveToExpectedSquadAction(
            string sourceName,
            string targetName,
            string expectedActionName)
        {
            Type guardType = RuntimeAssembly.GetType(GuardTypeName);
            Type sourceType = guardType.GetNestedType("SquadListSource", BindingFlags.NonPublic);
            Type targetType = guardType.GetNestedType("SquadDropTarget", BindingFlags.NonPublic);
            Assert.That(sourceType, Is.Not.Null);
            Assert.That(targetType, Is.Not.Null);

            object source = Enum.Parse(sourceType, sourceName);
            object target = Enum.Parse(targetType, targetName);
            object action = RuntimeAssembly.InvokeStatic(guardType, "ResolveDropAction", source, target);

            Assert.That(action.ToString(), Is.EqualTo(expectedActionName));
        }

        [TestCase("Saved Squad - First Fleet #17", 17)]
        [TestCase("Chosen Squad - Name with # inside #42", 42)]
        [TestCase("Saved Squad - Generated #-7", -7)]
        public void SquadRowIdentityUsesFinalHashIdContract(string rowName, int expectedId)
        {
            Type guardType = RuntimeAssembly.GetType(GuardTypeName);
            MethodInfo method = guardType.GetMethod(
                "TryParseSquadId",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(method, Is.Not.Null);

            object[] arguments = { rowName, 0 };
            bool parsed = (bool)method.Invoke(null, arguments);

            Assert.That(parsed, Is.True);
            Assert.That((int)arguments[1], Is.EqualTo(expectedId));
        }

        [Test]
        public void HoverDescriptionAboveBottomButtonRemainsInsideOverlay()
        {
            Rect overlay = new Rect(-200f, -400f, 400f, 800f);
            Rect button = new Rect(-80f, -350f, 160f, 50f);
            Vector2 size = new Vector2(300f, 90f);

            Vector2 position = (Vector2)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateHoverDescriptionPosition",
                button,
                size,
                overlay,
                8f,
                8f);

            Assert.That(position.y, Is.GreaterThanOrEqualTo(button.yMax + 8f));
            AssertDescriptionInsideOverlay(position, size, overlay, 8f);
        }

        [Test]
        public void HoverDescriptionNearTopButtonFlipsBelowAndRemainsInsideOverlay()
        {
            Rect overlay = new Rect(-200f, -400f, 400f, 800f);
            Rect button = new Rect(-80f, 330f, 160f, 50f);
            Vector2 size = new Vector2(300f, 90f);

            Vector2 position = (Vector2)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateHoverDescriptionPosition",
                button,
                size,
                overlay,
                8f,
                8f);

            Assert.That(position.y + size.y, Is.LessThanOrEqualTo(button.yMin - 8f));
            AssertDescriptionInsideOverlay(position, size, overlay, 8f);
        }

        [Test]
        public void InteractionGuardKeepsDropsOnExistingSquadMakerValidationPaths()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "UI Components",
                "SquadMakerInteractionGuard.cs"));

            Assert.That(source, Does.Contain("ResolveListViewport"));
            Assert.That(source, Does.Contain("scroll.viewport"));
            Assert.That(source, Does.Contain("Squad Maker Column"));
            Assert.That(source, Does.Contain("ConfirmLoadSquad()"));
            Assert.That(source, Does.Contain("ConfirmChooseSquad()"));
            Assert.That(source, Does.Contain("ConfirmUnchooseSquad(chosenRow)"));
            Assert.That(source, Does.Contain("LoadSquadConfirmation.IsOpen"));
            Assert.That(source, Does.Contain("Squad Maker Hover Text Overlay"));
        }

        private static void AssertDescriptionInsideOverlay(
            Vector2 position,
            Vector2 size,
            Rect overlay,
            float margin)
        {
            float halfWidth = size.x * 0.5f;
            Assert.That(position.x - halfWidth,
                Is.GreaterThanOrEqualTo(overlay.xMin + margin - 0.001f));
            Assert.That(position.x + halfWidth,
                Is.LessThanOrEqualTo(overlay.xMax - margin + 0.001f));
            Assert.That(position.y,
                Is.GreaterThanOrEqualTo(overlay.yMin + margin - 0.001f));
            Assert.That(position.y + size.y,
                Is.LessThanOrEqualTo(overlay.yMax - margin + 0.001f));
        }
    }
}
