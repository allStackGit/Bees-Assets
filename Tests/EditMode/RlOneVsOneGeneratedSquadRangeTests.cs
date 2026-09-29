using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public sealed class RlOneVsOneGeneratedSquadRangeTests
    {
        [Test]
        public void Apply_NormalizesCopiedLegacySquadRangeToExactlyOne()
        {
            GameObject stageObject = new GameObject(nameof(RlOneVsOneGeneratedSquadRangeTests));
            System.Type configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            object previousConfiguration = RuntimeAssembly.GetStaticField(configDataType, "Configuration");
            object previousStartingSettings = RuntimeAssembly.GetStaticField(configDataType, "StartingSettings");
            object previousShipInfo = RuntimeAssembly.GetStaticField(configDataType, "ShipInfo");
            try
            {
                RuntimeAssembly.SetStaticField(configDataType, "Configuration", CreateLoadedSetting("Assets.Scripts.Settings.Configuration"));
                RuntimeAssembly.SetStaticField(configDataType, "StartingSettings", CreateLoadedSetting("Assets.Scripts.Settings.StartingSettings"));
                RuntimeAssembly.SetStaticField(configDataType, "ShipInfo", CreateLoadedSetting("Assets.Scripts.Settings.ShipStats"));

                Component stage = stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
                ((Behaviour)stage).enabled = false;
                RuntimeAssembly.SetField(stage, "GeneratedSquadCountOverride", 16);
                RuntimeAssembly.SetField(stage, "GeneratedSquadCountMinimum", 12);

                RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType("RlOneVsOneTrainingBootstrap"),
                    "Apply",
                    stage);

                Assert.That(RuntimeAssembly.GetField(stage, "GeneratedSquadCountOverride"), Is.EqualTo(1));
                Assert.That(RuntimeAssembly.GetField(stage, "GeneratedSquadCountMinimum"), Is.EqualTo(0));
            }
            finally
            {
                RuntimeAssembly.SetStaticField(configDataType, "Configuration", previousConfiguration);
                RuntimeAssembly.SetStaticField(configDataType, "StartingSettings", previousStartingSettings);
                RuntimeAssembly.SetStaticField(configDataType, "ShipInfo", previousShipInfo);
                Object.DestroyImmediate(stageObject);
            }
        }

        private static object CreateLoadedSetting(string typeName)
        {
            object setting = RuntimeAssembly.CreateUninitialized(typeName);
            RuntimeAssembly.SetField(setting, "IsLoaded", true);
            return setting;
        }
    }
}
