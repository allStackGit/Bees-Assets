using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PooledShipReferenceCleanupTests
    {
        [Test]
        public void RemoveShipClearsDamageAndSpottingReferencesBeforeRelease()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs");
            string source = File.ReadAllText(path);

            int methodStart = source.IndexOf("public void RemoveShip(Ship ship)");
            int methodEnd = source.IndexOf("public void AddDeadBody", methodStart);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(methodEnd, Is.GreaterThan(methodStart));

            string method = source.Substring(methodStart, methodEnd - methodStart);
            int damageCleanup = method.IndexOf("for (int statusIndex = statuses.Count - 1; statusIndex >= 0; statusIndex--)");
            int spottedCleanup = method.IndexOf("for (int spottedIndex = spotted.Count - 1; spottedIndex >= 0; spottedIndex--)");
            int releaseQueue = method.IndexOf("ShipsToRelease.Add(ship)");

            Assert.That(damageCleanup, Is.GreaterThanOrEqualTo(0));
            Assert.That(spottedCleanup, Is.GreaterThan(damageCleanup));
            Assert.That(releaseQueue, Is.GreaterThan(spottedCleanup));
            StringAssert.Contains("status.Ship == ship", method);
            StringAssert.Contains("entry.Ship == ship", method);
            StringAssert.Contains("ShipsBySide[shipSideIndex].Remove(ship);", method);
        }

        [Test]
        public void ShipRegistryMaintainsPerSideIndexes()
        {
            string registry = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs"));
            string state = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "GameState.cs"));
            string queries = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "GameState.Queries.cs"));

            StringAssert.Contains("ShipsBySide[sideIndex].Add(ship);", registry);
            StringAssert.Contains("ShipsBySide[shipSideIndex].Remove(ship);", registry);
            StringAssert.Contains("ShipsBySide[side].Clear();", state);
            StringAssert.Contains("? ShipsBySide[sideIndex]", queries);
        }

        [Test]
        public void CarrierDeathCleansReferencesForItsOwnSide()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Carrier.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("List<Ship> levelShips = Level.State.Ships;", source);
            StringAssert.Contains("candidate.Side == Side", source);
            StringAssert.DoesNotContain("Level.State.GetHumanShips()", source);
            StringAssert.Contains("carrierShip.Carrier == this", source);
            StringAssert.Contains("carrierShip.Carrier = null;", source);
        }

        [Test]
        public void FogVisionTimersStayBoundToTheLifecycleLevelThatCreatedThem()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "FogOfWarVision.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("private Level _ownerLevel;", source);
            StringAssert.Contains("_ownerLevel = Ship.Level;", source);
            StringAssert.Contains("Level fadeLevel = _ownerLevel;", source);
            StringAssert.Contains("fadeLevel.AddTimer(_shrinkVisionStartTimer);", source);
            StringAssert.Contains("if (_ownerLevel == fadeLevel)", source);
            StringAssert.DoesNotContain("Ship.Level.AddTimer(_shrinkVisionTimer);", source);
        }

        [Test]
        public void ShipRemainsRetirePreviousLevelOwnershipBeforePoolReuse()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "ShipRemains.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("private Level _ownerLevel;", source);
            StringAssert.Contains("RetirePreviousPlacement();", source);
            StringAssert.Contains("_ownerLevel = Ship.Level;", source);
            StringAssert.Contains("_ownerLevel.AddTimer(_killTimer);", source);
            StringAssert.Contains("_ownerLevel.State.Deadbodies.Remove(this);", source);
            StringAssert.DoesNotContain("Ship.Level.AddTimer(_killTimer);", source);
            StringAssert.DoesNotContain("Ship.Level.CancelTimer(_killTimer);", source);
        }

        [Test]
        public void NonAnimatedShipRemainsRecolorFromImmutableBaseline()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "ShipRemains.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("private Sprite _baseSprite;", source);
            StringAssert.Contains("_baseSprite = _spriteRenderer != null ? _spriteRenderer.sprite : null;", source);
            StringAssert.Contains("Utilities.GetChangablePixelsForImage(colors, _baseSprite)", source);
            StringAssert.Contains("Utilities.SetImageColor(Ship.Squad.Color, _baseSprite, changeablePixels)", source);
        }

        [Test]
        public void ShipAnimationActivationResetsWarpAndFrameSessionState()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "ShipAnimationController.cs");
            string source = File.ReadAllText(path);
            int activate = source.IndexOf("public void Activate()");
            int deactivate = source.IndexOf("public void Deactivate()", activate);
            string body = source.Substring(activate, deactivate - activate);

            StringAssert.Contains("ShouldSwapSprite = false;", body);
            StringAssert.Contains("UseSecondaryLoop = false;", body);
            StringAssert.Contains("IsReadyToWarp = false;", body);
            StringAssert.Contains("SpriteIndex = 0;", body);
            StringAssert.Contains("CurrentSprite = SpriteRenderer.sprite;", body);
        }

        [Test]
        public void AnimatedShipRemainsResetFrameStateBeforeRecoloring()
        {
            string remainsPath = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "ShipRemains.cs");
            string remainsSource = File.ReadAllText(remainsPath);
            StringAssert.Contains("AnimationController.ResetForReuse();", remainsSource);

            string controllerPath = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "RemainsAnimationController.cs");
            string controllerSource = File.ReadAllText(controllerPath);
            StringAssert.Contains("public void ResetForReuse()", controllerSource);
            StringAssert.Contains("SpriteIndex = 0;", controllerSource);
            StringAssert.Contains("CurrentSprite = null;", controllerSource);
            StringAssert.Contains("Ship.Squad.HasCustomColor && CurrentSprite != null", controllerSource);
        }
    }
}
