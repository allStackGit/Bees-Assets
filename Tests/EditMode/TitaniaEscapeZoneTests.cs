using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TitaniaEscapeZoneTests
    {
        [Test]
        public void TitaniaOneEscapeFieldUsesReducedScale()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Titania1Minesweeper.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("exitBox.transform.localScale = new Vector2(40f, 40f);", source);
            StringAssert.DoesNotContain("exitBox.transform.localScale = new Vector2(75, 75);", source);
        }
    }
}
