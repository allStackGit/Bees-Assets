using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SpriteCacheFallbackTests
    {
        [Test]
        public void MissingOrUnreadableCachedSpritesAreRegeneratedLocally()
        {
            string fleetShipSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "FleetShip.cs"));
            string visualsSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Visuals.cs"));
            string animationSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "Ships", "ShipAnimationController.cs"));
            string remainsAnimationSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "Ships", "RemainsAnimationController.cs"));
            string repairSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "Ships", "CustomSpriteCacheRepair.cs"));

            StringAssert.Contains("if (!File.Exists(path))", fleetShipSource);
            StringAssert.Contains("DeleteUnreadableCachedSprite(path);", fleetShipSource);
            StringAssert.Contains("Directory.CreateDirectory(directory);", fleetShipSource);
            StringAssert.DoesNotContain("throw;", fleetShipSource.Substring(
                fleetShipSource.IndexOf("public Sprite LoadCachedSprite"),
                fleetShipSource.IndexOf("public void SaveSpriteToCache") -
                fleetShipSource.IndexOf("public Sprite LoadCachedSprite")));

            StringAssert.Contains("_loadedSprite = FleetShip.LoadCachedSprite(", visualsSource);
            StringAssert.DoesNotContain("if (FleetShip.HasCachedSprite)", visualsSource);
            StringAssert.Contains("if (!_hasLoadedSprite)", visualsSource);
            StringAssert.Contains("Utilities.SetImageColor", visualsSource);
            StringAssert.Contains("FleetShip.SaveSpriteToCache(", visualsSource);

            StringAssert.Contains("RecoloredSprites[i] = Ship.FleetShip.LoadCachedSprite(", animationSource);
            StringAssert.Contains("CustomSpriteCacheRepair.RecolorAndCache(", animationSource);
            StringAssert.Contains("RecoloredSprites[index] = recoloredSprite;", animationSource);
            StringAssert.Contains("CurrentSprite = SpriteRenderer.sprite;", animationSource);

            StringAssert.Contains("RecoloredSprites[_loopIndex] = Ship.FleetShip.LoadCachedSprite(", remainsAnimationSource);
            StringAssert.Contains("CustomSpriteCacheRepair.RecolorAndCache(", remainsAnimationSource);
            StringAssert.Contains("RecoloredSprites[_index] = recoloredSprite;", remainsAnimationSource);

            StringAssert.Contains("Utilities.GetChangablePixelsForImage", repairSource);
            StringAssert.Contains("ship.FleetShip.SaveSpriteToCache(", repairSource);
        }
    }
}
