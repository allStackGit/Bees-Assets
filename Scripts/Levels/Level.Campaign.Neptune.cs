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
        public void Neptune1SeizeTheMeans()
        {
            Stage.Menus.SetMissionStatus("Find and destroy all the Bees!");
            HasContinuousTriggers = true;
            Stage.ActivateHiveMind = false;
            Stage.CutsceneManager.Setup(Neptune1Ending);

            SpawnMiningAsteroids(5, 5);
            List<MiningAsteroid> miningAsteroids = State.MiningAsteroids.ToList();
            miningAsteroids[0].transform.localPosition = new Vector2(80, 300);
            miningAsteroids[1].transform.localPosition = new Vector2(-320, -245);
            miningAsteroids[2].transform.localPosition = new Vector2(-270, 390);
            miningAsteroids[3].transform.localPosition = new Vector2(-20, -175);
            miningAsteroids[4].transform.localPosition = new Vector2(155, -120);

            AddReinforcementSquads(new List<SavedSquad>()
            {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 2),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 2, true, true),
            }, miningAsteroids[0].transform.localPosition, Vector2.zero);
            AddReinforcementSquads(new List<SavedSquad>()
            {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 1),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 1),
            }, miningAsteroids[1].transform.localPosition, Vector2.zero);
            AddReinforcementSquads(new List<SavedSquad>()
            {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 1),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 1),
            }, miningAsteroids[2].transform.localPosition, Vector2.zero);
            AddReinforcementSquads(new List<SavedSquad>()
            {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 3),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 1),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 2, true, true),
            }, miningAsteroids[3].transform.localPosition, Vector2.zero);
            AddReinforcementSquads(new List<SavedSquad>()
            {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 1),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 2, true, true),
            }, miningAsteroids[4].transform.localPosition, Vector2.zero);

            Stage.Menus.TogglePausePanel();
            _dialogueTimer.Reuse(1.5f, () =>
                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_SeizeTheMeans.GetRange(2, 6)));
            AddTimer(_dialogueTimer);

            NextTriggers.Add(new Trigger(
                () => Stage.CutsceneManager.HitDialogueBreak,
                () =>
                {
                    Stage.Menus.TogglePausePanel();
                    Stage.EnablePlayerControl();
                    Stage.ActivateHiveMind = true;
                    SetupHivemind();

                    NextTriggers.Add(new Trigger(
                        () => State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide),
                        () =>
                        {
                            WinningSide = CampaignObjectiveRules.ResolveEliminationWinner(
                                State.IsSideKilled(ConfigData.Configuration.UserSide),
                                State.IsSideKilled(ConfigData.Configuration.AISide),
                                ConfigData.Configuration.UserSide,
                                ConfigData.Configuration.AISide);
                            CloseLevel();
                            if (WinningSide == ConfigData.Configuration.UserSide)
                            {
                                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_SeizeTheMeans[12]);
                                NextTriggers.Add(new Trigger(
                                    () => Stage.CutsceneManager.HitDialogueBreak,
                                    () => Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_SeizeTheMeans.GetRange(17, 31), true),
                                    "Level 4 Post-success dialogue"));
                            }
                            else
                            {
                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_SeizeTheMeans.GetRange(13, 4), true);
                            }
                        },
                        "Level 4 Mission Success or Fail dialogues"));
                },
                "Level 4 Start Level"));
        }

        public void Neptune2OfProduction()
        {
            Stage.Menus.SetMissionStatus("Survive and mine as many minerals as you can");
            HasContinuousTriggers = true;
            Stage.CutsceneManager.Setup(Neptune2Ending);

            SpawnMiningAsteroids(5, 5);
            List<MiningAsteroid> miningAsteroids = State.MiningAsteroids.ToList();
            miningAsteroids[0].transform.localPosition = new Vector2(80, 300);
            miningAsteroids[1].transform.localPosition = new Vector2(-320, -245);
            miningAsteroids[2].transform.localPosition = new Vector2(-270, 390);
            miningAsteroids[3].transform.localPosition = new Vector2(-20, -175);
            miningAsteroids[4].transform.localPosition = new Vector2(155, -120);

            Stage.Menus.TogglePausePanel();
            _dialogueTimer.Reuse(1.5f, () =>
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[2]));
            AddTimer(_dialogueTimer);

            NextTriggers.Add(new Trigger(
                () => Stage.CutsceneManager.HitDialogueBreak,
                () =>
                {
                    GameObject[] minimapIcons = new GameObject[5];
                    for (int i = 0; i < minimapIcons.Length; i++)
                    {
                        minimapIcons[i] = Instantiate(Stage.Prefabs.MinimapCircle, Map.transform);
                        minimapIcons[i].transform.localPosition = miningAsteroids[i].transform.localPosition;
                        minimapIcons[i].GetComponent<SpriteRenderer>().color = Color.red;
                        minimapIcons[i].transform.localScale = new Vector2(24, 24);
                    }

                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_OfProduction.GetRange(3, 5));
                    NextTriggers.Add(new Trigger(
                        () => Stage.CutsceneManager.HitDialogueBreak,
                        () =>
                        {
                            foreach (GameObject minimapIcon in minimapIcons)
                            {
                                Destroy(minimapIcon);
                            }

                            Tooltip basicTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                            basicTooltip.Show("To mine, first select your Factory ships, then right click on ore-rich asteroids. Once the Factory arrives, it will automatically begin collecting materials.", true);
                            basicTooltip.Place(Vector2.zero, new Vector2(150, 200));

                            Tooltip endMissionTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                            endMissionTooltip.Show("You can retreat from the mission at any time. Just send your ships to the green zone on the left side of the map where they can retreat to safety.", true);
                            endMissionTooltip.Place(new Vector2(-400, 0), new Vector2(150, 200));

                            GameObject exitBox = Instantiate(Stage.Prefabs.ExitZonePrefab, Map.transform);
                            exitBox.transform.localPosition = new Vector2(-512, 0);
                            exitBox.transform.localScale = new Vector2(50, 256);
                            Zone exitZone = exitBox.GetComponent<Zone>();
                            exitZone.OnShipEnter = ship =>
                            {
                                if (ship.Side == ConfigData.Configuration.UserSide)
                                {
                                    _lastShipRetreated = State.GetShips(ship.Side).Count(candidate => candidate.IsMobile) == 1;
                                    ship.EndKill();
                                }
                            };

                            NextTriggers.Add(new Trigger(
                                () => !basicTooltip.gameObject.activeSelf && !endMissionTooltip.gameObject.activeSelf,
                                () =>
                                {
                                    Destroy(basicTooltip);
                                    Destroy(endMissionTooltip);
                                    Stage.Menus.TogglePausePanel();
                                    Stage.EnablePlayerControl();
                                    StartNeptune2Waves();
                                },
                                "Level 5 Starting Level"));
                        },
                        "Level 5 Show mining tooltips"));
                },
                "Level 5 Highlighting Mining Locations"));
        }

        private void StartNeptune2Waves()
        {
            ScaledTimer wave1 = new ScaledTimer(120f, () =>
            {
                AddReinforcementSquads(new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                }, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);
                AddReinforcementsToHivemindCommandQueue();
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[8]);
            });
            AddTimer(wave1);

            ScaledTimer wave2 = new ScaledTimer(240f, () =>
            {
                AddReinforcementSquads(new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                }, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);
                AddReinforcementsToHivemindCommandQueue();
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[Utilities.RandomInt(5) + 10]);
            });
            AddTimer(wave2);

            ScaledTimer wave3 = new ScaledTimer(360f, () =>
            {
                AddReinforcementSquads(new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                }, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);
                AddReinforcementsToHivemindCommandQueue();
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[9]);
            });
            AddTimer(wave3);

            ScaledTimer wave5 = new ScaledTimer();
            ScaledTimer wave4 = new ScaledTimer(480f, () =>
            {
                List<SavedSquad> reinforcementSquads = ConfigData.CurrentShips.GetSavedSquads()
                    .Where(squad => squad.Side == ConfigData.Configuration.AISide && squad.Stats.BattlesFought > 0 && !squad.IsLoadedIntoLevel && squad.GetAliveSquadShips().Count > 0)
                    .ToList();
                reinforcementSquads.AddRange(new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                });
                AddReinforcementSquads(reinforcementSquads, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);
                AddReinforcementsToHivemindCommandQueue();
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[Utilities.RandomInt(5) + 10]);

                int waveCount = 4;
                wave5.Reuse(60f, () =>
                {
                    waveCount++;
                    reinforcementSquads = BuildNeptune2LateWave(waveCount);
                    AddReinforcementSquads(reinforcementSquads, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);
                    AddReinforcementsToHivemindCommandQueue();
                    Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[Utilities.RandomInt(5) + 10]);
                }, true);
                AddTimer(wave5);

                NextTriggers.Add(new Trigger(
                    () => State.IsSideKilled(ConfigData.Configuration.AISide),
                    () =>
                    {
                        CloseLevel();
                        CancelTimer(wave5);
                        Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[17], true);
                    },
                    "Level 5 Player Won dialogue"));
            });
            AddTimer(wave4);

            NextTriggers.Add(new Trigger(
                () => State.IsSideKilled(ConfigData.Configuration.UserSide) && !_lastShipRetreated,
                () =>
                {
                    CloseLevel();
                    CancelNeptune2Waves(wave1, wave2, wave3, wave4, wave5);
                    Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[16], true);
                },
                "Level 5 Player losing dialogue"));
            NextTriggers.Add(new Trigger(
                () => State.IsSideKilled(ConfigData.Configuration.UserSide) && _lastShipRetreated,
                () =>
                {
                    CloseLevel();
                    CancelNeptune2Waves(wave1, wave2, wave3, wave4, wave5);
                    Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[17], true);
                },
                "Level 5 Player retreating dialogue"));
        }

        private List<SavedSquad> BuildNeptune2LateWave(int waveCount)
        {
            List<SavedSquad> squads = new List<SavedSquad>()
            {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 6, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 6, true, true),
            };

            for (int i = 0; i < waveCount - 5; i++)
            {
                squads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true));
                squads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true));
                squads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true));
                squads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true));
                squads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true));
                squads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 6, true, true));
                squads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 6, true, true));
            }
            return squads;
        }

        private void CancelNeptune2Waves(params ScaledTimer[] waves)
        {
            foreach (ScaledTimer wave in waves)
            {
                CancelTimer(wave);
            }
        }

        public void SetRetreatForNeptune2()
        {
            Stage.Menus.CloseDialogue();
            Stage.Menus.RetreatButton.SetActive(false);
            ScaledTimer retreatDialogue = new ScaledTimer(1, () =>
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[15]));
            AddTimer(retreatDialogue);

            GameObject exitBox = Instantiate(Stage.Prefabs.ExitZonePrefab, Map.transform);
            exitBox.transform.localPosition = new Vector2(-512, 0);
            exitBox.transform.localScale = new Vector2(50, 256);
            Zone exitZone = exitBox.GetComponent<Zone>();
            exitZone.OnShipEnter = ship =>
            {
                if (ship.Side == ConfigData.Configuration.UserSide)
                {
                    _lastShipRetreated = State.GetShips(ship.Side).Count(candidate => candidate.IsMobile) == 1;
                    ship.EndKill();
                }
            };
        }
    }
}
