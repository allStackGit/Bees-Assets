using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PlutoOneCameraAndDiscoveryRegressionTests
    {
        [Test]
        public void ProximityDetectionResolvesShipOwnersAndCountsChildColliderOverlaps()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "Ships", "ProximityCollider.cs"));

            Assert.That(source, Does.Contain("GetComponentInParent<Ship>()"),
                "A Honeybee child collider entering Scout vision must resolve to the owning Ship.");
            Assert.That(source, Does.Contain("Dictionary<Ship, int> _enemyOverlapCounts"),
                "Multi-collider ships need overlap counts so one child exiting cannot hide a ship that is still visible.");
            Assert.That(source, Does.Contain("_enemyOverlapCounts[nearbyShip] = overlapCount + 1;"));
            Assert.That(source, Does.Contain("_enemyOverlapCounts[nearbyShip] = overlapCount - 1;"));
            Assert.That(source, Does.Contain("NearbyEnemyShips.Remove(nearbyShip);"));
        }

        [Test]
        public void CampaignFollowCameraStaysInsideMapAndKeepsScriptedScoutUntilRemoval()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "CampaignCameraFollowGuard.cs"));

            Assert.That(source, Does.Contain("private void LateUpdate()"));
            Assert.That(source, Does.Contain("ShouldContinueScriptedFollow(cameraShip)"));
            Assert.That(source, Does.Contain("cameraShip.CanOverrideBounds"));
            Assert.That(source, Does.Contain("cameraShip.ShipType == ConfigData.ShipTypes.Scout"));
            Assert.That(source, Does.Contain("!cameraShip.IsDead"),
                "The scripted Scout follow must persist only until the Scout is removed/dead.");
            Assert.That(source, Does.Contain("float maximumVerticalSize = Mathf.Min"),
                "Follow zoom must fit entirely inside the map instead of exposing space outside its bounds.");
            Assert.That(source, Does.Contain("Mathf.Clamp(position.x, minimumX, maximumX)"));
            Assert.That(source, Does.Contain("Mathf.Clamp(position.y, minimumY, maximumY)"));
            Assert.That(source, Does.Contain("else if (_stage.IsCameraMovingToTarget)"),
                "Cutscene lerps must receive the same map-boundary protection as direct ship following.");
        }

        [Test]
        public void SpaceCowboyPromptRendersClosedBeforeCampaignLoadingStarts()
        {
            string dialogue = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "Dialogue.cs"));
            string mainMenu = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "MainMenu.cs"));

            int hide = dialogue.IndexOf("Hide();");
            int defer = dialogue.IndexOf("DialogueDeferredActionRunner.InvokeNextFrame(action);");
            Assert.That(hide, Is.GreaterThanOrEqualTo(0));
            Assert.That(defer, Is.GreaterThan(hide),
                "The popup must be hidden before the scene-loading action is deferred.");
            Assert.That(dialogue, Does.Contain("methodName == \"PlayCampaign\" || methodName == \"DisableTooltips\""));
            Assert.That(dialogue, Does.Contain("yield return null;"),
                "A complete rendered frame is required between hiding the popup and starting the load.");
            Assert.That(mainMenu, Does.Contain("new List<UnityAction>() { DisableTooltips, PlayCampaign"),
                "The Space Cowboy confirmation must continue routing through the deferred campaign actions.");
        }
    }
}
