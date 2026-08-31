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
            try
            {
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
                Object.DestroyImmediate(stageObject);
            }
        }
    }
}
