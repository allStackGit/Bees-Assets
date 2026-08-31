using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public sealed class RlTrainingEasterEggTests
    {
        [Test]
        public void EasterEggTriggers_TrainingDoesNotRequireCutsceneManagerOrAddTimer()
        {
            GameObject stageObject = new GameObject("Training Stage");
            GameObject levelObject = new GameObject("Training Level");
            try
            {
                Component stage = stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
                Component level = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
                ((Behaviour)stage).enabled = false;
                ((Behaviour)level).enabled = false;

                RuntimeAssembly.SetField(stage, "IsTraining", true);
                RuntimeAssembly.SetField(level, "Stage", stage);

                Assert.DoesNotThrow(() => RuntimeAssembly.Invoke(level, "EasterEggTriggers"));
                Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(level, "Timers")), Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(levelObject);
                Object.DestroyImmediate(stageObject);
            }
        }
    }
}
