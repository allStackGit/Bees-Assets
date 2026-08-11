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
            string squadSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Squad.cs"));
            string movementSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Squad.Movement.cs"));

            int refresh = movementSource.IndexOf("private void RefreshCompositionCommandBans()");
            int add = squadSource.IndexOf("public void AddShip(Ship ship)");
            int remove = squadSource.IndexOf("public void RemoveShip(Ship ship)");

            Assert.That(refresh, Is.GreaterThanOrEqualTo(0));
            Assert.That(squadSource.IndexOf("RefreshCompositionCommandBans();", add), Is.GreaterThan(add));
            Assert.That(squadSource.IndexOf("RefreshCompositionCommandBans();", remove), Is.GreaterThan(remove),
                "Casualties must refresh defenseless/bomber-only command eligibility.");
        }
    }
}
