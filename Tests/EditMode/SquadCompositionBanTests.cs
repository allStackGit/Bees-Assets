using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadCompositionBanTests
    {
        [Test]
        public void OffsetRefreshAlsoRefreshesCompositionDependentCommandBans()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.Movement.cs");
            string source = File.ReadAllText(path);

            int offsetsIndex = source.IndexOf("public void SetOffsets()");
            int refreshIndex = source.IndexOf("RefreshCompositionCommandBans();", offsetsIndex);
            Assert.That(offsetsIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(refreshIndex, Is.GreaterThan(offsetsIndex));
        }

        [Test]
        public void CompositionRefreshHandlesDefenselessBomberAndArmedStates()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.Movement.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("if (IsDefenseless)", source);
            StringAssert.Contains("else if (HasOnlyBombers)", source);
            StringAssert.Contains("BannedStrats.Add(ConfigData.CommandTypes.Aggressive);", source);
            StringAssert.Contains("BannedStrats.Remove(ConfigData.CommandTypes.Aggressive);", source);
        }
    }
}
