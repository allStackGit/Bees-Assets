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
        public void Uranus2OnTheDefensive()
        {
            Stage.ActivateHiveMind = false;
            HasContinuousTriggers = true;
            Stage.CutsceneManager.Setup(Uranus2Ending);

            Vector2[] upperPositions =
            {
                CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0),
                CurrentLevelOptions.AIStartingPosition
            };
            Vector2[] lowerPositions =
            {
                new Vector2(600, -512),
                new Vector2(450, -450)
            };

            SpawnMiningAsteroids(6, 6);
            List<MiningAsteroid> miningAsteroids = State.MiningAsteroids.ToList();
            miningAsteroids[0].transform.localPosition = new Vector2(235, -105);
            miningAsteroids[1].transform.localPosition = new Vector2(0, -300);
            miningAsteroids[2].transform.localPosition = new Vector2(20, -400);
            miningAsteroids[3].transform.localPosition = new Vector2(-80, -370);
            miningAsteroids[4].transform.localPosition = new Vector2(-350, 80);
            miningAsteroids[5].transform.localPosition = new Vector2(-200, 200);

            List<DialogueLine> dialogueLines = Stage.CutsceneManager.Uranus_OnTheDefensive.GetRange(2, 6);
            if (ConfigData.CurrentShips.HasShipsOfType(ConfigData.ShipTypes.Cruiser))
            {
                dialogueLines.Add(Stage.CutsceneManager.Uranus_OnTheDefensive[9]);
            }
            if (ConfigData.UserProgressData.HasMetAlejandraAndEmilia)
            {
                dialogueLines.AddRange(Stage.CutsceneManager.Uranus_OnTheDefensive.GetRange(10, 2));
            }

            Stage.Menus.TogglePausePanel();
            Tooltip endMissionTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
            endMissionTooltip.Show(
                "You can retreat from the mission at any time. Just send your ships to the green zone on the left side of the map where they can retreat to safety.",
                true);
            endMissionTooltip.Place(new Vector2(-400, 0), new Vector2(150, 250));
            CreateCampaignRetreatZone();
            Stage.Menus.SetMissionStatus("Survive and mine as many minerals as you can");

            NextTriggers.Add(new Trigger(
                () => !endMissionTooltip.gameObject.activeSelf,
                () =>
                {
                    Destroy(endMissionTooltip.gameObject);
                    SelectedCarrierTrigger();
                    NextTriggers.Add(new Trigger(
                        () => _hasSeenCarrierIntroIfNeeded,
                        () =>
                        {
                            Stage.Menus.TogglePausePanel();
                            StartUranus2Waves(upperPositions, lowerPositions, dialogueLines);
                        },
                        "Level 10 Start Wave Timers"));
                },
                "Level 10 Show carrier trigger dialogue"));
        }

        private void StartUranus2Waves(Vector2[] upperPositions, Vector2[] lowerPositions, List<DialogueLine> dialogueLines)
        {
            ScaledTimer wave1 = new ScaledTimer(120f, () =>
            {
                AddReinforcementSquads(new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 1, false, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                }, upperPositions[0], upperPositions[1]);
                AddReinforcementsToHivemindCommandQueue();
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheDefensive[1]);
            });
            AddTimer(wave1);

            ScaledTimer wave2 = new ScaledTimer(240f, () =>
            {
                AddReinforcementSquads(new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 1, false, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                }, upperPositions[0], upperPositions[1]);
                AddReinforcementsToHivemindCommandQueue();
                Stage.CutsceneManager.PlaySingleDialogueLine(dialogueLines[Utilities.RandomInt(dialogueLines.Count)]);
            });
            AddTimer(wave2);

            ScaledTimer wave3 = new ScaledTimer(360f, () =>
            {
                AddReinforcementSquads(new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                }, upperPositions[0], upperPositions[1]);
                AddReinforcementsToHivemindCommandQueue();
                Stage.CutsceneManager.PlaySingleDialogueLine(dialogueLines[Utilities.RandomInt(dialogueLines.Count)]);
            });
            AddTimer(wave3);

            ScaledTimer wave4 = new ScaledTimer(480f, () =>
            {
                AddReinforcementSquads(new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 6, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 6, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                }, upperPositions[0], upperPositions[1]);
                AddReinforcementSquads(new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                }, lowerPositions[0], lowerPositions[1]);
                AddReinforcementsToHivemindCommandQueue();
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheDefensive[8]);
            });
            AddTimer(wave4);

            ScaledTimer wave5 = new ScaledTimer(540f, () =>
            {
                List<SavedSquad> reinforcementSquads = ConfigData.CurrentShips.GetSavedSquads()
                    .Where(squad => squad.Side == ConfigData.Configuration.AISide && squad.Stats.BattlesFought > 0 && !squad.IsLoadedIntoLevel && squad.GetAliveSquadShips().Count > 0)
                    .ToList();
                reinforcementSquads.AddRange(new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Bumblebee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                });
                AddReinforcementSquads(reinforcementSquads, upperPositions[0], upperPositions[1]);

                reinforcementSquads = new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                };
                AddReinforcementSquads(reinforcementSquads, lowerPositions[0], lowerPositions[1]);
                AddReinforcementsToHivemindCommandQueue();
                Stage.CutsceneManager.PlaySingleDialogueLine(dialogueLines[Utilities.RandomInt(dialogueLines.Count)]);
            });
            AddTimer(wave5);

            ScaledTimer wave6 = new ScaledTimer(600f, () =>
            {
                AddReinforcementSquads(new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Bumblebee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                }, upperPositions[0], upperPositions[1]);
                AddReinforcementSquads(new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Bumblebee, 2, true, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                }, lowerPositions[0], lowerPositions[1]);
                AddReinforcementsToHivemindCommandQueue();
                Stage.CutsceneManager.PlaySingleDialogueLine(dialogueLines[Utilities.RandomInt(dialogueLines.Count)]);

                NextTriggers.Add(new Trigger(
                    () => State.IsSideKilled(ConfigData.Configuration.AISide),
                    () =>
                    {
                        WinningSide = ConfigData.Configuration.UserSide;
                        CloseLevel();
                        Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheDefensive[17]);
                        QueueUranus2FinalDialogue();
                    },
                    "Level 10 Bees defeated"));
            });
            AddTimer(wave6);

            NextTriggers.Add(new Trigger(
                () => State.IsSideKilled(ConfigData.Configuration.UserSide),
                () =>
                {
                    WinningSide = ConfigData.Configuration.AISide;
                    CloseLevel();
                    CancelUranus2Waves(wave1, wave2, wave3, wave4, wave5, wave6);
                    Stage.CutsceneManager.PlaySingleDialogueLine(
                        Stage.CutsceneManager.Uranus_OnTheDefensive[_lastShipRetreated ? 17 : 13]);
                    QueueUranus2FinalDialogue();
                },
                "Level 10 Player defeated or retreated"));
        }

        private void QueueUranus2FinalDialogue()
        {
            NextTriggers.Add(new Trigger(
                () => Stage.CutsceneManager.HitDialogueBreak,
                () => Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheDefensive.GetRange(14, 3), true),
                "Level 10 Ended dialogue"));
        }

        private void CancelUranus2Waves(params ScaledTimer[] waves)
        {
            foreach (ScaledTimer wave in waves)
            {
                CancelTimer(wave);
            }
        }

        public void SetRetreatForUranus2()
        {
            Stage.Menus.CloseDialogue();
            Stage.Menus.RetreatButton.SetActive(false);
            ScaledTimer retreatDialogue = new ScaledTimer(1, () =>
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheDefensive[12]));
            AddTimer(retreatDialogue);
            CreateCampaignRetreatZone();
        }

        private Zone CreateCampaignRetreatZone()
        {
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
            return exitZone;
        }
    }
}
