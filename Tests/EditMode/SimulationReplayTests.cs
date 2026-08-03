using System;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SimulationReplayTests
    {
        private Type _scopeType;
        private Type _traceType;
        private Type _utilitiesType;
        private Type _playerType;

        [SetUp]
        public void SetUp()
        {
            _scopeType = RuntimeAssembly.GetType("Assets.Scripts.Levels.SimulationReplayRandomScope");
            _traceType = RuntimeAssembly.GetType("Assets.Scripts.Levels.SimulationReplayTrace");
            _utilitiesType = RuntimeAssembly.GetType("Assets.Scripts.Utilities");
            _playerType = RuntimeAssembly.GetType("Assets.Scripts.Levels.SimulationReplayPlayer");
        }

        [Test]
        public void SameSeedReproducesBothGameplayRandomStreams()
        {
            int[] first = Sample(481516);
            int[] second = Sample(481516);
            int[] different = Sample(108);

            Assert.That(second, Is.EqualTo(first));
            Assert.That(different, Is.Not.EqualTo(first));
        }

        [Test]
        public void ReplayTraceRoundTripsEscapedPayloadsAndPreservesFixedStepOrder()
        {
            object trace = Activator.CreateInstance(_traceType, new object[] { 42 });
            RuntimeAssembly.Invoke(trace, "Record", 3L, "user-order", "{\"point\":\"a\\\\b\"}");
            RuntimeAssembly.Invoke(trace, "Record", 3L, "server-response", "line1\nline2");
            RuntimeAssembly.Invoke(trace, "Record", 9L, "pause", "");

            string json = (string)RuntimeAssembly.Invoke(trace, "ToJson");
            object restored = RuntimeAssembly.InvokeStatic(_traceType, "FromJson", json);

            Assert.That(RuntimeAssembly.GetField(restored, "Seed"), Is.EqualTo(42));
            object events = RuntimeAssembly.GetField(restored, "Events");
            Assert.That(RuntimeAssembly.GetCount(events), Is.EqualTo(3));
            Assert.That((string)RuntimeAssembly.Invoke(restored, "ToJson"), Is.EqualTo(json));
        }

        [Test]
        public void ReplayTraceRejectsOutOfOrderEventsAndUnsupportedVersions()
        {
            object trace = Activator.CreateInstance(_traceType, new object[] { 7 });
            RuntimeAssembly.Invoke(trace, "Record", 5L, "start", "");
            Assert.That(Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                RuntimeAssembly.Invoke(trace, "Record", 4L, "late", "")).InnerException,
                Is.TypeOf<InvalidOperationException>());

            RuntimeAssembly.SetField(trace, "Version", 99);
            Assert.That(Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                RuntimeAssembly.Invoke(trace, "ToJson")).InnerException,
                Is.TypeOf<NotSupportedException>());
        }

        [Test]
        public void ReplayPlayerDispatchesEveryEventAtItsRecordedStepAndPreservesOrder()
        {
            object trace = Activator.CreateInstance(_traceType, new object[] { 7 });
            RuntimeAssembly.Invoke(trace, "Record", 2L, "first", "a");
            RuntimeAssembly.Invoke(trace, "Record", 2L, "second", "b");
            RuntimeAssembly.Invoke(trace, "Record", 4L, "third", "c");
            object player = Activator.CreateInstance(_playerType, new[] { trace });
            var kinds = new System.Collections.Generic.List<string>();
            Type eventType = RuntimeAssembly.GetType("Assets.Scripts.Levels.SimulationReplayEvent");
            Delegate dispatch = Delegate.CreateDelegate(
                typeof(Action<>).MakeGenericType(eventType),
                new ReplayEventCollector(kinds),
                typeof(ReplayEventCollector).GetMethod(nameof(ReplayEventCollector.Add)));

            Assert.That(RuntimeAssembly.Invoke(player, "DispatchStep", 0L, dispatch), Is.EqualTo(0));
            Assert.That(RuntimeAssembly.Invoke(player, "DispatchStep", 2L, dispatch), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.Invoke(player, "DispatchStep", 4L, dispatch), Is.EqualTo(1));
            Assert.That(kinds, Is.EqualTo(new[] { "first", "second", "third" }));
            Assert.That((bool)_playerType.GetProperty("IsComplete").GetValue(player), Is.True);
        }

        [Test]
        public void ReplayPlayerRejectsSkippedAndBackwardSteps()
        {
            object trace = Activator.CreateInstance(_traceType, new object[] { 7 });
            RuntimeAssembly.Invoke(trace, "Record", 2L, "event", "");
            object player = Activator.CreateInstance(_playerType, new[] { trace });
            Type eventType = RuntimeAssembly.GetType("Assets.Scripts.Levels.SimulationReplayEvent");
            Delegate dispatch = Delegate.CreateDelegate(
                typeof(Action<>).MakeGenericType(eventType),
                new ReplayEventCollector(new System.Collections.Generic.List<string>()),
                typeof(ReplayEventCollector).GetMethod(nameof(ReplayEventCollector.Add)));

            Assert.That(Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                RuntimeAssembly.Invoke(player, "DispatchStep", 3L, dispatch)).InnerException,
                Is.TypeOf<InvalidOperationException>());

            object secondPlayer = Activator.CreateInstance(_playerType, new[] { trace });
            RuntimeAssembly.Invoke(secondPlayer, "DispatchStep", 1L, dispatch);
            Assert.That(Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                RuntimeAssembly.Invoke(secondPlayer, "DispatchStep", 0L, dispatch)).InnerException,
                Is.TypeOf<InvalidOperationException>());
        }

        private sealed class ReplayEventCollector
        {
            private readonly System.Collections.Generic.List<string> _kinds;

            public ReplayEventCollector(System.Collections.Generic.List<string> kinds)
            {
                _kinds = kinds;
            }

            public void Add(object replayEvent)
            {
                _kinds.Add((string)RuntimeAssembly.GetField(replayEvent, "Kind"));
            }
        }

        private int[] Sample(int seed)
        {
            IDisposable scope = (IDisposable)Activator.CreateInstance(_scopeType, new object[] { seed });
            try
            {
                return new[]
                {
                    (int)RuntimeAssembly.InvokeStatic(_utilitiesType, "RandomInt", 1000000),
                    (bool)RuntimeAssembly.InvokeStatic(_utilitiesType, "CoinToss") ? 1 : 0,
                    UnityEngine.Random.Range(0, 1000000),
                    UnityEngine.Random.Range(0, 1000000),
                };
            }
            finally
            {
                scope.Dispose();
            }
        }
    }
}
