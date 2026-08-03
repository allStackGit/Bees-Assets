using System;
using System.Collections.Generic;
using Assets.Scripts;
using Newtonsoft.Json;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    [Serializable]
    public sealed class SimulationReplayEvent
    {
        public long FixedStep;
        public string Kind;
        public string Payload;

        public SimulationReplayEvent(long fixedStep, string kind, string payload)
        {
            FixedStep = fixedStep;
            Kind = kind;
            Payload = payload;
        }
    }

    /// <summary>
    /// Versioned, transport-neutral record of inputs that crossed into a simulation.
    /// It deliberately records opaque payloads so user orders and server responses can
    /// be captured without coupling replay storage to today's command protocol.
    /// </summary>
    [Serializable]
    public sealed class SimulationReplayTrace
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public int Seed;
        public List<SimulationReplayEvent> Events = new List<SimulationReplayEvent>();

        public SimulationReplayTrace(int seed)
        {
            Seed = seed;
        }

        [JsonConstructor]
        public SimulationReplayTrace(int version, int seed, List<SimulationReplayEvent> events)
        {
            Version = version;
            Seed = seed;
            Events = events ?? new List<SimulationReplayEvent>();
            Validate();
        }

        public void Record(long fixedStep, string kind, string payload)
        {
            if (fixedStep < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fixedStep));
            }
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new ArgumentException("Replay event kind is required.", nameof(kind));
            }
            if (Events.Count > 0 && fixedStep < Events[Events.Count - 1].FixedStep)
            {
                throw new InvalidOperationException("Replay events must be recorded in fixed-step order.");
            }

            Events.Add(new SimulationReplayEvent(fixedStep, kind, payload ?? string.Empty));
        }

        public string ToJson()
        {
            Validate();
            return JsonConvert.SerializeObject(this, Formatting.None);
        }

        public static SimulationReplayTrace FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ArgumentException("Replay JSON is required.", nameof(json));
            }

            SimulationReplayTrace trace = JsonConvert.DeserializeObject<SimulationReplayTrace>(json);
            if (trace == null)
            {
                throw new JsonSerializationException("Replay JSON did not contain a trace.");
            }
            trace.Validate();
            return trace;
        }

        public void Validate()
        {
            if (Version != CurrentVersion)
            {
                throw new NotSupportedException($"Replay version {Version} is not supported.");
            }

            long priorStep = -1;
            for (int index = 0; index < Events.Count; index++)
            {
                SimulationReplayEvent replayEvent = Events[index];
                if (replayEvent == null || replayEvent.FixedStep < priorStep ||
                    string.IsNullOrWhiteSpace(replayEvent.Kind))
                {
                    throw new InvalidOperationException($"Replay event {index} is invalid or out of order.");
                }
                priorStep = replayEvent.FixedStep;
            }
        }
    }

    /// <summary>
    /// Fixed-step dispatcher used by replay hosts. The host remains responsible for
    /// interpreting event kinds and payloads through the same command boundaries used
    /// during recording.
    /// </summary>
    public sealed class SimulationReplayPlayer
    {
        private readonly SimulationReplayTrace _trace;
        private int _nextEvent;
        private long _lastStep = -1;

        public bool IsComplete => _nextEvent >= _trace.Events.Count;

        public SimulationReplayPlayer(SimulationReplayTrace trace)
        {
            _trace = trace ?? throw new ArgumentNullException(nameof(trace));
            _trace.Validate();
        }

        public int DispatchStep(long fixedStep, Action<SimulationReplayEvent> dispatch)
        {
            if (fixedStep < _lastStep)
            {
                throw new InvalidOperationException("Replay steps cannot move backwards.");
            }
            if (dispatch == null)
            {
                throw new ArgumentNullException(nameof(dispatch));
            }

            _lastStep = fixedStep;
            int dispatched = 0;
            while (_nextEvent < _trace.Events.Count &&
                   _trace.Events[_nextEvent].FixedStep == fixedStep)
            {
                dispatch(_trace.Events[_nextEvent++]);
                dispatched++;
            }

            if (_nextEvent < _trace.Events.Count &&
                _trace.Events[_nextEvent].FixedStep < fixedStep)
            {
                throw new InvalidOperationException(
                    $"Replay host skipped fixed step {_trace.Events[_nextEvent].FixedStep}.");
            }
            return dispatched;
        }
    }

    /// <summary>
    /// Seeds both random systems currently used by the game and restores their prior
    /// state afterward. Create before level construction, not during an active frame.
    /// </summary>
    public sealed class SimulationReplayRandomScope : IDisposable
    {
        private readonly UnityEngine.Random.State _previousUnityState;
        private IDisposable _utilitiesScope;

        public SimulationReplayRandomScope(int seed)
        {
            _previousUnityState = UnityEngine.Random.state;
            _utilitiesScope = Utilities.UseDeterministicRandom(seed);
            UnityEngine.Random.InitState(seed);
        }

        public void Dispose()
        {
            if (_utilitiesScope == null)
            {
                return;
            }

            UnityEngine.Random.state = _previousUnityState;
            _utilitiesScope.Dispose();
            _utilitiesScope = null;
        }
    }
}
