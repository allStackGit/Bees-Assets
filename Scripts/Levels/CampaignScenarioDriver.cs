using System;
using System.Collections.Generic;
using System.Linq;

namespace Assets.Scripts.Levels
{
    /// <summary>
    /// Process-wide guard used only while an isolated campaign scene is loaded.
    /// Scene components consult it before creating persistent or network services.
    /// </summary>
    public static class CampaignScenarioIsolation
    {
        private static int _missionId = -1;

        public static bool IsActive => _missionId >= 0;
        public static int MissionId => _missionId;

        public static IDisposable Begin(int missionId)
        {
            CampaignMissionCatalog.MissionDefinition definition =
                CampaignMissionCatalog.Get(missionId);
            if (definition.ScenarioStatus !=
                CampaignMissionCatalog.AutomatedScenarioStatus.Ready)
            {
                throw new InvalidOperationException(
                    $"Campaign mission {missionId} is not enabled for isolated scenes ({definition.ScenarioStatus}).");
            }
            if (IsActive)
            {
                throw new InvalidOperationException(
                    $"Campaign mission {_missionId} already owns the isolated scene scope.");
            }
            _missionId = missionId;
            return new IsolationScope(missionId);
        }

        private sealed class IsolationScope : IDisposable
        {
            private readonly int _ownerMissionId;
            private bool _disposed;

            public IsolationScope(int ownerMissionId)
            {
                _ownerMissionId = ownerMissionId;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                if (_missionId != _ownerMissionId)
                {
                    throw new InvalidOperationException(
                        "Campaign scenario isolation ownership changed before disposal.");
                }
                _missionId = -1;
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Deterministic driver for an already-created campaign Level. It advances the
    /// real trigger graph without waiting for the runtime timer. It never constructs
    /// or configures a mission, which keeps scene ownership and teardown with the host.
    /// </summary>
    public sealed class CampaignScenarioDriver
    {
        private readonly Level _level;

        public int MissionId { get; }

        public CampaignScenarioDriver(Level level, int missionId)
        {
            _level = level ?? throw new ArgumentNullException(nameof(level));
            CampaignMissionCatalog.MissionDefinition definition =
                CampaignMissionCatalog.Get(missionId);
            if (definition.ScenarioStatus !=
                CampaignMissionCatalog.AutomatedScenarioStatus.Ready)
            {
                throw new InvalidOperationException(
                    $"Campaign mission {missionId} is not enabled for automated scenarios ({definition.ScenarioStatus}).");
            }
            if (_level.CurrentLevelOptions != null &&
                _level.CurrentLevelOptions.Id != missionId)
            {
                throw new ArgumentException(
                    $"Level options identify mission {_level.CurrentLevelOptions.Id}, not {missionId}.",
                    nameof(missionId));
            }
            MissionId = missionId;
        }

        public CampaignScenarioSnapshot Advance()
        {
            int triggered = _level.EvaluateCampaignTriggers();
            return new CampaignScenarioSnapshot(
                MissionId,
                triggered,
                _level.Triggers.Count,
                _level.NextTriggers.Count,
                _level.WinningSide,
                _level.State != null && _level.State.GameOver,
                _level.Triggers.Select(trigger => trigger.Name)
                    .Concat(_level.NextTriggers.Select(trigger => trigger.Name))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToArray());
        }
    }

    public sealed class CampaignScenarioSnapshot
    {
        public readonly int MissionId;
        public readonly int TriggeredCount;
        public readonly int ActiveTriggerCount;
        public readonly int DeferredTriggerCount;
        public readonly int WinningSide;
        public readonly bool GameOver;
        public readonly IReadOnlyList<string> PendingTriggerNames;

        public CampaignScenarioSnapshot(int missionId, int triggeredCount,
            int activeTriggerCount, int deferredTriggerCount, int winningSide,
            bool gameOver, IReadOnlyList<string> pendingTriggerNames)
        {
            MissionId = missionId;
            TriggeredCount = triggeredCount;
            ActiveTriggerCount = activeTriggerCount;
            DeferredTriggerCount = deferredTriggerCount;
            WinningSide = winningSide;
            GameOver = gameOver;
            PendingTriggerNames = pendingTriggerNames ?? Array.Empty<string>();
        }
    }
}
