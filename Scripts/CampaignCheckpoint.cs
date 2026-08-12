using Assets.Scripts.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

        private static bool _pendingSave;

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

        /// <summary>
        /// Requests one coherent profile checkpoint. The write is deferred until the existing
        /// socket lifecycle guard reaches its next update and all seven profile members have
        /// completed their in-memory load callbacks. Several synchronous legacy Save() calls
        /// therefore collapse into one snapshot instead of exposing an intermediate split state.
        /// </summary>
        public static void Save()
        {
            if (ConfigData.Test)
            {
                // Local test storage intentionally keeps its existing direct-file behavior; there
                // is no transactional server to coordinate these files.
                ConfigData.UserProgressData.Save();
                ConfigData.CurrentShips?.SaveSquadData();
                ConfigData.CurrentShips?.SaveFleetData();
                return;
            }

            _pendingSave = true;
        }

        private static bool AreProfileMembersReady()
        {
            // Follow the same bootstrap ownership rule as SocketResponseLifecycleGuard: do not
            // force creation/use of ConfigData.Socket before a live Scene owns socket management.
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

        internal static void FlushIfReady()
        {
            if (!_pendingSave || !AreProfileMembersReady())
            {
                return;
            }

            JObject checkpoint = new JObject
            {
                [ConfigData.UserProgressFilename] = ConfigData.UserProgressData.ToJson(),
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
