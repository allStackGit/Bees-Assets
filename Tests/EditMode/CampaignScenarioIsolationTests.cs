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

        [Test]
        public void ScopeOwnsOneReadyMissionAndDisposeIsIdempotent()
        {
            IDisposable scope = (IDisposable)RuntimeAssembly.InvokeStatic(_isolationType, "Begin", 2);
            Assert.That((bool)_isolationType.GetProperty("IsActive").GetValue(null), Is.True);
            Assert.That((int)_isolationType.GetProperty("MissionId").GetValue(null), Is.EqualTo(2));

            scope.Dispose();
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
                    RuntimeAssembly.InvokeStatic(_isolationType, "Begin", 2));
                Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That((int)_isolationType.GetProperty("MissionId").GetValue(null), Is.EqualTo(1));
            }
            finally
            {
                first.Dispose();
            }
        }

        [TestCase(7)]
        [TestCase(8)]
        [TestCase(9)]
        [TestCase(10)]
        [TestCase(11)]
        public void NonReadyMissionCannotAcquireIsolation(int missionId)
        {
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                RuntimeAssembly.InvokeStatic(_isolationType, "Begin", missionId));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That((bool)_isolationType.GetProperty("IsActive").GetValue(null), Is.False);
        }
    }
}
