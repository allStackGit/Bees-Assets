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
        public void MissingOrUnreadableCachedSpriteFallsBackToLiveRecoloring()
        {
            string fleetShipPath = Path.Combine(Application.dataPath, "Scripts", "Data", "FleetShip.cs");
            string fleetShipSource = File.ReadAllText(fleetShipPath);
            string visualsPath = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Visuals.cs");
            string visualsSource = File.ReadAllText(visualsPath);

            StringAssert.Contains("if (!File.Exists(path))", fleetShipSource);
            StringAssert.Contains("return null;", fleetShipSource);
            StringAssert.DoesNotContain("throw;", fleetShipSource.Substring(fleetShipSource.IndexOf("public Sprite LoadCachedSprite"), fleetShipSource.IndexOf("public void SaveSpriteToCache") - fleetShipSource.IndexOf("public Sprite LoadCachedSprite")));
            StringAssert.Contains("if (!_hasLoadedSprite)", visualsSource);
            StringAssert.Contains("Utilities.SetImageColor", visualsSource);
        }
    }
}
