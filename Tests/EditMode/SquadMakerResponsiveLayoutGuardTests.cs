using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerResponsiveLayoutGuardTests
    {
        private const string GuardTypeName = "Assets.Scripts.UI_Components.SquadMakerResponsiveLayoutGuard";

        [Test]
        public void SimultaneouslyActiveHoverDescriptionsDoNotConsumeLayoutRows()
        {
            GameObject column = new GameObject(
                "Chosen Squads Column",
                typeof(RectTransform),
                typeof(VerticalLayoutGroup));
            GameObject startDescription = CreateDescription("Start Description", column.transform);
            GameObject testDescription = CreateDescription("Test Description", column.transform);

            startDescription.SetActive(false);
            testDescription.SetActive(false);

            try
            {
                System.Type guardType = RuntimeAssembly.GetType(GuardTypeName);

                RuntimeAssembly.InvokeStatic(
                    guardType,
                    "SetDescriptionVisibility",
                    startDescription,
                    false);
                RuntimeAssembly.InvokeStatic(
                    guardType,
                    "SetDescriptionVisibility",
                    testDescription,
                    false);

                Assert.That(startDescription.activeSelf, Is.True);
                Assert.That(testDescription.activeSelf, Is.True,
                    "START and TEST descriptions may coexist when both buttons are available.");

                AssertIgnoredByLayout(startDescription);
                AssertIgnoredByLayout(testDescription);
                AssertCanvasGroup(startDescription, 0f);
                AssertCanvasGroup(testDescription, 0f);

                RuntimeAssembly.InvokeStatic(
                    guardType,
                    "SetDescriptionVisibility",
                    startDescription,
                    true);

                AssertIgnoredByLayout(startDescription);
                AssertIgnoredByLayout(testDescription);
                AssertCanvasGroup(startDescription, 1f);
                AssertCanvasGroup(testDescription, 0f);
            }
            finally
            {
                Object.DestroyImmediate(column);
            }
        }

        private static GameObject CreateDescription(string name, Transform parent)
        {
            GameObject description = new GameObject(name, typeof(RectTransform));
            description.transform.SetParent(parent, false);
            return description;
        }

        private static void AssertIgnoredByLayout(GameObject description)
        {
            LayoutElement layout = description.GetComponent<LayoutElement>();
            Assert.That(layout, Is.Not.Null);
            Assert.That(layout.ignoreLayout, Is.True,
                "Hover-only description text must not reserve space in the level-details column.");
        }

        private static void AssertCanvasGroup(GameObject description, float expectedAlpha)
        {
            CanvasGroup group = description.GetComponent<CanvasGroup>();
            Assert.That(group, Is.Not.Null);
            Assert.That(group.alpha, Is.EqualTo(expectedAlpha));
            Assert.That(group.interactable, Is.False);
            Assert.That(group.blocksRaycasts, Is.False);
        }
    }
}
