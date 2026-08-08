using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Assets.Scripts.Entities.Ships;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public static class SimulationReplayKinds
    {
        public const string UserCommand = "user-command";
        public const string UserMove = "user-move";
        public const string HiveMindMatchupResponse = "hivemind-matchup-response";
        public const string HiveMindCommandResponse = "hivemind-command-response";

        public static readonly IReadOnlyCollection<string> KnownKinds = new[]
        {
            UserCommand,
            UserMove,
            HiveMindMatchupResponse,
            HiveMindCommandResponse,
        };
    }

    public sealed class ReplayUserCommand
    {
        public readonly int SquadItemId;
        public readonly ConfigData.CommandTypes CommandType;
        public readonly int EnemySquadItemId;

        public ReplayUserCommand(int squadItemId, ConfigData.CommandTypes commandType, int enemySquadItemId)
        {
            SquadItemId = squadItemId;
            CommandType = commandType;
            EnemySquadItemId = enemySquadItemId;
        }
    }

    public sealed class ReplayUserMove
    {
        public readonly IReadOnlyList<int> SquadItemIds;
        public readonly Vector2 Destination;

        public ReplayUserMove(IReadOnlyList<int> squadItemIds, Vector2 destination)
        {
            SquadItemIds = squadItemIds;
            Destination = destination;
        }
    }

    /// <summary>
    /// Parses the stable payload formats emitted at the current simulation input boundaries.
    /// Network response payloads remain opaque JSON and are routed without reinterpretation.
    /// </summary>
    public static class SimulationReplayEventAdapter
    {
        public static ReplayUserCommand ParseUserCommand(SimulationReplayEvent replayEvent)
        {
            RequireKind(replayEvent, SimulationReplayKinds.UserCommand);
            string[] parts = replayEvent.Payload.Split('|');
            if (parts.Length != 3 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int squadItemId) ||
                !Enum.TryParse(parts[1], out ConfigData.CommandTypes commandType) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int enemySquadItemId))
            {
                throw new FormatException($"Invalid {SimulationReplayKinds.UserCommand} replay payload: {replayEvent.Payload}");
            }
            return new ReplayUserCommand(squadItemId, commandType, enemySquadItemId);
        }

        public static ReplayUserMove ParseUserMove(SimulationReplayEvent replayEvent)
        {
            RequireKind(replayEvent, SimulationReplayKinds.UserMove);
            string[] parts = replayEvent.Payload.Split('|');
            if (parts.Length != 3 ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            {
                throw new FormatException($"Invalid {SimulationReplayKinds.UserMove} replay payload: {replayEvent.Payload}");
            }

            string[] idParts = string.IsNullOrWhiteSpace(parts[0])
                ? Array.Empty<string>()
                : parts[0].Split(',');
            var ids = new List<int>(idParts.Length);
            for (int index = 0; index < idParts.Length; index++)
            {
                if (!int.TryParse(idParts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                {
                    throw new FormatException($"Invalid squad ID in {SimulationReplayKinds.UserMove} replay payload: {replayEvent.Payload}");
                }
                ids.Add(id);
            }
            if (ids.Count == 0)
            {
                throw new FormatException($"A {SimulationReplayKinds.UserMove} replay event must contain at least one squad ID.");
            }
            return new ReplayUserMove(ids, new Vector2(x, y));
        }

        public static string GetOpaqueServerPayload(SimulationReplayEvent replayEvent)
        {
            if (replayEvent == null)
            {
                throw new ArgumentNullException(nameof(replayEvent));
            }
            if (replayEvent.Kind != SimulationReplayKinds.HiveMindMatchupResponse &&
                replayEvent.Kind != SimulationReplayKinds.HiveMindCommandResponse)
            {
                throw new ArgumentException($"Replay event kind {replayEvent.Kind} is not a Hive Mind response.", nameof(replayEvent));
            }
            return replayEvent.Payload ?? string.Empty;
        }

        public static void Route(
            SimulationReplayEvent replayEvent,
            Action<ReplayUserCommand> userCommand,
            Action<ReplayUserMove> userMove,
            Action<string> hiveMindMatchupResponse,
            Action<string> hiveMindCommandResponse)
        {
            if (replayEvent == null)
            {
                throw new ArgumentNullException(nameof(replayEvent));
            }

            switch (replayEvent.Kind)
            {
                case SimulationReplayKinds.UserCommand:
                    (userCommand ?? throw new ArgumentNullException(nameof(userCommand)))(ParseUserCommand(replayEvent));
                    return;
                case SimulationReplayKinds.UserMove:
                    (userMove ?? throw new ArgumentNullException(nameof(userMove)))(ParseUserMove(replayEvent));
                    return;
                case SimulationReplayKinds.HiveMindMatchupResponse:
                    (hiveMindMatchupResponse ?? throw new ArgumentNullException(nameof(hiveMindMatchupResponse)))(replayEvent.Payload ?? string.Empty);
                    return;
                case SimulationReplayKinds.HiveMindCommandResponse:
                    (hiveMindCommandResponse ?? throw new ArgumentNullException(nameof(hiveMindCommandResponse)))(replayEvent.Payload ?? string.Empty);
                    return;
                default:
                    throw new NotSupportedException($"Replay event kind {replayEvent.Kind} has no playback adapter.");
            }
        }

        private static void RequireKind(SimulationReplayEvent replayEvent, string expectedKind)
        {
            if (replayEvent == null)
            {
                throw new ArgumentNullException(nameof(replayEvent));
            }
            if (replayEvent.Kind != expectedKind)
            {
                throw new ArgumentException($"Expected replay kind {expectedKind}, got {replayEvent.Kind}.", nameof(replayEvent));
            }
        }
    }

    /// <summary>
    /// Canonical checkpoint of simulation-owned state used to compare a replay with its
    /// original run. Collections are sorted by stable IDs so hash/dictionary iteration
    /// order cannot create false mismatches.
    /// </summary>
    public sealed class SimulationStateSnapshot
    {
        public readonly string CanonicalState;

        private SimulationStateSnapshot(string canonicalState)
        {
            CanonicalState = canonicalState;
        }

        public static SimulationStateSnapshot Capture(Level level)
        {
            if (level == null)
            {
                throw new ArgumentNullException(nameof(level));
            }
            if (level.State == null)
            {
                throw new InvalidOperationException("A Level must have GameState before a replay snapshot can be captured.");
            }

            var builder = new StringBuilder(1024);
            builder.Append("level|")
                .Append(level.WinningSide).Append('|')
                .Append(level.State.GameOver ? 1 : 0).Append('|')
                .Append(level.State.LevelEnded ? 1 : 0).Append('\n');

            foreach (Squad squad in level.State.Squads.Where(value => value != null).OrderBy(value => value.ItemId))
            {
                builder.Append("squad|")
                    .Append(squad.ItemId).Append('|')
                    .Append(squad.Side).Append('|')
                    .Append(squad.IsDead ? 1 : 0).Append('|')
                    .Append(squad.IsSelected ? 1 : 0).Append('|')
                    .Append(squad.GetShips().Count).Append('\n');
            }

            foreach (Ship ship in level.State.Ships.Where(value => value != null).OrderBy(value => value.Id))
            {
                Vector2 position = ship.GetPosition();
                Vector2 velocity = ship.Body == null ? Vector2.zero : ship.Body.linearVelocity;
                float rotation = ship.transform == null ? 0f : ship.transform.eulerAngles.z;
                builder.Append("ship|")
                    .Append(ship.Id).Append('|')
                    .Append(ship.Side).Append('|')
                    .Append(ship.Health).Append('|')
                    .Append(ship.Tsv).Append('|')
                    .Append(ship.IsDead ? 1 : 0).Append('|')
                    .Append(ship.PathfindingLifecycleId).Append('|')
                    .Append(Format(position.x)).Append('|')
                    .Append(Format(position.y)).Append('|')
                    .Append(Format(velocity.x)).Append('|')
                    .Append(Format(velocity.y)).Append('|')
                    .Append(Format(rotation)).Append('\n');
            }

            return new SimulationStateSnapshot(builder.ToString());
        }

        public override string ToString()
        {
            return CanonicalState;
        }

        private static string Format(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
