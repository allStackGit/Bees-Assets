using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.MLAgents.Actuators;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlGameplayDemonstrationCapabilityCaptureTests
    {
        [Test]
        public void PinnedMlAgentsReflectionContractsAreAvailable()
        {
            Type type = RuntimeAssembly.GetType("RlGameplayDemonstrationCapabilityCapture");
            PropertyInfo property = type.GetProperty(
                "RuntimeContractsAvailableForTests",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.That(property, Is.Not.Null);
            Assert.That(property.GetValue(null), Is.True);
        }

        [Test]
        public void CapabilitySampleCopiesObservationAndActionsAndChangesOnlySpecialBranch()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            int observationSize = (int)RuntimeAssembly.GetStaticField(agentType, "ObservationSize");
            int continuousCount = (int)RuntimeAssembly.GetStaticField(agentType, "ContinuousActionCount");
            int discreteCount = (int)RuntimeAssembly.GetStaticField(agentType, "DiscreteBranchCount");
            int specialBranch = (int)RuntimeAssembly.GetStaticField(agentType, "SpecialActionBranch");
            int specialAction = (int)RuntimeAssembly.GetStaticField(agentType, "ShipSpecialAction");

            float[] observations = Enumerable.Range(0, observationSize)
                .Select(index => index * 0.001f)
                .ToArray();
            float[] continuous = Enumerable.Range(0, continuousCount)
                .Select(index => index * -0.01f)
                .ToArray();
            int[] discrete = Enumerable.Range(0, discreteCount)
                .Select(index => index % 2)
                .ToArray();
            int originalSpecial = discrete[specialBranch];
            ActionBuffers actions = new ActionBuffers(continuous, discrete);

            object[] arguments =
            {
                observations,
                actions,
                specialAction,
                null,
                null,
                null
            };
            bool created = (bool)GetCreateSampleMethod().Invoke(null, arguments);

            Assert.That(created, Is.True);
            float[] observationSnapshot = (float[])arguments[3];
            float[] continuousSnapshot = (float[])arguments[4];
            int[] discreteSnapshot = (int[])arguments[5];

            Assert.That(observationSnapshot, Is.Not.SameAs(observations));
            Assert.That(continuousSnapshot, Is.Not.SameAs(continuous));
            Assert.That(discreteSnapshot, Is.Not.SameAs(discrete));
            Assert.That(observationSnapshot, Is.EqualTo(observations));
            Assert.That(continuousSnapshot, Is.EqualTo(continuous));
            for (int i = 0; i < discreteCount; i++)
            {
                int expected = i == specialBranch ? specialAction : discrete[i];
                Assert.That(discreteSnapshot[i], Is.EqualTo(expected), $"branch {i}");
            }
            Assert.That(discrete[specialBranch], Is.EqualTo(originalSpecial));
        }

        [Test]
        public void CapabilitySampleRejectsWrongObservationShape()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            int observationSize = (int)RuntimeAssembly.GetStaticField(agentType, "ObservationSize");
            int continuousCount = (int)RuntimeAssembly.GetStaticField(agentType, "ContinuousActionCount");
            int discreteCount = (int)RuntimeAssembly.GetStaticField(agentType, "DiscreteBranchCount");
            int specialAction = (int)RuntimeAssembly.GetStaticField(agentType, "ShipSpecialAction");

            object[] arguments =
            {
                new float[observationSize - 1],
                new ActionBuffers(new float[continuousCount], new int[discreteCount]),
                specialAction,
                null,
                null,
                null
            };

            Assert.That(GetCreateSampleMethod().Invoke(null, arguments), Is.False);
            Assert.That(arguments[3], Is.Null);
            Assert.That(arguments[4], Is.Null);
            Assert.That(arguments[5], Is.Null);
        }

        private static MethodInfo GetCreateSampleMethod()
        {
            Type type = RuntimeAssembly.GetType("RlGameplayDemonstrationCapabilityCapture");
            MethodInfo method = type.GetMethod(
                "TryCreateSample",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return method;
        }
    }
}
