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
        public void Level0Ending() => Pluto1Ending(GetRequiredAnomalyGunshipSquad());
        public void Level1Ending() => Pluto2Ending();
        public void Level2Ending() => Pluto3Ending();
        public void Level3Ending() => Pluto4Ending();
        public void Level4Ending() => Neptune1Ending();
        public void Level5Ending() => Neptune2Ending();
        public void Level6Ending() => Neptune3Ending();
        public void Level7Ending() => Titania1MinesweeperEnding();
        public void Level8Ending() => Titania2CampaignEnding();
        public void Level9Ending() => Uranus1Ending();
        public void Level10Ending() => Uranus2Ending();
        public void Level11Ending() => Uranus3Ending();

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

            // Pluto1Ending needs the scripted Gunship squad so it can add the awarded ship.
            // If surrender occurs before that scripted squad exists, completing the mission
            // would corrupt progression; fail explicitly rather than dereference null later.
            throw new System.InvalidOperationException("Anomaly cannot complete before its scripted Gunship squad exists.");
        }
    }
}
