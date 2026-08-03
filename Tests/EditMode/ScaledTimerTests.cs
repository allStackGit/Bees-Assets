using System;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ScaledTimerTests
    {
        private Type _timerType;

        [SetUp]
        public void SetUp()
        {
            _timerType = RuntimeAssembly.GetType("Assets.Scripts.ScaledTimer");
        }

        [Test]
        public void ReuseResetsPerUseState()
        {
            object timer = Activator.CreateInstance(_timerType);
            RuntimeAssembly.SetField(timer, "Elapsed", 12f);
            RuntimeAssembly.SetField(timer, "IsCanceled", true);
            RuntimeAssembly.SetField(timer, "StartImmediate", true);

            RuntimeAssembly.Invoke(timer, "Reuse", 3f, (Action)(() => { }), true, false);

            Assert.That(RuntimeAssembly.GetField(timer, "Length"), Is.EqualTo(3f));
            Assert.That(RuntimeAssembly.GetField(timer, "Elapsed"), Is.EqualTo(0f));
            Assert.That(RuntimeAssembly.GetField(timer, "IsCanceled"), Is.False);
            Assert.That(RuntimeAssembly.GetField(timer, "IsRecurring"), Is.True);
            Assert.That(RuntimeAssembly.GetField(timer, "StartImmediate"), Is.False);
            Assert.That(RuntimeAssembly.GetField(timer, "Action"), Is.Not.Null);
        }

        [Test]
        public void StartImmediateRunsOnceAndClearsImmediateFlag()
        {
            int callCount = 0;
            object timer = Activator.CreateInstance(
                _timerType,
                new object[] { 10f, (Action)(() => callCount++), false, true });

            bool completed = (bool)RuntimeAssembly.Invoke(timer, "Update");

            Assert.That(completed, Is.True);
            Assert.That(callCount, Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(timer, "StartImmediate"), Is.False);
        }

        [Test]
        public void CanceledImmediateTimerDoesNotRun()
        {
            int callCount = 0;
            object timer = Activator.CreateInstance(
                _timerType,
                new object[] { 10f, (Action)(() => callCount++), false, true });
            RuntimeAssembly.SetField(timer, "IsCanceled", true);

            bool completed = (bool)RuntimeAssembly.Invoke(timer, "Update");

            Assert.That(completed, Is.False);
            Assert.That(callCount, Is.Zero);
        }
    }
}
