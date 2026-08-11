using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadCompositionBanTests
    {
        private static string MovementSourcePath =>
            Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.Movement.cs");

        [Test]
        public void OffsetRefreshAlsoRefreshesCompositionDependentCommandBans()
        {
            string source = File.ReadAllText(MovementSourcePath);

            int offsetsIndex = source.IndexOf("public void SetOffsets()");
            int refreshIndex = source.IndexOf("RefreshCompositionCommandBans();", offsetsIndex);
            Assert.That(offsetsIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(refreshIndex, Is.GreaterThan(offsetsIndex));
        }

        [Test]
        public void CompositionRefreshHandlesDefenselessBomberBargeAndArmedStates()
        {
            string source = File.ReadAllText(MovementSourcePath);

            StringAssert.Contains("if (IsDefenseless)", source);
            StringAssert.Contains("else if (HasOnlyBombers)", source);
            StringAssert.Contains("else if (HasOnlyBarges)", source);
            StringAssert.Contains("BannedStrats.Add(ConfigData.CommandTypes.Aggressive);", source);
            StringAssert.Contains("BannedStrats.Remove(ConfigData.CommandTypes.Aggressive);", source);
        }

        [Test]
        public void BargeBranchBansMovementAttackAliasesButKeepsHoldDistinct()
        {
            string source = File.ReadAllText(MovementSourcePath);
            int bargeStart = source.IndexOf("else if (HasOnlyBarges)");
            int followingElse = source.IndexOf("\n            else\n", bargeStart);

            Assert.That(bargeStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(followingElse, Is.GreaterThan(bargeStart));
            string branch = source.Substring(bargeStart, followingElse - bargeStart);

            StringAssert.Contains("BannedStrats.Remove(ConfigData.CommandTypes.Aggressive);", branch);
            StringAssert.Contains("BannedStrats.Add(ConfigData.CommandTypes.CircleSquad);", branch);
            StringAssert.Contains("BannedStrats.Add(ConfigData.CommandTypes.RightSwipe);", branch);
            StringAssert.Contains("BannedStrats.Add(ConfigData.CommandTypes.LeftSwipe);", branch);
            StringAssert.Contains("BannedStrats.Add(ConfigData.CommandTypes.InAndOut);", branch);
            StringAssert.Contains("BannedStrats.Remove(ConfigData.CommandTypes.Hold);", branch);
        }

        [Test]
        public void CompositionBanMethodHasSinglePartialClassOwner()
        {
            string levelsDirectory = Path.Combine(Application.dataPath, "Scripts", "Levels");
            int definitions = 0;
            foreach (string path in Directory.GetFiles(levelsDirectory, "Squad*.cs"))
            {
                string source = File.ReadAllText(path);
                int searchIndex = 0;
                const string signature = "private void RefreshCompositionCommandBans()";
                while ((searchIndex = source.IndexOf(signature, searchIndex)) >= 0)
                {
                    definitions++;
                    searchIndex += signature.Length;
                }
            }

            Assert.That(definitions, Is.EqualTo(1),
                "RefreshCompositionCommandBans must have exactly one definition across Squad partials.");
        }
    }
}
