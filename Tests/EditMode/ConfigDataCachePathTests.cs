using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ConfigDataCachePathTests
    {
        [Test]
        public void SpriteCacheResolvesOutsideAssetsInEditor()
        {
            Type configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            string cachePath = NormalizePath((string)RuntimeAssembly.InvokeStatic(configDataType, "GetCachePath"));
            string assetsPath = NormalizePath(Application.dataPath);
            string expectedPath = NormalizePath(Path.Combine(
                Application.dataPath,
                "..",
                "Library",
                "SpriteCache"));

            Assert.That(cachePath, Is.EqualTo(expectedPath).IgnoreCase);
            Assert.That(
                cachePath.StartsWith(assetsPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                Is.False,
                "Generated sprite cache must not live under Assets because Unity imports runtime PNGs and creates .meta files there.");
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
