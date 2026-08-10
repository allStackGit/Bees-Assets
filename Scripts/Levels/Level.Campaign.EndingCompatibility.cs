using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;

namespace Assets.Scripts.Levels
{
    /// <summary>
    /// Compatibility surface for legacy callers that resolve campaign endings as
    /// "Level{campaignId}Ending" by reflection. Active mission implementations use
    /// descriptive names and are catalogued by CampaignMissionCatalog.
    /// </summary>
    public partial class Level
    {
        public void Level0Ending() { PrepareCampaignSurrenderEnding(); Pluto1Ending(GetRequiredAnomalyGunshipSquad()); }
        public void Level1Ending() { PrepareCampaignSurrenderEnding(); Pluto2Ending(); }
        public void Level2Ending() { PrepareCampaignSurrenderEnding(); Pluto3Ending(); }
        public void Level3Ending() { PrepareCampaignSurrenderEnding(); Pluto4Ending(); }
        public void Level4Ending() { PrepareCampaignSurrenderEnding(); Neptune1Ending(); }
        public void Level5Ending() { PrepareCampaignSurrenderEnding(); Neptune2Ending(); }
        public void Level6Ending() { PrepareCampaignSurrenderEnding(); Neptune3Ending(); }
        public void Level7Ending() { PrepareCampaignSurrenderEnding(); Titania1MinesweeperEnding(); }
        public void Level8Ending() { PrepareCampaignSurrenderEnding(); Titania2CampaignEnding(); }
        public void Level9Ending() { PrepareCampaignSurrenderEnding(); Uranus1Ending(); }
        public void Level10Ending() { PrepareCampaignSurrenderEnding(); Uranus2Ending(); }
        public void Level11Ending() { PrepareCampaignSurrenderEnding(); Uranus3Ending(); }

        private void PrepareCampaignSurrenderEnding()
        {
            WinningSide = ConfigData.Configuration.AISide;
            DidUserWin = false;
        }

        private SavedSquad GetRequiredAnomalyGunshipSquad()
        {
            foreach (Squad squad in State.GetSquadsBySide(ConfigData.Configuration.UserSide))
            {
                foreach (Ship ship in squad.GetShips())
                {
                    if (ship.ShipType == ConfigData.ShipTypes.Gunship)
                    {
                        return squad.SavedSquad;
                    }
                }
            }

            // Pluto1 hides surrender during the scripted opening, but keep this compatibility
            // method defensive in case a non-UI caller invokes it before the Gunship exists.
            throw new System.InvalidOperationException("Anomaly cannot complete before its scripted Gunship squad exists.");
        }
    }
}
