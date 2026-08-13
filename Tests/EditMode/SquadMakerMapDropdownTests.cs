using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerMapDropdownTests
    {
        [Test]
        public void MapCatalogKeepsTrainingDropdownIdsContiguousAndIncludesTitania()
        {
            Type configData = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            var maps = ((System.Collections.IEnumerable)RuntimeAssembly.GetStaticField(configData, "Maps"))
                .Cast<object>()
                .ToList();

            int[] ids = maps.Select(map => (int)RuntimeAssembly.GetField(map, "Id")).ToArray();
            string[] names = maps.Select(map => (string)RuntimeAssembly.GetField(map, "Name")).ToArray();

            Assert.That(ids, Is.EqualTo(new[] { 0, 1, 2, 3 }));
            Assert.That(names, Is.EqualTo(new[] { "Pluto", "Neptune", "Titania", "Uranus" }));
        }

        [Test]
        public void TrainingMapDropdownIsBuiltFromMapCatalog()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "SquadMakerMapDropdownGuard.cs"));

            Assert.That(source, Does.Contain("new TMP_Dropdown.OptionData(\"Random\")"));
            Assert.That(source, Does.Contain("ConfigData.Maps.OrderBy(map => map.Id)"));
            Assert.That(source, Does.Contain("new TMP_Dropdown.OptionData(map.Name)"));
            Assert.That(source, Does.Contain("dropdown.ClearOptions()"));
        }
    }
}
