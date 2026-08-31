using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerSupplyCapacityPresentationGuardTests
    {
        private const string GuardTypeName =
            "Assets.Scripts.UI_Components.SquadMakerSupplyCapacityPresentationGuard";
        private const string TextMeshProTypeName = "TMPro.TextMeshProUGUI";

        [Test]
        public void SupplyCapacityTextFillsWarningBackgroundAndCentersGlyphs()
        {
            Type textMeshProType = ResolveLoadedType(TextMeshProTypeName);
            Assert.That(textMeshProType, Is.Not.Null,
                "TextMeshProUGUI must be available through the runtime assembly without adding a direct test-assembly reference.");

            PropertyInfo alignmentProperty = textMeshProType.GetProperty("alignment");
            PropertyInfo marginProperty = textMeshProType.GetProperty("margin");
            Assert.That(alignmentProperty, Is.Not.Null);
            Assert.That(marginProperty, Is.Not.Null);

            GameObject backgroundObject = new GameObject("Supply Capacity Background", typeof(RectTransform));
            RectTransform background = backgroundObject.GetComponent<RectTransform>();
            background.sizeDelta = new Vector2(270f, 35f);

            GameObject labelObject = new GameObject("Supply Capacity Label", typeof(RectTransform));
            RectTransform label = labelObject.GetComponent<RectTransform>();
            label.SetParent(background, false);
            label.anchorMin = new Vector2(0.5f, 0.5f);
            label.anchorMax = new Vector2(0.5f, 0.5f);
            label.pivot = new Vector2(0f, 1f);
            label.anchoredPosition = new Vector2(13f, -5f);
            label.sizeDelta = new Vector2(220f, 22f);

            GameObject textObject = new GameObject(
                "Text",
                typeof(RectTransform),
                typeof(CanvasRenderer));
            Component text = textObject.AddComponent(textMeshProType);
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.SetParent(label, false);
            textRect.anchorMin = new Vector2(0.5f, 0.5f);
            textRect.anchorMax = new Vector2(0.5f, 0.5f);
            textRect.pivot = new Vector2(0f, 1f);
            textRect.anchoredPosition = new Vector2(-9f, 4f);
            textRect.sizeDelta = new Vector2(180f, 18f);

            object topLeftAlignment = Enum.Parse(alignmentProperty.PropertyType, "TopLeft");
            object centeredAlignment = Enum.Parse(alignmentProperty.PropertyType, "Center");
            alignmentProperty.SetValue(text, topLeftAlignment);
            marginProperty.SetValue(text, new Vector4(4f, 2f, 7f, 5f));

            try
            {
                RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CenterPresentation",
                    background,
                    label,
                    text);

                AssertStretched(label);
                AssertStretched(textRect);
                Assert.That(alignmentProperty.GetValue(text), Is.EqualTo(centeredAlignment));
                Assert.That((Vector4)marginProperty.GetValue(text), Is.EqualTo(Vector4.zero));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(backgroundObject);
            }
        }

        private static Type ResolveLoadedType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int index = 0; index < assemblies.Length; index++)
            {
                Type type = assemblies[index].GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static void AssertStretched(RectTransform rect)
        {
            Assert.That(rect.anchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(rect.anchorMax, Is.EqualTo(Vector2.one));
            Assert.That(rect.pivot, Is.EqualTo(new Vector2(0.5f, 0.5f)));
            Assert.That(rect.anchoredPosition, Is.EqualTo(Vector2.zero));
            Assert.That(rect.sizeDelta, Is.EqualTo(Vector2.zero));
            Assert.That(rect.localScale, Is.EqualTo(Vector3.one));
        }
    }
}
