using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CarrierDeckVariantsTests
    {
        private const string VariantTypeName = "Assets.Scripts.Entities.Ships.CarrierDeckVariants";
        private const string PresenterTypeName = "Assets.Scripts.UIComponents.SquadMakerCarrierDeckPresenter";
        private const string ResourcePath = "Sprites/carrier_alts_deck";
        private const string OverlayObjectName = "Carrier Deck Variant";

        [TestCase(195, 98, 107, 0)]
        [TestCase(193, 111, 56, 1)]
        [TestCase(220, 213, 74, 2)]
        [TestCase(147, 204, 93, 3)]
        [TestCase(68, 137, 108, 4)]
        [TestCase(79, 180, 79, 5)]
        [TestCase(98, 180, 197, 6)]
        [TestCase(95, 108, 195, 7)]
        [TestCase(155, 124, 171, 8)]
        [TestCase(214, 135, 189, 9)]
        [TestCase(152, 104, 104, 10)]
        [TestCase(184, 196, 221, 11)]
        [TestCase(105, 168, 63, 12)]
        public void AuthoredAccentColorsSelectTheirDeck(int red, int green, int blue, int expectedDeck)
        {
            int actualDeck = GetDeckIndex(ColorFromBytes(red, green, blue));
            Assert.That(actualDeck, Is.EqualTo(expectedDeck));
        }

        [Test]
        public void NearbyColorStillSelectsDeckButUnrelatedColorDoesNot()
        {
            Assert.That(GetDeckIndex(ColorFromBytes(205, 93, 102)), Is.EqualTo(0),
                "A user should not need to hit the exact authored red to discover the red-planet deck.");
            Assert.That(GetDeckIndex(Color.black), Is.EqualTo(-1),
                "Deck variants should be optional rather than matching every possible squad color.");
        }

        [Test]
        public void SheetResourceIsSplitIntoThirteenEqualNinetySixByOneTwelveSprites()
        {
            TextAsset source = Resources.Load<TextAsset>(ResourcePath);
            Assert.That(source, Is.Not.Null, "Carrier deck sheet must remain runtime-loadable from Resources.");
            Assert.That(source.bytes.Length, Is.GreaterThan(0));

            Type type = RuntimeAssembly.GetType(VariantTypeName);
            AssertRect((Rect)RuntimeAssembly.InvokeStatic(type, "GetSpriteRect", 0), 0f, 112f);
            AssertRect((Rect)RuntimeAssembly.InvokeStatic(type, "GetSpriteRect", 6), 576f, 112f);
            AssertRect((Rect)RuntimeAssembly.InvokeStatic(type, "GetSpriteRect", 7), 0f, 0f);
            AssertRect((Rect)RuntimeAssembly.InvokeStatic(type, "GetSpriteRect", 12), 480f, 0f);
        }

        [Test]
        public void MatchingColorBuildsTheSelectedDeckSpriteFromTheAuthoredSheet()
        {
            Type type = RuntimeAssembly.GetType(VariantTypeName);
            Sprite sprite = (Sprite)RuntimeAssembly.InvokeStatic(
                type,
                "GetDeckSprite",
                ColorFromBytes(195, 98, 107));

            Assert.That(sprite, Is.Not.Null);
            Assert.That(sprite.name, Is.EqualTo("carrier_alts_deck_0"));
            Assert.That(sprite.texture.width, Is.EqualTo(672));
            Assert.That(sprite.texture.height, Is.EqualTo(224));
            Assert.That(sprite.rect.width, Is.EqualTo(96f));
            Assert.That(sprite.rect.height, Is.EqualTo(112f));
        }

        [Test]
        public void UiDeckVariantFillsBaseImageAndCanBeHiddenForReuse()
        {
            GameObject iconObject = new GameObject(
                "Carrier Icon",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));

            try
            {
                Image baseImage = iconObject.GetComponent<Image>();
                Sprite deckSprite = (Sprite)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(VariantTypeName),
                    "GetDeckSprite",
                    ColorFromBytes(195, 98, 107));

                RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(VariantTypeName),
                    "SetUiDeckVariant",
                    baseImage,
                    deckSprite);

                Transform overlayTransform = iconObject.transform.Find(OverlayObjectName);
                Assert.That(overlayTransform, Is.Not.Null);
                Image overlay = overlayTransform.GetComponent<Image>();
                Assert.That(overlay, Is.Not.Null);
                Assert.That(overlay.enabled, Is.True);
                Assert.That(overlay.sprite, Is.SameAs(deckSprite));
                Assert.That(overlay.raycastTarget, Is.False);
                AssertVector(overlay.rectTransform.anchorMin, Vector2.zero);
                AssertVector(overlay.rectTransform.anchorMax, Vector2.one);
                AssertVector(overlay.rectTransform.offsetMin, Vector2.zero);
                AssertVector(overlay.rectTransform.offsetMax, Vector2.zero);

                RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(VariantTypeName),
                    "SetUiDeckVariant",
                    baseImage,
                    null);
                Assert.That(overlay.enabled, Is.False,
                    "A reused squad-info/list image must not retain the previous carrier deck.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(iconObject);
            }
        }

        [Test]
        public void SquadMakerPresenterShowsDeckOnlyForMatchingCustomCarrierIcons()
        {
            GameObject iconObject = new GameObject(
                "Squad Icon",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));

            try
            {
                Image image = iconObject.GetComponent<Image>();
                Type presenterType = RuntimeAssembly.GetType(PresenterTypeName);
                Color matchingColor = ColorFromBytes(195, 98, 107);

                RuntimeAssembly.InvokeStatic(
                    presenterType,
                    "ApplyDeckVariant",
                    image,
                    true,
                    true,
                    matchingColor);

                Image overlay = iconObject.transform.Find(OverlayObjectName)?.GetComponent<Image>();
                Assert.That(overlay, Is.Not.Null);
                Assert.That(overlay.enabled, Is.True);
                Assert.That(overlay.sprite.name, Is.EqualTo("carrier_alts_deck_0"));

                RuntimeAssembly.InvokeStatic(
                    presenterType,
                    "ApplyDeckVariant",
                    image,
                    false,
                    true,
                    matchingColor);
                Assert.That(overlay.enabled, Is.False,
                    "The shared Squad Maker surface must clear a previous carrier overlay when it represents another ship type.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(iconObject);
            }
        }

        private static int GetDeckIndex(Color color)
        {
            return (int)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(VariantTypeName),
                "GetDeckIndex",
                color);
        }

        private static Color ColorFromBytes(int red, int green, int blue)
        {
            return new Color(red / 255f, green / 255f, blue / 255f, 1f);
        }

        private static void AssertRect(Rect rect, float x, float y)
        {
            Assert.That(rect.x, Is.EqualTo(x));
            Assert.That(rect.y, Is.EqualTo(y));
            Assert.That(rect.width, Is.EqualTo(96f));
            Assert.That(rect.height, Is.EqualTo(112f));
        }

        private static void AssertVector(Vector2 actual, Vector2 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.001f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.001f));
        }
    }
}
