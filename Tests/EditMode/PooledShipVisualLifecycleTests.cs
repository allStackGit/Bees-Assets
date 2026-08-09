using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PooledShipVisualLifecycleTests
    {
        [Test]
        public void ShipRecoloringAlwaysStartsFromPrefabEraSprites()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Visuals.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("private readonly List<Sprite> _baseColorSprites", source);
            StringAssert.Contains("EnsureBaseColorSprites();", source);
            StringAssert.Contains("_prefabSprite = _baseColorSprites[_tempIndex];", source);
            StringAssert.Contains("prefab.GetComponent<SpriteRenderer>().sprite = _baseColorSprites[_tempIndex];", source);
        }

        [Test]
        public void BeaconRestoresBothAlternateSpritesBeforeEachRecolor()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Beacon.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("_originalStandardSprite = StandardSprite;", source);
            StringAssert.Contains("_originalEnemySprite = EnemySprite;", source);
            StringAssert.Contains("StandardSprite = _originalStandardSprite;", source);
            StringAssert.Contains("EnemySprite = _originalEnemySprite;", source);
        }
    }
}
