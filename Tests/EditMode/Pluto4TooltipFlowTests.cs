using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class Pluto4TooltipFlowTests
    {
        private string _source;

        [SetUp]
        public void SetUp()
        {
            _source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Campaign.Pluto4.cs"));
        }

        [Test]
        public void TooltipDisabledPathBypassesTutorialUiAndResumesMission()
        {
            string method = ExtractMethodBody(_source, "Pluto4BluerPasturesCampaign");
            Assert.That(method, Does.Contain("if (!ConfigData.UserProgressData.ShowToolTips)"));
            Assert.That(method, Does.Contain("Stage.Menus.TogglePausePanel();"));
            Assert.That(method, Does.Contain("hasSeenFleetMessages = true;"));
            Assert.That(method, Does.Contain("else\n                            {\n                                Tooltip basicTooltip"));
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf(" " + methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), $"Could not find method {methodName}.");
            int openingBrace = source.IndexOf('{', signature);
            int depth = 0;
            for (int index = openingBrace; index < source.Length; index++)
            {
                if (source[index] == '{') depth++;
                else if (source[index] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(openingBrace, index - openingBrace + 1);
                    }
                }
            }

            Assert.Fail($"Could not extract method {methodName}.");
            return string.Empty;
        }
    }
}
