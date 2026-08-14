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
        public void ResetStateReusesPerSideSpottedLists()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("if (SpottedShips[side] == null) SpottedShips[side] = new List<SpottedShip>();", source);
            StringAssert.Contains("else SpottedShips[side].Clear();", source);
            StringAssert.DoesNotContain(
                "SpottedShips = new[] { new List<SpottedShip>(), new List<SpottedShip>() };",
                source);
        }
    }
}
