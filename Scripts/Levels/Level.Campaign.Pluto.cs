using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
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
        public void Pluto1Anomaly()
        {
            FishTankTrigger();
            Debug.Log("Setting triggers for level 0");

            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>()
            {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Scout, 1),
            }, StartingPositions[ConfigData.Configuration.UserSide - 1], Vector2.zero, false);
            Scout firstScout = (Scout)State.GetHumanShips().First();

            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>()
            {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 1),
            }, StartingPositions[ConfigData.Configuration.AISide - 1], Vector2.zero, false);
            Honeybee firstHoneybee = (Honeybee)State.GetBeeShips().First();

            Gunship firstGunship = null;
            int passes = 0;
            bool hasBeenUserControlled = false;
            bool gunshipHasReachedCenterPosition = false;
            HasContinuousTriggers = true;
            ScaledTimer endLevelTimer = new ScaledTimer();
            ScaledTimer cameraMovement = new ScaledTimer();
            Tooltip moveScoutTooltip = null;

            firstScout.ProximityCollider = Instantiate(
                Stage.Prefabs.HumanProximityColliderPrefab,
                Vector3.zero,
                Quaternion.identity,
                firstScout.transform).GetComponent<ProximityCollider>();
            firstScout.HasProximityCollider = true;

            int originalSight = firstScout.Sight;
            firstScout.Sight = 60;
            firstScout.ProximityCollider.Create(firstScout);
            firstScout.Sight = originalSight;
            firstScout.ProximityCollider.transform.localPosition = Vector3.zero;
            firstScout.ProximityCollider.Activate();
            firstScout.CanDropBeacons = false;
            firstScout.ChargingBar.gameObject.SetActive(false);

            Stage.Menus.SurrenderButton.SetActive(false);
            Stage.Camera.orthographicSize += 15;
            Stage.InputManager.MaintainScrollBoundary();

            firstHoneybee.Squad.HasCommandQueue = true;
            firstHoneybee.Squad.CommandQueueEmptyAction = () =>
            {
                MoveToPoint moveToPoint = (MoveToPoint)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToPoint);
                moveToPoint.Setup(firstHoneybee.Squad, false, null, null, new Vector2(70, -30));
                firstHoneybee.Squad.CommandQueue.Enqueue(moveToPoint);

                for (int i = 0; i < 5; i++)
                {
                    MoveToRandom moveToRandom = (MoveToRandom)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToRandom);
                    moveToRandom.Setup(firstHoneybee.Squad, false, null, null, 16);
                    firstHoneybee.Squad.CommandQueue.Enqueue(moveToRandom);
                }
                firstHoneybee.Squad.RunCommandQueue();
            };
            firstHoneybee.Squad.RunCommandQueue();

            Stage.CutsceneManager.Setup(() => Pluto1Ending(firstGunship.Squad.SavedSquad));
            Stage.CutsceneManager.StartCutScene();
            Stage.EnablePlayerControl();
            State.SelectSquads(new List<Squad>());
            Stage.Menus.ToggleMiniMapDisplay();
            Stage.Menus.MiniMapOpenButton.SetActive(false);
            Stage.Menus.SetMissionStatus("Scout and explore around Pluto");

            float startTime = Time.realtimeSinceStartup;

            Triggers.AddRange(new List<Trigger>()
            {
                new Trigger(
                    () => firstScout.ProximityCollider.NearbyEnemyShips.Contains(firstHoneybee),
                    () =>
                    {
                        Stage.Menus.MissionStatus.SetActive(false);
                        State.SelectSquads(new List<Squad>());
                        firstScout.Squad.StopMoving();
                        firstScout.Squad.CanAcceptUserInput = false;
                        firstScout.Squad.FinalizeUserCommand();

                        GameObject alarm = Instantiate(Stage.Prefabs.AlarmReactionPrefab);
                        alarm.transform.SetParent(firstScout.transform);
                        alarm.transform.localPosition = new Vector2(3, 1.5f);
                        alarm.transform.eulerAngles = Vector3.zero;
                        Stage.CameraShip = firstScout;
                        Stage.IsFollowingShip = true;

                        Utilities.Shake(this, alarm, 1.5f, () =>
                        {
                            Destroy(alarm);
                            firstScout.Squad.HasCommandQueue = true;
                            firstScout.Squad.CommandQueueEmptyAction = () =>
                            {
                                Stage.IsFollowingShip = false;
                                Stage.SetupCamera();
                                Stage.Menus.ToggleMiniMapDisplay();
                                Stage.Menus.MiniMapOpenButton.SetActive(false);
                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Anomaly.GetRange(0, 11));
                                firstScout.EndKill();
                            };

                            firstScout.CanOverrideBounds = true;
                            MoveToPoint moveToPoint = (MoveToPoint)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToPoint);
                            moveToPoint.Setup(
                                firstScout.Squad,
                                false,
                                null,
                                null,
                                StartingPositions[ConfigData.Configuration.UserSide - 1] - new Vector2(0, 150));
                            firstScout.Squad.CommandQueue.Enqueue(moveToPoint);
                            firstScout.Squad.RunCommandQueue();
                        });
                    },
                    "Level 0 Looking for Honeybee Trigger"),

                new Trigger(
                    () =>
                    {
                        hasBeenUserControlled = firstScout.DistanceToPoint(StartingPositions[firstScout.Side - 1]) > 3;
                        return hasBeenUserControlled || Time.realtimeSinceStartup - startTime > 10;
                    },
                    () =>
                    {
                        if (!hasBeenUserControlled)
                        {
                            // Do not shadow this variable; a later trigger owns its cleanup.
                            moveScoutTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                            moveScoutTooltip.Show(
                                "You can select the ship by left clicking on it. You can then move the ship by right clicking somewhere in space.",
                                true);
                            moveScoutTooltip.Place(Vector2.zero, new Vector2(150, 150));
                        }
                    },
                    "Level 0 Waiting for user to control Scout Trigger"),

                new Trigger(
                    () =>
                    {
                        hasBeenUserControlled = firstScout.DistanceToPoint(StartingPositions[firstScout.Side - 1]) > 3 ||
                            !ConfigData.UserProgressData.ShowToolTips;
                        return hasBeenUserControlled;
                    },
                    () =>
                    {
                        if (hasBeenUserControlled && ConfigData.UserProgressData.ShowToolTips)
                        {
                            if (moveScoutTooltip != null)
                            {
                                Destroy(moveScoutTooltip.gameObject);
                            }
                            Stage.Menus.WASDTooltip.SetActive(true);
                            Vector2 initialCameraPosition = Stage.Camera.transform.position;
                            NextTriggers.Add(new Trigger(
                                () => Vector2.Distance(Stage.Camera.transform.position, initialCameraPosition) > 10,
                                () => Stage.Menus.WASDTooltip.SetActive(false),
                                "Level 0 Waiting for user to control camera Trigger"));
                        }
                    },
                    "Level 0 Waiting for user to control Scout to hide tooltip Trigger"),

                new Trigger(
                    () => Stage.CutsceneManager.HitDialogueBreak,
                    () =>
                    {
                        AddReinforcementSquads(new List<SavedSquad>()
                        {
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Gunship, 1),
                        }, StartingPositions[ConfigData.Configuration.UserSide - 1] - new Vector2(0, 100), Vector2.zero);

                        firstGunship = (Gunship)State.GetHumanShips().First();
                        firstGunship.Squad.CanAcceptUserInput = false;
                        firstGunship.Squad.FinalizeUserCommand();
                        firstGunship.Squad.SetSquadCeaseFire(true);
                        Stage.CameraShip = firstGunship;
                        Stage.IsFollowingShip = true;
                        firstGunship.Squad.HasCommandQueue = true;
                        firstGunship.Squad.CommandQueueEmptyAction = () =>
                        {
                            gunshipHasReachedCenterPosition = true;
                            Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.PlutoLines_Anomaly[11]);
                        };

                        MoveToPoint moveToPoint = (MoveToPoint)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToPoint);
                        moveToPoint.Setup(
                            firstGunship.Squad,
                            false,
                            null,
                            null,
                            StartingPositions[ConfigData.Configuration.UserSide - 1] + new Vector2(0, 40));
                        firstGunship.Squad.CommandQueue.Enqueue(moveToPoint);
                        firstGunship.Squad.RunCommandQueue();
                    },
                    "Level 0 Waiting for dialogue break after Lt. Tom Intro"),

                new Trigger(
                    () => gunshipHasReachedCenterPosition,
                    () =>
                    {
                        firstGunship.Squad.CommandQueueEmptyAction = () => { };
                        Aggressive aggressive = (Aggressive)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Aggressive);
                        aggressive.Setup(firstGunship.Squad, false, firstHoneybee.Squad, null);
                        firstGunship.Squad.CommandQueue.Enqueue(aggressive);
                        firstGunship.Squad.RunCommandQueue();

                        NextTriggers.Add(new Trigger(
                            () => firstGunship.Weapons.First().GetEnemyShipsWithinRange().Count > 0,
                            () =>
                            {
                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Anomaly.GetRange(12, 9));
                                NextTriggers.Add(new Trigger(
                                    () => Stage.CutsceneManager.HitDialogueBreak,
                                    () =>
                                    {
                                        Stage.Menus.MiniMapOpenButton.SetActive(true);
                                        Stage.Menus.ToggleMiniMapDisplay();
                                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Anomaly.GetRange(21, 2));
                                        CurrentLevelOptions.HasSquadActionBox = true;
                                        Stage.Menus.ActionBox.Setup(Stage, this, Stage.EventSystem, ConfigData.Configuration.UserSide);

                                        NextTriggers.Add(new Trigger(
                                            () => Stage.CutsceneManager.HitDialogueBreak,
                                            () =>
                                            {
                                                Stage.Menus.MissionStatus.SetActive(true);
                                                Stage.Menus.SetMissionStatus("Find and destroy the enemy ship");
                                                firstGunship.Squad.CanAcceptUserInput = true;
                                                firstGunship.Squad.HasCommandQueue = false;
                                                Stage.IsFollowingShip = false;
                                                aggressive.SetFinalize("Honeybee reached by gunship, ceding to user control");

                                                Tooltip controlGunshipTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                                                controlGunshipTooltip.Show(
                                                    "You can select the ship by left clicking on it or by left clicking and dragging a selection box around it.",
                                                    true);
                                                controlGunshipTooltip.Place(Vector2.zero, new Vector2(150, 150));

                                                NextTriggers.Add(new Trigger(
                                                    () => firstGunship.Squad.IsSelected,
                                                    () =>
                                                    {
                                                        controlGunshipTooltip.Show(
                                                            "You'll want to familiarize yourself with the controls to your bottom left. They aren't usually required, but they are helpful.",
                                                            true);
                                                        controlGunshipTooltip.Place(new Vector2(-500, 0), new Vector2(150, 150));
                                                        NextTriggers.Add(new Trigger(
                                                            () => ++passes > 2,
                                                            () =>
                                                            {
                                                                Tooltip attackOnSightTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                                                                attackOnSightTooltip.Show(
                                                                    "When you're ready to engage the Honeybee, click \"Attack on Sight\" (the red exclamation point) to disable the Cease Fire. Once the Gunship is within range it will automatically fire upon the Honeybee. To chase after an enemy ship, right click on it.",
                                                                    true);
                                                                attackOnSightTooltip.Place(new Vector2(0, -50), new Vector2(150, 300));
                                                            },
                                                            "Level 0 Showing Attack on Sight tooltip prompt"));
                                                    },
                                                    "Level 0 Showing squad controls tooltip when squad is selected"));

                                                AddReinforcementSquads(new List<SavedSquad>()
                                                {
                                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 1),
                                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 3, true),
                                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 3, true),
                                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 2, true),
                                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                                                }, StartingPositions[ConfigData.Configuration.AISide - 1] + new Vector2(0, 50), StartingPositions[ConfigData.Configuration.AISide - 1]);

                                                NextTriggers.Add(new Trigger(
                                                    () => firstHoneybee.IsDead,
                                                    () =>
                                                    {
                                                        Stage.Menus.TogglePausePanel();
                                                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Anomaly.GetRange(23, 2));
                                                        List<Squad> beeSquads = State.GetSquadsBySide(ConfigData.Configuration.AISide);
                                                        for (int i = 1; i < beeSquads.Count; i++)
                                                        {
                                                            beeSquads[i].SetStartingPosition(
                                                                beeSquads[i].GetPosition() + new Vector2((i % 2 == 0 ? 1 : -1) * 25 * i, 0));
                                                        }

                                                        NextTriggers.Add(new Trigger(
                                                            () => Stage.CutsceneManager.HitDialogueBreak,
                                                            () =>
                                                            {
                                                                Stage.Menus.TogglePausePanel();
                                                                Stage.Menus.ToggleFogOfWar();
                                                                Stage.Menus.MissionStatus.SetActive(false);
                                                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Anomaly.GetRange(25, 2));
                                                                Stage.CameraTargetPosition = StartingPositions[ConfigData.Configuration.AISide - 1];
                                                                Stage.IsCameraMovingToTarget = true;
                                                                Stage.IsPlayerControlling = false;
                                                                int ticks = 0;

                                                                cameraMovement.Reuse(.5f, () =>
                                                                {
                                                                    if (!Stage.IsCameraMovingToTarget || ticks >= 20)
                                                                    {
                                                                        Stage.IsCameraMovingToTarget = false;
                                                                        CancelTimer(cameraMovement);
                                                                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Anomaly.GetRange(27, 5), true);
                                                                        endLevelTimer.Reuse(2, CloseLevel);
                                                                        AddTimer(endLevelTimer);
                                                                    }
                                                                    else
                                                                    {
                                                                        ticks++;
                                                                    }
                                                                }, true);
                                                                AddTimer(cameraMovement);
                                                            },
                                                            "Level 0 Showing dialogue after Bees approach"));
                                                    },
                                                    "Level 0 Showing Dialogue after defeating Honeybee"));
                                            },
                                            "Level 0 Showing prompt for user controls"));
                                    },
                                    "Level 0 Displaying HUD and Minimap"));
                            },
                            "Level 0 Triggering dialogue when Honeybee is reached by Gunship"));
                    },
                    "Level 0 Pursuing Honeybee")
            });
        }

        public void Pluto2Reinforcements()
        {
            Stage.Menus.MissionStatus.SetActive(false);
            Stage.Menus.SurrenderButton.SetActive(false);
            FishTankTrigger();
            HasContinuousTriggers = true;
            Tooltip basicTooltip = null;
            Tooltip selectMultiple = null;
            Tooltip rangeTooltip = null;
            GameObject highlightTooltipObject = null;
            GameObject squadNumberHighlight = null;

            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>()
            {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Scout, 1),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Scout, 1),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Gunship, 2, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Gunship, 2, true),
            }, StartingPositions[ConfigData.Configuration.UserSide - 1], Vector2.zero, false);

            Squad scoutSquad = State.GetSquadByNumber(ConfigData.Configuration.HumanSide, 1);
            Squad scoutSquad2 = State.GetSquadByNumber(ConfigData.Configuration.HumanSide, 2);
            Squad gunshipSquad = State.GetSquadByNumber(ConfigData.Configuration.HumanSide, 3);
            Squad gunshipSquad2 = State.GetSquadByNumber(ConfigData.Configuration.HumanSide, 4);
            RectTransform rectTransform = null;

            State.SelectSquads(new List<Squad>());
            gunshipSquad.CanAcceptUserInput = false;
            scoutSquad.CanAcceptUserInput = false;
            scoutSquad2.CanAcceptUserInput = false;
            gunshipSquad2.CanAcceptUserInput = false;
            Stage.ActivateHiveMind = false;
            Stage.CutsceneManager.Setup(Pluto2Ending);
            Stage.Menus.TogglePausePanel();

            _dialogueTimer.Reuse(1.5f, () =>
                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Reinforcements.GetRange(2, 1)));
            AddTimer(_dialogueTimer);

            Triggers.Add(new Trigger(
                () => Stage.CutsceneManager.HitDialogueBreak,
                () =>
                {
                    Stage.Menus.TogglePausePanel();
                    Stage.Menus.MissionStatus.SetActive(true);
                    Stage.Menus.SetMissionStatus("Use the Scout");
                    Stage.EnablePlayerControl();
                    scoutSquad.CanAcceptUserInput = true;

                    if (ConfigData.UserProgressData.ShowToolTips)
                    {
                        basicTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                        basicTooltip.Show("Select the Scout squad with the left mouse button.", true);
                        basicTooltip.Place(new Vector2(200, 0), new Vector2(150, 100));

                        highlightTooltipObject = Instantiate(Stage.Menus.HighlightTooltipPrefab, Map.transform);
                        highlightTooltipObject.SetActive(true);
                        highlightTooltipObject.transform.position = scoutSquad.GetPosition();
                        highlightTooltipObject.transform.localScale = new Vector2(scoutSquad.GetWidth() + 2, scoutSquad.GetHeight() + 2);
                    }

                    NextTriggers.Add(new Trigger(
                        () => scoutSquad.IsSelected,
                        () =>
                        {
                            GameObject pointerA = null;
                            GameObject pointerB = null;
                            if (ConfigData.UserProgressData.ShowToolTips)
                            {
                                highlightTooltipObject.SetActive(false);
                                basicTooltip.Show("Here are different settings for your ship.You can determine your squad’s flight pattern and shooting strategies here. Take some time to familiarize yourself with these options.", true);
                                basicTooltip.Place(new Vector2(-175, -150), new Vector2(150, 225));

                                pointerA = Instantiate(Stage.Menus.PointerArrow, Stage.Menus.UIOverlay.transform);
                                rectTransform = pointerA.GetComponent<RectTransform>();
                                rectTransform.localPosition = new Vector2(-447, -238);
                                rectTransform.eulerAngles = new Vector3(0, 0, 170);
                                pointerA.SetActive(true);

                                pointerB = Instantiate(Stage.Menus.PointerArrow, Stage.Menus.UIOverlay.transform);
                                rectTransform = pointerB.GetComponent<RectTransform>();
                                rectTransform.localPosition = new Vector2(-330, -340);
                                rectTransform.eulerAngles = new Vector3(0, 0, 90);
                                rectTransform.localScale = new Vector2(0.25f, 0.5f);
                                pointerB.SetActive(true);
                            }

                            NextTriggers.Add(new Trigger(
                                () => !ConfigData.UserProgressData.ShowToolTips || !basicTooltip.gameObject.activeSelf,
                                () =>
                                {
                                    Stage.Menus.SetMissionStatus("Try out the different squads");
                                    gunshipSquad.CanAcceptUserInput = true;
                                    scoutSquad2.CanAcceptUserInput = true;
                                    gunshipSquad2.CanAcceptUserInput = true;

                                    if (ConfigData.UserProgressData.ShowToolTips)
                                    {
                                        Destroy(pointerA);
                                        Destroy(pointerB);
                                        squadNumberHighlight = Instantiate(Stage.Menus.UIHighlightTooltipPrefab, Stage.Menus.UIOverlay.transform);
                                        squadNumberHighlight.SetActive(true);
                                        squadNumberHighlight.transform.localPosition = new Vector2(-610, 370);
                                        squadNumberHighlight.transform.localScale = new Vector2(150, 30);
                                        squadNumberHighlight.transform.SetAsFirstSibling();

                                        basicTooltip.Show("You can also select squads with the number hotkeys on your keyboard. These are displayed at the top of the screen.", true);
                                        basicTooltip.Place(new Vector2(-550, 300), new Vector2(150, 150));
                                        selectMultiple = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                                        selectMultiple.Show("If you need to select multiple squads, click and drag the mouse over the squads.", true);
                                        selectMultiple.Place(new Vector2(-200, 0), new Vector2(150, 100));
                                        rangeTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                                        rangeTooltip.Show("Your ships with weapons will automatically shoot at any enemies in range. You can view your selected ships’ range at any time by holding <b>R</b>.You can also manually fire towards your cursor with any selected ships by pressing <b>F</b>", true);
                                        rangeTooltip.Place(new Vector2(200, 0), new Vector2(150, 280));
                                    }

                                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Reinforcements.GetRange(3, 2));
                                    Stage.Menus.TogglePausePanel();

                                    NextTriggers.Add(new Trigger(
                                        () => Stage.CutsceneManager.HitDialogueBreak,
                                        () =>
                                        {
                                            if (ConfigData.UserProgressData.ShowToolTips)
                                            {
                                                Destroy(squadNumberHighlight);
                                            }
                                            Stage.Menus.TogglePausePanel();
                                            Stage.Menus.SetMissionStatus("Find and destroy the enemy ships!");

                                            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>()
                                            {
                                                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 1),
                                                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 3, true),
                                                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 3, true),
                                            }, new Vector2(0, 316), StartingPositions[ConfigData.Configuration.AISide - 1], true);

                                            Stage.ActivateHiveMind = true;
                                            SetupHivemind();
                                            NextTriggers.Add(new Trigger(
                                                () => State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide),
                                                () =>
                                                {
                                                    if (State.IsSideKilled(ConfigData.Configuration.AISide))
                                                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Reinforcements.GetRange(5, 1), true);
                                                    else
                                                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Reinforcements.GetRange(6, 1), true);
                                                    CloseLevel();
                                                },
                                                "Level 1 ending"));
                                        },
                                        "Level 1 start combat"));
                                },
                                "Level 1 squad controls complete"));
                        },
                        "Level 1 scout selected"));
                },
                "Level 1 opening dialogue complete"));
        }

        public void Pluto3Pushback()
        {
            Stage.Menus.SetMissionStatus("Push back the enemy!");
            HasContinuousTriggers = true;
            Stage.Menus.MissionStatus.SetActive(false);
            FishTankTrigger();
            Stage.CutsceneManager.Setup(Pluto3Ending);
            Stage.ActivateHiveMind = false;
            Stage.Menus.TogglePausePanel();

            _dialogueTimer.Reuse(1.5f, () =>
                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Pushback.GetRange(1, 12)));
            AddTimer(_dialogueTimer);

            NextTriggers.Add(new Trigger(
                () => Stage.CutsceneManager.HitDialogueBreak,
                () =>
                {
                    AddReinforcementSquads(new List<SavedSquad>()
                    {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                    }, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);
                    Stage.EnablePlayerControl();
                    Stage.Menus.TogglePausePanel();
                    Stage.Menus.MissionStatus.SetActive(true);
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
                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Pushback.GetRange(13, 9), true);
                            else
                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Pushback.GetRange(22, 9), true);
                        },
                        "Level 2 Ending dialogue"));
                },
                "Level 2 start"));
        }

        public void Pluto4BluerPastures()
        {
            FishTankTrigger();
            Stage.EnablePlayerControl();
            HasContinuousTriggers = true;
            int personnelLost = 0;
            int personnelEvacuated = 0;
            ScaledTimer clock = new ScaledTimer();
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
                            bool hasSeenFleetMessages = !ConfigData.UserProgressData.ShowToolTips;
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

                                    ScaledTimer wave2 = new ScaledTimer(60f, () =>
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

                                    ScaledTimer wave3 = new ScaledTimer(120f, () =>
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

                                    ScaledTimer wave4 = new ScaledTimer(210f, () =>
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
