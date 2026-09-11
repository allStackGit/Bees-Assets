using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlGameplayDemonstrationAgentTests
    {
        [Test]
        public void CaptureRequiresExplicitCommandLineFlag()
        {
            MethodInfo method = GetStaticMethod("IsCaptureRequested");

            Assert.That(method.Invoke(null, new object[] { new string[] { "Bees.exe" } }), Is.False);
            Assert.That(method.Invoke(null, new object[] { new string[] { "Bees.exe", "--rl-record-demonstrations" } }), Is.True);
            Assert.That(method.Invoke(null, new object[] { new string[] { "Bees.exe", "--RL-RECORD-DEMONSTRATIONS" } }), Is.True);
        }

        [Test]
        public void MovementDirectionUsesSharedRlCoordinateConvention()
        {
            MethodInfo method = GetStaticMethod("EncodeMovementDirection");

            AssertVector(method, 0, 0f, 1f);
            AssertVector(method, 90, -1f, 0f);
            AssertVector(method, 180, 0f, -1f);
            AssertVector(method, 270, 1f, 0f);
            AssertVector(method, 360, 0f, 0f);
        }

        [Test]
        public void SourceClassificationSeparatesHumanHiveMindAndLiveRl()
        {
            MethodInfo method = GetStaticMethod("DetermineSourceForTests");

            Assert.That(method.Invoke(null, new object[] { true, false, false }), Is.EqualTo(1));
            Assert.That(method.Invoke(null, new object[] { false, true, false }), Is.EqualTo(2));
            Assert.That(method.Invoke(null, new object[] { false, false, false }), Is.EqualTo(0));

            // Live RL deliberately reuses Hive Mind squad ownership flags. It must never be mislabeled
            // as a Hive Mind demonstration merely because IsHiveMindControlled remains true.
            Assert.That(method.Invoke(null, new object[] { false, true, true }), Is.EqualTo(0));
            Assert.That(method.Invoke(null, new object[] { true, true, true }), Is.EqualTo(0));
        }

        [Test]
        public void DemonstrationSourcesUseSeparateDirectories()
        {
            MethodInfo method = GetStaticMethod("GetSourceDirectoryNameForTests");

            Assert.That(method.Invoke(null, new object[] { 1 }), Is.EqualTo("Human"));
            Assert.That(method.Invoke(null, new object[] { 2 }), Is.EqualTo("HiveMind"));
            Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { 0 }));
        }

        private static MethodInfo GetStaticMethod(string name)
        {
            Type type = RuntimeAssembly.GetType("RlGameplayDemonstrationAgent");
            Assert.That(type, Is.Not.Null);
            MethodInfo method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(method, Is.Not.Null);
            return method;
        }

        private static void AssertVector(MethodInfo method, int direction, float expectedX, float expectedY)
        {
            Vector2 vector = (Vector2)method.Invoke(null, new object[] { direction });
            Assert.That(vector.x, Is.EqualTo(expectedX).Within(0.0001f));
            Assert.That(vector.y, Is.EqualTo(expectedY).Within(0.0001f));
        }
    }
}
