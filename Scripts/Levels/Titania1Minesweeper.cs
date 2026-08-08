using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.UI_Components;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        /// <summary>
        /// Current campaign implementation for Titania mission 1.
        ///
        /// The older implementation in LeveLTriggers.cs seeded and immediately saved a
        /// temporary Bee fleet for testing. CampaignMissionCatalog deliberately routes
        /// mission 7 here instead so the mission uses the campaign's existing persistent
        /// fleets without manufacturing test-only ships on entry.
        /// </summary>
        public void Titania1MinesweeperCampaign()
        {
            Vector2 centerOfTitania = new Vector2(-32, 55);
            HasContinuousTriggers = true;
            Stage.Menus.SetMissionStatus("Reach the center of the map or clear the Bees");

            Stage.CutsceneManager.Setup(Titania1MinesweeperEnding);

            // Small patrols already present in the persistent Bee fleet are distributed
            // around the obstacle field. The encounter is intentionally avoidable.
            for (int i = 0; i < 4; i++)
            {
                AddReinforcementSquads(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 1),
                }, new Vector2(-150, -150), new Vector2(-150, -150));
            }

            for (int i = 0; i < 3; i++)
            {
                AddReinforcementSquads(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 1),
                }, new Vector2(-150, 150), new Vector2(-150, 150));
            }

            for (int i = 0; i < 4; i++)
            {
                AddReinforcementSquads(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 1),
                }, new Vector2(100, 110), new Vector2(100, 110));
            }

            for (int i = 0; i < 4; i++)
            {
                AddReinforcementSquads(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 1),
                }, new Vector2(100, 110), new Vector2(100, 110));
            }

            for (int i = 0; i < 2; i++)
            {
                AddReinforcementSquads(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 1),
                }, new Vector2(205, -35), new Vector2(205, -35));
            }

            Stage.EnablePlayerControl();

            // Time pressure discourages clearing the entire demolition maze at leisure.
            ScaledTimer reinforcements = new ScaledTimer(90f, () =>
            {
                AddReinforcementSquads(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2, true, true),
                }, new Vector2(325, -225), new Vector2(200, -225));
                AddReinforcementsToHivemindCommandQueue();
            });
            AddTimer(reinforcements);

            bool exitZoneCreated = false;
            NextTriggers.Add(new Trigger(() =>
            {
                return !exitZoneCreated && State.GetShips(ConfigData.Configuration.UserSide)
                    .Any(ship => Vector2.Distance(ship.GetPosition(), centerOfTitania) < 50);
            }, () =>
            {
                exitZoneCreated = true;
                GameObject exitBox = Instantiate(Stage.Prefabs.ExitZonePrefab, Map.transform);
                exitBox.transform.localPosition = centerOfTitania;
                exitBox.transform.localScale = new Vector2(75, 75);
                Zone exitZone = exitBox.GetComponent<Zone>();

                exitZone.OnShipEnter = ship =>
                {
                    if (ship.Side != ConfigData.Configuration.UserSide)
                    {
                        return;
                    }

                    _lastShipRetreated = State.GetShips(ship.Side).Count(candidate => candidate.IsMobile) == 1;
                    ship.EndKill();
                };
            }, "Titania 1 Create exit zone"));

            NextTriggers.Add(new Trigger(() =>
            {
                return State.IsSideKilled(ConfigData.Configuration.UserSide) ||
                       State.IsSideKilled(ConfigData.Configuration.AISide);
            }, () =>
            {
                WinningSide = State.IsSideKilled(ConfigData.Configuration.UserSide) && !_lastShipRetreated
                    ? ConfigData.Configuration.AISide
                    : ConfigData.Configuration.UserSide;

                // Unlike the old test implementation, do not leave a delayed reinforcement
                // callback alive while the ending dialogue/result flow is running.
                CancelTimer(reinforcements);
                CloseLevel();

                if (WinningSide == ConfigData.Configuration.UserSide)
                {
                    Stage.CutsceneManager.PlayDialogueSection(
                        Stage.CutsceneManager.Titania_Minesweeper.GetRange(16, 5), true);
                }
                else
                {
                    Stage.CutsceneManager.PlayDialogueSection(
                        Stage.CutsceneManager.Titania_Minesweeper.GetRange(21, 10), true);
                }
            }, "Titania 1 Ending"));

            // The Fire Tank discovery/tutorial triggers in the older implementation are
            // intentionally still disabled for fast level testing. They should be restored
            // when normal campaign pacing is re-enabled; the obstacle/tank mechanic itself
            // remains part of the authored Minesweeper map.
        }

        /// <summary>
        /// Persistent campaign completion runs only after the ending dialogue completes.
        /// </summary>
        public void Titania1MinesweeperEnding()
        {
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore;
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();

            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }
    }
}
