using Assets.Scripts.Data;
using Assets.Scripts.Entities;
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
            TitaniaRouteState.BeginMinesweeper();

            Stage.CutsceneManager.Setup(Titania1MinesweeperEnding);

            // The authored prefab currently contains duplicated serialized target links.
            // Preserve a correct authored layout when one exists, but repair stale links
            // deterministically so each Fire Tank demolishes its own nearby barrier.
            RepairMinesweeperDemolitionTargets(Map.transform);

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

            // The scripting document establishes discovery of the first Fire Tank as the
            // mechanic tutorial. This was temporarily disabled only to speed level testing.
            NextTriggers.Add(new Trigger(() => State.PlayerVisibleMapObjects.Any(), () =>
            {
                var firstExplosive = State.PlayerVisibleMapObjects.FirstOrDefault();
                if (firstExplosive == null)
                {
                    return;
                }

                Stage.CutsceneManager.PlayDialogueSection(
                    Stage.CutsceneManager.Titania_Minesweeper.GetRange(1, 11), false);

                NextTriggers.Add(new Trigger(() => firstExplosive == null || firstExplosive.IsDead, () =>
                {
                    Stage.CutsceneManager.PlayDialogueSection(
                        Stage.CutsceneManager.Titania_Minesweeper.GetRange(12, 4), false);
                }, "Titania 1 destroyed first Fire Tank"));
            }, "Titania 1 discovered first Fire Tank"));

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
        }

        private static void RepairMinesweeperDemolitionTargets(Transform root)
        {
            if (root == null)
            {
                return;
            }

            CanisterBomb[] fireTanks = root.GetComponentsInChildren<CanisterBomb>(true);
            if (fireTanks.Length == 0)
            {
                return;
            }

            // These prefab components have not run MapObject.Setup yet, so their game IDs
            // are still zero. Use each target's Transform as stable per-instance identity
            // rather than MapObject's runtime ID-based equality.
            int distinctAuthoredTargets = fireTanks
                .Where(tank => tank.TargetObstacle != null)
                .Select(tank => tank.TargetObstacle.transform)
                .Distinct()
                .Count();
            if (distinctAuthoredTargets == fireTanks.Length)
            {
                return;
            }

            List<Obstacle> obstacles = root.GetComponentsInChildren<Obstacle>(true)
                .Where(obstacle => obstacle != null)
                .ToList();
            HashSet<Transform> usedTargetTransforms = new HashSet<Transform>();

            foreach (CanisterBomb fireTank in fireTanks)
            {
                Obstacle nearestUnused = obstacles
                    .Where(obstacle => !usedTargetTransforms.Contains(obstacle.transform))
                    .OrderBy(obstacle =>
                        ((Vector2)obstacle.transform.position - (Vector2)fireTank.transform.position).sqrMagnitude)
                    .FirstOrDefault();

                if (nearestUnused == null)
                {
                    Debug.LogError($"Could not assign a unique demolition target to {fireTank.name}.");
                    continue;
                }

                fireTank.TargetObstacle = nearestUnused;
                usedTargetTransforms.Add(nearestUnused.transform);
            }
        }

        /// <summary>
        /// Persistent campaign completion runs only after the ending dialogue completes.
        /// </summary>
        public void Titania1MinesweeperEnding()
        {
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore;
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            ConfigData.CurrentShips.SaveSquadData();
            ConfigData.CurrentShips.SaveFleetData();

            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }
    }
}
