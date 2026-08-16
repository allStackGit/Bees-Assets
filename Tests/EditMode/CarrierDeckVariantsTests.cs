using System;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CarrierDeckVariantsTests
    {
        private const string VariantTypeName = "Assets.Scripts.Entities.Ships.CarrierDeckVariants";
        private const string ResourcePath = "Sprites/carrier_alts_deck";

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
        public void SheetIsThirteenEqualNinetySixByOneTwelveSprites()
        {
            Texture2D texture = Resources.Load<Texture2D>(ResourcePath);
            Assert.That(texture, Is.Not.Null, "Carrier deck sheet must remain runtime-loadable from Resources.");
            Assert.That(texture.width, Is.EqualTo(672));
            Assert.That(texture.height, Is.EqualTo(224));

            Type type = RuntimeAssembly.GetType(VariantTypeName);
            AssertRect((Rect)RuntimeAssembly.InvokeStatic(type, "GetSpriteRect", 0), 0f, 112f);
            AssertRect((Rect)RuntimeAssembly.InvokeStatic(type, "GetSpriteRect", 6), 576f, 112f);
            AssertRect((Rect)RuntimeAssembly.InvokeStatic(type, "GetSpriteRect", 7), 0f, 0f);
            AssertRect((Rect)RuntimeAssembly.InvokeStatic(type, "GetSpriteRect", 12), 480f, 0f);
        }

        [Test]
        public void MatchingColorBuildsOnlyTheSelectedDeckSprite()
        {
            Type type = RuntimeAssembly.GetType(VariantTypeName);
            Sprite sprite = (Sprite)RuntimeAssembly.InvokeStatic(
                type,
                "GetDeckSprite",
                ColorFromBytes(195, 98, 107));

            Assert.That(sprite, Is.Not.Null);
            Assert.That(sprite.name, Is.EqualTo("carrier_alts_deck_0"));
            Assert.That(sprite.rect.width, Is.EqualTo(96f));
            Assert.That(sprite.rect.height, Is.EqualTo(112f));
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
    }
}
