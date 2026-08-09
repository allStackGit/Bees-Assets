using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class HiveMindVisibleSquadTests
    {
        [Test]
        public void VisibleSquadsAreDeduplicatedBeforeMatchupSelection()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Queries.cs");
            string source = File.ReadAllText(path);

            int start = source.IndexOf("public List<Squad> GetSquadsVisibleToHiveMind");
            int end = source.IndexOf("public Squad GetSquadByNumber", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));

            string method = source.Substring(start, end - start);
            StringAssert.Contains(".Select(ship => ship.Squad)", method);
            StringAssert.Contains(".Distinct()", method);
        }
    }
}
