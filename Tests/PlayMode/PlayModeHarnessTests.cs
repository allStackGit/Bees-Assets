using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bees.Tests.PlayMode
{
    [TestFixture]
    [Category("BeesPlayModeFoundation")]
    public class PlayModeHarnessTests
    {
        [Test]
        public void TestRunnerEntersPlayMode()
        {
            Assert.That(Application.isPlaying, Is.True);
        }

        [UnityTest]
        public IEnumerator DestroyedObjectsAreCleanedUpAtFrameBoundary()
        {
            GameObject transientObject = new GameObject("PlayMode lifecycle probe");

            Object.Destroy(transientObject);
            yield return null;

            Assert.That(transientObject == null, Is.True,
                "Unity should report the destroyed native object as null after a frame boundary.");
        }
    }
}
