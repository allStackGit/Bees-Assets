using System;
using System.Reflection;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesCampaignScenario")]
    public class CampaignScenarioIsolationTests
    {
        private Type _isolationType;

        [SetUp]
        public void SetUp()
        {
            _isolationType = RuntimeAssembly.GetType("Assets.Scripts.Levels.CampaignScenarioIsolation");
            Assert.That((bool)_isolationType.GetProperty("IsActive").GetValue(null), Is.False);
        }

        [TearDown]
        public void TearDown()
        {
            Assert.That((bool)_isolationType.GetProperty("IsActive").GetValue(null), Is.False,
                "Campaign isolation leaked beyond its owning test.");
        }

        [TestCase(0)]
        [TestCase(2)]
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(9)]
        [TestCase(10)]
        [TestCase(11)]
        public void AnyRegisteredMissionCanOwnIsolationWithoutConfiguringIt(int missionId)
        {
            IDisposable scope = (IDisposable)RuntimeAssembly.InvokeStatic(_isolationType, "Begin", missionId);
            try
            {
                Assert.That((bool)_isolationType.GetProperty("IsActive").GetValue(null), Is.True);
                Assert.That((int)_isolationType.GetProperty("MissionId").GetValue(null), Is.EqualTo(missionId));
            }
            finally
            {
                scope.Dispose();
            }

            // Dispose remains idempotent because test teardown paths may call it defensively.
            scope.Dispose();
            Assert.That((bool)_isolationType.GetProperty("IsActive").GetValue(null), Is.False);
            Assert.That((int)_isolationType.GetProperty("MissionId").GetValue(null), Is.EqualTo(-1));
        }

        [Test]
        public void SecondOwnerIsRejectedUntilFirstScopeReleases()
        {
            IDisposable first = (IDisposable)RuntimeAssembly.InvokeStatic(_isolationType, "Begin", 1);
            try
            {
                TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                    RuntimeAssembly.InvokeStatic(_isolationType, "Begin", 11));
                Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That((int)_isolationType.GetProperty("MissionId").GetValue(null), Is.EqualTo(1));
            }
            finally
            {
                first.Dispose();
            }
        }

        [Test]
        public void UnknownMissionStillCannotAcquireIsolation()
        {
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                RuntimeAssembly.InvokeStatic(_isolationType, "Begin", 99));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That((bool)_isolationType.GetProperty("IsActive").GetValue(null), Is.False);
        }
    }
}
