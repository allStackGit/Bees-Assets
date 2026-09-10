using Assets.Scripts.Levels;
using System;
using Unity.MLAgents.SideChannels;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Training-only side channel used by the offline continual-learning evaluator.
///
/// The evaluator must score candidates from authoritative game outcomes rather than from shaped
/// PPO reward. This channel publishes one compact result record after each primary-arena episode.
/// It never accepts commands from Python and therefore cannot change gameplay, rewards, actions,
/// or the frozen policy ABI.
/// </summary>
internal sealed class RlOneVsOneEvaluationSideChannel : SideChannel
{
    internal const int ProtocolVersion = 1;
    internal const string ChannelIdText = "7ca0e8e5-47f7-49ce-b44a-738ae7f1ad15";

    private static RlOneVsOneEvaluationSideChannel _instance;

    private RlOneVsOneEvaluationSideChannel()
    {
        ChannelId = new Guid(ChannelIdText);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        RlOneVsOneEpisodeCoordinator.EpisodeEnded -= OnEpisodeEnded;
        _instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void RegisterForDedicatedTrainingScene()
    {
        if (!RlOneVsOneTrainingBootstrap.ShouldApply(SceneManager.GetActiveScene().name) || _instance != null)
        {
            return;
        }

        _instance = new RlOneVsOneEvaluationSideChannel();
        SideChannelManager.RegisterSideChannel(_instance);
        RlOneVsOneEpisodeCoordinator.EpisodeEnded -= OnEpisodeEnded;
        RlOneVsOneEpisodeCoordinator.EpisodeEnded += OnEpisodeEnded;
    }

    protected override void OnMessageReceived(IncomingMessage msg)
    {
        // This is deliberately a Unity -> Python result channel. Evaluation configuration continues
        // to use the existing command-line training options, so untrusted side-channel messages can
        // never alter a running episode.
    }

    internal static int GetWinningTeamId(RlOneVsOneEpisodeCoordinator.EpisodeResult result)
    {
        if (result.TimedOut || result.WinningSide == 0 || ConfigData.Configuration == null)
        {
            return -1;
        }

        if (result.WinningSide == ConfigData.Configuration.BeeSide)
        {
            return result.BeeTeamId;
        }
        if (result.WinningSide == ConfigData.Configuration.HumanSide)
        {
            return result.HumanTeamId;
        }
        return -1;
    }

    private static void OnEpisodeEnded(Level level, RlOneVsOneEpisodeCoordinator.EpisodeResult result)
    {
        if (_instance == null || level == null || level.Stage == null || level.Stage.PrimaryLevel != level)
        {
            return;
        }

        using (OutgoingMessage message = new OutgoingMessage())
        {
            message.WriteInt32(ProtocolVersion);
            message.WriteInt32(result.EpisodeNumber);
            message.WriteInt32(result.BeeTeamId);
            message.WriteInt32(result.HumanTeamId);
            message.WriteInt32(result.WinningSide);
            message.WriteInt32(GetWinningTeamId(result));
            message.WriteBoolean(result.TimedOut);
            message.WriteFloat32(result.DurationSeconds);
            message.WriteInt32(result.BeeStartingTsv);
            message.WriteInt32(result.BeeFinalTsv);
            message.WriteInt32(result.HumanStartingTsv);
            message.WriteInt32(result.HumanFinalTsv);
            message.WriteInt32(result.BeeShotsFired);
            message.WriteInt32(result.BeeShotsHit);
            message.WriteInt32(result.BeeDamageDealt);
            message.WriteInt32(result.HumanShotsFired);
            message.WriteInt32(result.HumanShotsHit);
            message.WriteInt32(result.HumanDamageDealt);
            _instance.QueueMessageToSend(message);
        }
    }
}
