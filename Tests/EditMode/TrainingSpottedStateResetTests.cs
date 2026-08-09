using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TrainingSpottedStateResetTests
    {
        [Test]
        public void ResetStateRecreatesPerSideSpottedLists()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains(
                "SpottedShips = new[] { new List<SpottedShip>(), new List<SpottedShip>() };",
                source);
            StringAssert.DoesNotContain("SpottedShips[0].Clear();", source);
            StringAssert.DoesNotContain("SpottedShips[1].Clear();", source);
        }
    }
}
