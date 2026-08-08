using Assets.Scripts.Data;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        /// <summary>
        /// Active Neptune mission 3 setup. Kept separate from the campaign trigger monolith so
        /// the failure dialogue can preserve the unconditional escape order while conditionally
        /// including the Factory-abandonment exchange.
        /// </summary>
        public void Neptune3PressingForwardCampaign()
        {
            Stage.Menus.SetMissionStatus("Destroy all the Bees to break through the blockade");
            HasContinuousTriggers = true;

            Stage.CutsceneManager.Setup(Neptune3Ending);

            AddReinforcementSquads(new List<SavedSquad>() {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
            }, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);

            Stage.EnablePlayerControl();

            NextTriggers.Add(new Trigger(() =>
            {
                return State.IsSideKilled(ConfigData.Configuration.UserSide) ||
                       State.IsSideKilled(ConfigData.Configuration.AISide);
            }, () =>
            {
                WinningSide = CampaignObjectiveRules.ResolveEliminationWinner(
                    State.IsSideKilled(ConfigData.Configuration.UserSide),
                    State.IsSideKilled(ConfigData.Configuration.AISide),
                    ConfigData.Configuration.UserSide,
                    ConfigData.Configuration.AISide);

                CloseLevel();
                if (WinningSide == ConfigData.Configuration.UserSide)
                {
                    Stage.CutsceneManager.PlayDialogueSection(
                        Stage.CutsceneManager.Neptune_PressingForward.GetRange(1, 4), true);
                    return;
                }

                List<DialogueLine> failure = new List<DialogueLine>();
                failure.AddRange(Stage.CutsceneManager.Neptune_PressingForward.GetRange(5, 2));
                if (ConfigData.CurrentShips.HasShipsOfType(ConfigData.ShipTypes.Factory))
                {
                    failure.AddRange(Stage.CutsceneManager.Neptune_PressingForward.GetRange(7, 4));
                }
                // The final evacuation order is unconditional in the authored script.
                failure.Add(Stage.CutsceneManager.Neptune_PressingForward[11]);
                Stage.CutsceneManager.PlayDialogueSection(failure, true);
            }, "Neptune 3 Ending dialogue"));
        }
    }
}
