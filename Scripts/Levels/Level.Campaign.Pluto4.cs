using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.UI_Components;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Assets.Scripts.ConfigData;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        public void Pluto4BluerPasturesCampaign()
        {
            FishTankTrigger();
            Stage.EnablePlayerControl();
            HasContinuousTriggers = true;
            int personnelLost = 0;
            int personnelEvacuated = 0;
            ScaledTimer clock = new ScaledTimer();
            ScaledTimer wave2 = new ScaledTimer();
            ScaledTimer wave3 = new ScaledTimer();
            ScaledTimer wave4 = new ScaledTimer();
            Stage.ActivateHiveMind = false;
            Stage.Menus.GameSpeedButton.GetComponent<RectTransform>().anchoredPosition = new Vector2(-290, -15);
            Stage.CutsceneManager.Setup(Pluto4Ending);
            Stage.Menus.TogglePausePanel();
            _dialogueTimer.Reuse(1.5f, () =>
                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(1, 4)));
            AddTimer(_dialogueTimer);
            Stage.Menus.SetMissionStatus("Survive and defend Pluto!");

            NextTriggers.Add(new Trigger(
                () => Stage.CutsceneManager.HitDialogueBreak,
                () =>
                {
                    GameObject plutoCircle = Instantiate(Stage.Menus.PlutoCircle, Map.transform);
                    plutoCircle.SetActive(true);
                    List<DialogueLine> plutoLines = Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(5, 2);
                    HashSet<ShipTypes> shipTypes = State.GetHumanShipTypes();
                    if (shipTypes.Contains(ConfigData.ShipTypes.Dreadnought)) plutoLines.Add(Stage.CutsceneManager.PlutoLines_BluerPastures[7]);
                    if (shipTypes.Contains(ConfigData.ShipTypes.Gunship)) plutoLines.Add(Stage.CutsceneManager.PlutoLines_BluerPastures[8]);
                    if (shipTypes.Contains(ConfigData.ShipTypes.Frigate)) plutoLines.Add(Stage.CutsceneManager.PlutoLines_BluerPastures[9]);
                    plutoLines.AddRange(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(10, 3));
                    Stage.CutsceneManager.PlayDialogueSection(plutoLines);

                    NextTriggers.Add(new Trigger(
                        () => Stage.CutsceneManager.HitDialogueBreak,
                        () =>
                        {
                            Destroy(plutoCircle);
                            bool hasSeenFleetMessages = false;

                            if (!ConfigData.UserProgressData.ShowToolTips)
                            {
                                // The opening dialogue leaves the level paused. When tutorial
                                // tooltips are disabled there is no tooltip callback to resume it,
                                // so bypass the tutorial UI and resume combat explicitly.
                                Stage.Menus.TogglePausePanel();
                                hasSeenFleetMessages = true;
                            }
                            else
                            {
                                Tooltip basicTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                                basicTooltip.Show("As the war campaign progresses, you may lose ships that you bring into battles. These ships are gone forever. Your fleet will still find a way forward, even if you lose all the ships you brought into battle. But if you lose all of the ships in your fleet, the campaign will end.", true);
                                basicTooltip.Place(Vector2.zero, new Vector2(150, 350));

                                NextTriggers.Add(new Trigger(
                                    () => !basicTooltip.gameObject.activeSelf,
                                    () =>
                                    {
                                        basicTooltip.Show("Similarly, the Bees have a finite number of resources. The more enemy ships you destroy in each mission, the less the Bee threat will have for their entire invasion. But the same is true in reverse: if you don’t destroy many ships, they can come back to haunt you later on.", true);
                                        NextTriggers.Add(new Trigger(
                                            () => !basicTooltip.gameObject.activeSelf,
                                            () =>
                                            {
                                                basicTooltip.Show("In this mission, the more personnel you evacuate, the more ships you'll have for your fleet. Play strategically to preserve as much of your own fleet while whittling down the Bees' numbers. Good luck, Commander.", true);
                                                NextTriggers.Add(new Trigger(
                                                    () => !basicTooltip.gameObject.activeSelf,
                                                    () =>
                                                    {
                                                        Stage.Menus.TogglePausePanel();
                                                        hasSeenFleetMessages = true;
                                                        Destroy(basicTooltip.gameObject);
                                                    },
                                                    "Level 3 Hiding the 3rd message"));
                                            },
                                            "Level 3 Showing 3rd message"));
                                    },
                                    "Level 3 Showing 2nd message"));
                            }

                            NextTriggers.Add(new Trigger(
                                () => hasSeenFleetMessages,
                                () =>
                                {
                                    float endTime = Time.time + 300.49f;
                                    float timeLeft = endTime - Time.time;
                                    TMP_Text clockText = Stage.Menus.Clock.transform.GetChild(0).GetComponent<TMP_Text>();
                                    TMP_Text counterText = Stage.Menus.Counter.transform.GetChild(0).GetComponent<TMP_Text>();
                                    Stage.Menus.Counter.transform.GetChild(1).GetComponent<TMP_Text>().text = "Evacuated";
                                    Stage.Menus.Clock.SetActive(true);
                                    Stage.Menus.Counter.SetActive(true);
                                    Stage.Menus.PlutoShield.SetActive(true);
                                    HumanTarget humanTarget = CreateHumanTarget(new Vector2(68, -28));

                                    clock.Reuse(1f, () =>
                                    {
                                        timeLeft = endTime - Time.time;
                                        int minutesLeft = Mathf.FloorToInt(timeLeft / 60f);
                                        int secondsLeft = Mathf.FloorToInt(timeLeft % 60f);
                                        personnelLost = (humanTarget.MaxHealth - humanTarget.Health) / 200;
                                        Stage.Menus.PlutoShieldHealthBar.transform.localScale = new Vector2(((float)(15 - personnelLost) / 15) * 150, 1);

                                        if (timeLeft <= 0 || personnelLost >= 15)
                                        {
                                            _questPoints = personnelEvacuated;
                                            CancelTimer(clock);
                                            CancelTimer(wave2);
                                            CancelTimer(wave3);
                                            CancelTimer(wave4);
                                            CloseLevel();
                                            if (personnelLost >= 15)
                                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(18, 4), true);
                                            else if (personnelLost == 0)
                                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(22, 6), true);
                                            else
                                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(22, 4), true);
                                        }
                                        else
                                        {
                                            if (Mathf.RoundToInt(timeLeft) % 5 == 0) personnelEvacuated++;
                                            counterText.text = $"{personnelEvacuated}";
                                            clockText.text = $"{minutesLeft}:{secondsLeft:D2}";
                                        }
                                    }, true);
                                    AddTimer(clock);

                                    List<SavedSquad> firstSquads = ConfigData.CurrentShips.GetSavedSquads()
                                        .Where(squad => squad.Side == ConfigData.Configuration.AISide && squad.Stats.BattlesFought > 0 && squad.GetAliveSquadShips().Count > 0)
                                        .ToList();
                                    firstSquads.AddRange(new List<SavedSquad>()
                                    {
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                                    });
                                    AddReinforcementSquads(firstSquads, StartingPositions[ConfigData.Configuration.AISide - 1] + new Vector2(0, 50), StartingPositions[ConfigData.Configuration.AISide - 1]);
                                    Stage.ActivateHiveMind = true;
                                    SetupHivemind();

                                    wave2.Reuse(60f, () =>
                                    {
                                        AddReinforcementSquads(new List<SavedSquad>()
                                        {
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                                        }, new Vector2(-295, -195), new Vector2(-185, -170));
                                        AddReinforcementsToHivemindCommandQueue();
                                    });
                                    AddTimer(wave2);

                                    wave3.Reuse(120f, () =>
                                    {
                                        AddReinforcementSquads(new List<SavedSquad>()
                                        {
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                                        }, new Vector2(395, 245), new Vector2(180, 200));
                                        AddReinforcementsToHivemindCommandQueue();
                                    });
                                    AddTimer(wave3);

                                    wave4.Reuse(210f, () =>
                                    {
                                        AddReinforcementSquads(new List<SavedSquad>()
                                        {
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2, true, true),
                                        }, StartingPositions[ConfigData.Configuration.AISide - 1] + new Vector2(0, 50), StartingPositions[ConfigData.Configuration.AISide - 1]);
                                        AddReinforcementsToHivemindCommandQueue();
                                    });
                                    AddTimer(wave4);

                                    NextTriggers.Add(new Trigger(
                                        () => Stage.Pool.SplitShotProjectilePool.CountAll > 0,
                                        () => Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.PlutoLines_BluerPastures[14]),
                                        "Level 3 Leafcutter splitter shot"));
                                    NextTriggers.Add(new Trigger(
                                        () => State.ShipsToRelease.Any(ship => ship.ShipType == ConfigData.ShipTypes.YellowJacket && ship.IsDead && ((YellowJacket)ship).ContactedShip != null) && !(timeLeft <= 0 || personnelLost >= 15),
                                        () => Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(15, 3)),
                                        "Level 3 Yellow Jacket Hitting ship"));
                                },
                                "Level 3 Starting combat"));
                        },
                        "Level 3 Starting combat messages"));
                },
                "Level 3 Showing Pluto outline and dialogue"));
        }
    }
}
