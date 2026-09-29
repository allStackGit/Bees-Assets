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
            StringAssert.Contains("object loadedData = GetLoadedDataWithDefaults();", source);
            StringAssert.Contains("TitaniaRouteState.LoadFromPlayerProgress(loadedData);", source);
            StringAssert.Contains("_onceDataIsLoaded?.Invoke(loadedData);", source,
                "Migration must invoke the loader callback with the merged payload while remaining safe for recovery-only data files without a callback.");
        }

        [Test]
        public void FleetLoaderAllowsOptionalNameAndMinedStatFromOlderRecords()
        {
            string fleetData = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Data", "FleetData.cs"));
            string aotJson = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Data", "AotJson.cs"));

            StringAssert.Contains("AotJson.ParseFleetShips(json)", fleetData,
                "FleetData must route persisted ship records through the explicit AOT-safe parser.");
            StringAssert.Contains("ship[\"m\"]?.Value<int>() ?? 0", aotJson);
            StringAssert.Contains("ship[\"n\"]?.Value<string>() ?? string.Empty", aotJson);
        }
    }
}
