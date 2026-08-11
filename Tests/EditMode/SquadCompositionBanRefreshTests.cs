using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadCompositionBanRefreshTests
    {
        [Test]
        public void ShipRemovalRefreshesCompositionDerivedCommandBans()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Squad.cs"));

            int refresh = source.IndexOf("private void RefreshCompositionCommandBans()");
            int add = source.IndexOf("public void AddShip(Ship ship)");
            int remove = source.IndexOf("public void RemoveShip(Ship ship)");

            Assert.That(refresh, Is.GreaterThanOrEqualTo(0));
            Assert.That(source.IndexOf("RefreshCompositionCommandBans();", add), Is.GreaterThan(add));
            Assert.That(source.IndexOf("RefreshCompositionCommandBans();", remove), Is.GreaterThan(remove),
                "Casualties must refresh defenseless/bomber-only command eligibility.");
        }
    }
}
