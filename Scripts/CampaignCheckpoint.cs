using Assets.Scripts.Levels;
using Assets.Scripts.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Assets.Scripts
{
    /// <summary>
    /// Coalesces server-backed profile writes and persists progress plus every mode's fleet/squad
    /// snapshot in one database transaction. This keeps global ID allocators and mode progression
    /// consistent with the objects they identify even when several legacy Save() calls fire together.
    /// </summary>
    public static class CampaignCheckpoint
    {
        public const string DataFile = "__campaign_checkpoint__";

        private const float FailedWriteRetryDelaySeconds = 30f;
        private static bool _pendingSave;
        private static float _retryAfterRealtime;

        public static bool IsProfileMember(string filename)
        {
            if (filename == ConfigData.UserProgressFilename)
            {
                return true;
            }

            for (int i = 0; i < ConfigData.FleetDataFilenames.Length; i++)
            {
                if (filename == ConfigData.FleetDataFilenames[i] ||
                    filename == ConfigData.SavedSquadsDataFilenames[i])
                {
                    return true;
                }
            }
            return false;
        }

        public static void Save()
        {
            if (ConfigData.Test)
            {
                ConfigData.UserProgressData.Save();
                ConfigData.CurrentShips?.SaveSquadData();
                ConfigData.CurrentShips?.SaveFleetData();
                return;
            }

            _pendingSave = true;
        }

        private static bool HasOutstandingCheckpointWrite()
        {
            foreach (ServerRequest request in ConfigData.Socket.StandingRequests)
            {
                if (request is StoreUserDataRequest storeRequest &&
                    storeRequest.Request != null &&
                    storeRequest.Request.DataFile == DataFile)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool AreProfileMembersReady()
        {
            if (ConfigData.SocketManager == null ||
                ConfigData.UserProgressData == null || !ConfigData.IsUserProgressDataLoaded)
            {
                return false;
            }

            for (int i = 0; i < ConfigData.FleetDataFilenames.Length; i++)
            {
                if (!ConfigData.IsFleetDataLoaded[i] || !ConfigData.IsSavedSquadsDataLoaded[i])
                {
                    return false;
                }
            }

            return ConfigData.GetFleetData() != null &&
                   ConfigData.GetCampaignFleetData() != null &&
                   ConfigData.GetChallengeFleetData() != null &&
                   ConfigData.GetSavedSquadsData() != null &&
                   ConfigData.GetCampaignSavedSquadsData() != null &&
                   ConfigData.GetChallengeSavedSquadsData() != null;
        }

        internal static void HandleWriteResponse(StoreUserDataRequest request, int status)
        {
            if (request?.Request == null || request.Request.DataFile != DataFile)
            {
                return;
            }

            if (status == 1 || (status >= 200 && status < 300))
            {
                _retryAfterRealtime = 0f;
                return;
            }

            _pendingSave = true;
            _retryAfterRealtime = Time.realtimeSinceStartup + FailedWriteRetryDelaySeconds;
            Debug.LogWarning(
                "Campaign checkpoint write failed with status " + status +
                "; the latest profile snapshot will be retried.");
        }

        internal static void FlushIfReady()
        {
            // Keep the coalesced save pending while transport recovery is in progress. Serializing
            // a fresh seven-file checkpoint every rendered frame and attempting to send it through
            // a closed WebSocket turns a normal disconnect into a main-thread allocation/error loop.
            if (!_pendingSave ||
                !AreProfileMembersReady() ||
                Time.realtimeSinceStartup < _retryAfterRealtime ||
                !ConfigData.Socket.IsOpen ||
                HasOutstandingCheckpointWrite())
            {
                return;
            }

            string userProgressJson = TitaniaRouteState.AddToPlayerProgressJson(
                ConfigData.UserProgressData.ToJson());
            JObject checkpoint = new JObject
            {
                [ConfigData.UserProgressFilename] = userProgressJson,
                [ConfigData.SavedSquadsDataFilenames[0]] = ConfigData.GetSavedSquadsData().ToJson(),
                [ConfigData.FleetDataFilenames[0]] = ConfigData.GetFleetData().ToJson(),
                [ConfigData.SavedSquadsDataFilenames[1]] = ConfigData.GetCampaignSavedSquadsData().ToJson(),
                [ConfigData.FleetDataFilenames[1]] = ConfigData.GetCampaignFleetData().ToJson(),
                [ConfigData.SavedSquadsDataFilenames[2]] = ConfigData.GetChallengeSavedSquadsData().ToJson(),
                [ConfigData.FleetDataFilenames[2]] = ConfigData.GetChallengeFleetData().ToJson(),
            };

            var payload = new StoreUserData(
                ConfigData.GetUserId(),
                DataFile,
                checkpoint.ToString(Formatting.None));
            ConfigData.Socket.SendRequest(new StoreUserDataRequest(payload, ConfigData.StandardMaxTimeOnQueue));
            _pendingSave = false;
        }
    }
}
