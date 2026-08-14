using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TitaniaReinforcementRoutingTests
    {
        [Test]
        public void ClearBeenocularsMapDoesNotRequireObstaclePathfinderForReinforcements()
        {
            string routingPath = Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Titania2ReinforcementRouting.cs");
            string sharedPath = Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Campaign.Shared.cs");

            string routing = File.ReadAllText(routingPath);
            string shared = File.ReadAllText(sharedPath);

            StringAssert.Contains("if (!HasObstacles || Pathfinder == null)", routing);
            StringAssert.Contains("if (isBeenoculars && HasObstacles && Pathfinder != null)", shared);
            StringAssert.Contains("EnsureTitania2ReinforcementRoute(ref startingPosition, ref nextPosition);", shared);
        }
    }
}
