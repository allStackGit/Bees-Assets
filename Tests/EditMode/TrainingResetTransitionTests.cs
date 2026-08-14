using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TrainingResetTransitionTests
    {
        private string _levelResetSource;
        private string _registrySource;

        [SetUp]
        public void SetUp()
        {
            _levelResetSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Reset.cs"));
            _registrySource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs"));
        }

        [Test]
        public void ShipRemovalToleratesSpottedStateDuringEpisodeTeardown()
        {
            string resetLevel = ExtractMethodBody(_levelResetSource, "ResetLevel");
            string removeShip = ExtractMethodBody(_registrySource, "RemoveShip");

            int clearSpotted = resetLevel.IndexOf("State.SpottedShips[_reset_i]?.Clear();", StringComparison.Ordinal);
            int endKill = resetLevel.IndexOf("_resetShips[_reset_i].EndKill();", StringComparison.Ordinal);
            Assert.That(clearSpotted, Is.GreaterThanOrEqualTo(0));
            Assert.That(endKill, Is.GreaterThan(clearSpotted),
                "ResetLevel should clear the reusable spotting containers before old ships are torn down.");
            Assert.That(resetLevel, Does.Not.Contain("Array.Clear(_reset_spottedShips"));
            Assert.That(removeShip, Does.Contain("if (spotted == null)"));
            Assert.That(removeShip, Does.Contain("for (int spottedIndex = spotted.Count - 1; spottedIndex >= 0; spottedIndex--)"));
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = FindMethodDeclaration(source, methodName);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), $"Could not find method {methodName}.");
            int openingBrace = source.IndexOf('{', signature);
            Assert.That(openingBrace, Is.GreaterThanOrEqualTo(0));

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

        private static int FindMethodDeclaration(string source, string methodName)
        {
            string token = methodName + "(";
            int searchFrom = 0;
            while (searchFrom < source.Length)
            {
                int occurrence = source.IndexOf(token, searchFrom, StringComparison.Ordinal);
                if (occurrence < 0) return -1;

                int lineStart = source.LastIndexOf('\n', occurrence);
                lineStart = lineStart < 0 ? 0 : lineStart + 1;
                string prefix = source.Substring(lineStart, occurrence - lineStart);
                if (prefix.Contains("public ") || prefix.Contains("private ") ||
                    prefix.Contains("protected ") || prefix.Contains("internal "))
                {
                    return occurrence;
                }

                searchFrom = occurrence + token.Length;
            }
            return -1;
        }
    }
}
