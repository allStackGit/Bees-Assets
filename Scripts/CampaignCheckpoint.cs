using Assets.Scripts.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Assets.Scripts
{
    /// <summary>
    /// Persists campaign progress, squads, and fleet as one server transaction so a disconnect
    /// cannot leave the profile advanced to a mission whose fleet/squad snapshot is older.
    /// </summary>
    public static class CampaignCheckpoint
    {
        public const string DataFile = "__campaign_checkpoint__";

        public static void Save()
        {
            // Local test storage is ordinary files and has no transactional server. Preserve the
            // established local behavior there; production/development server storage is atomic.
            if (ConfigData.Test)
            {
                ConfigData.UserProgressData.Save();
                ConfigData.CurrentShips.SaveSquadData();
                ConfigData.CurrentShips.SaveFleetData();
                return;
            }

            JObject checkpoint = new JObject
            {
                [ConfigData.UserProgressFilename] = ConfigData.UserProgressData.ToJson(),
                [ConfigData.SavedSquadsDataFilenames[1]] = ConfigData.GetCampaignSavedSquadsData().ToJson(),
                [ConfigData.FleetDataFilenames[1]] = ConfigData.GetCampaignFleetData().ToJson(),
            };

            var payload = new StoreUserData(
                ConfigData.GetUserId(),
                DataFile,
                checkpoint.ToString(Formatting.None));
            ConfigData.Socket.SendRequest(new StoreUserDataRequest(payload, ConfigData.StandardMaxTimeOnQueue));
        }
    }
}
