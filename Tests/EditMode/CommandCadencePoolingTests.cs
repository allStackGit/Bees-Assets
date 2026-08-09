using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CommandCadencePoolingTests
    {
        [TestCase("Aggressive.cs")]
        [TestCase("CircleSquad.cs")]
        public void AcceleratingCommandsRestoreDefaultCadenceOnReuse(string fileName)
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", fileName);
            string source = File.ReadAllText(path);

            StringAssert.Contains("CommandFrequency = .25f;", source);
            int clearStart = source.IndexOf("public override void ClearData()");
            Assert.That(clearStart, Is.GreaterThanOrEqualTo(0));
            int nextMethod = source.IndexOf("private ", clearStart);
            string clear = nextMethod > clearStart
                ? source.Substring(clearStart, nextMethod - clearStart)
                : source.Substring(clearStart);
            StringAssert.Contains("CommandFrequency = 3f;", clear);
        }
    }
}