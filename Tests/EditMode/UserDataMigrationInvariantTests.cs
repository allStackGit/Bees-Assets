using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class UserDataMigrationInvariantTests
    {
        [Test]
        public void ObjectRootedUserDataFillsMissingFieldsWithoutMergingArrayRecords()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Data", "UserData.cs"));

            StringAssert.Contains("loadedObject is JObject loaded", source);
            StringAssert.Contains("defaultToken is JObject defaults", source);
            StringAssert.Contains("MergeArrayHandling = MergeArrayHandling.Replace", source);
            StringAssert.Contains("_onceDataIsLoaded(GetLoadedDataWithDefaults());", source);
        }

        [Test]
        public void FleetLoaderAllowsOptionalNameAndMinedStatFromOlderRecords()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Data", "FleetData.cs"));

            StringAssert.Contains("ship.m != null ? (int)ship.m : 0", source);
            StringAssert.Contains("ship.n != null ? (string)ship.n : \"\"", source);
        }
    }
}
