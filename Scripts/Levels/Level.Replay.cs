using System;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        public SimulationReplayTrace ActiveReplayTrace { get; private set; }
        public bool IsRecordingReplay => ActiveReplayTrace != null;

        public SimulationReplayTrace BeginReplayRecording(int seed)
        {
            if (ActiveReplayTrace != null)
            {
                throw new InvalidOperationException("This level is already recording a replay.");
            }
            ActiveReplayTrace = new SimulationReplayTrace(seed);
            return ActiveReplayTrace;
        }

        public SimulationReplayTrace EndReplayRecording()
        {
            SimulationReplayTrace completed = ActiveReplayTrace;
            ActiveReplayTrace = null;
            return completed;
        }

        public void RecordSimulationInput(string kind, string payload)
        {
            if (ActiveReplayTrace == null)
            {
                return;
            }
            if (Stage == null)
            {
                throw new InvalidOperationException("A level must belong to a stage before recording inputs.");
            }
            ActiveReplayTrace.Record(Stage.FixedUpdates, kind, payload);
        }
    }
}
