using System;
using System.Reflection;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlLivePolicyAgentTests
    {
        [Test]
        public void PlayerFacingLevelOnlyGivesRlControlToAiSide()
        {
            MethodInfo shouldControlSide = GetShouldControlSide();

            Assert.That(shouldControlSide.Invoke(null, new object[] { true, 2, 2 }), Is.True);
            Assert.That(shouldControlSide.Invoke(null, new object[] { true, 1, 2 }), Is.False);
        }

        [Test]
        public void FishTankStyleLevelAllowsBothSidesToUseRlPolicy()
        {
            MethodInfo shouldControlSide = GetShouldControlSide();

            Assert.That(shouldControlSide.Invoke(null, new object[] { false, 1, 2 }), Is.True);
            Assert.That(shouldControlSide.Invoke(null, new object[] { false, 2, 2 }), Is.True);
        }

        private static MethodInfo GetShouldControlSide()
        {
            Type type = RuntimeAssembly.GetType("RlLivePolicyAgent");
            Assert.That(type, Is.Not.Null);
            MethodInfo method = type.GetMethod("ShouldControlSide", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return method;
        }
    }
}
