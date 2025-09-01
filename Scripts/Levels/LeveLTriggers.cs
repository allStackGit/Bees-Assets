using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using static Assets.Scripts.ConfigData;

namespace Assets.Scripts.Levels
{
    public partial class Level : MonoBehaviour
    {

        /// <summary>
        /// Sets all the triggers for events in the level. Triggers are checked every 5 seconds so this currently has a maximum precision of 5 seconds
        /// </summary>
        private void SetTriggers()
        {
            Triggers.Clear();

            switch (CurrentLevelOptions.Id)
            {
                case 0:
                    Level0Triggers();
                    break;
            }
        }

        public void Level0Triggers()
        {
            Debug.Log("Setting triggers for level 0");
            // Setup specifics for the level
            Scout firstScout = (Scout)State.GetHumanShips().First();
            Honeybee firstHoneybee = (Honeybee)State.GetBeeShips().First();
            Gunship firstGunship = null;
            int checksForUserControl = 0;
            int passes = 0;
            bool hasBeenUserControlled = false;
            bool gunshipHasReachedCenterPosition = false;
            HasContinuousTriggers = true;
            ScaledTimer _beesPursuitTimer = new ScaledTimer();
            ExitZone exitZone = null;
            GameObject moveScoutTooltip = null;
            GameObject controlGunshipTooltip = null;
            GameObject attackOnSightTooltip = null;
            GameObject flydownTooltip = null;
            Vector2 initialCameraPosition = Stage.Camera.transform.position;


            // Setup proximity collider for the scout
            firstScout.ProximityCollider = Instantiate(Stage.Prefabs.HumanProximityColliderPrefab, Vector3.zero, Quaternion.identity, firstScout.transform).GetComponent<ProximityCollider>();
            firstScout.HasProximityCollider = true;
            firstScout.ProximityCollider.Create(firstScout);
            firstScout.ProximityCollider.transform.localPosition = Vector3.zero;
            firstScout.ProximityCollider.Activate();

            firstScout.CanDropBeacons = false;
            firstScout.ChargingBar.gameObject.SetActive(false);

            // Hide the surrender button
            Stage.SurrenderButton.SetActive(false);

            // Zoom out the camera a bit
            Stage.Camera.orthographicSize += 15;
            Stage.InputManager.MaintainScrollBoundary();

            // Add commands to the queue for the honeybee to move near Pluto
            firstHoneybee.Squad.HasCommandQueue = true;
            firstHoneybee.Squad.CommandQueueEmptyAction = () =>
            {
                //Debug.Log($"Refilling command queue");
                MoveToPoint moveToPoint = (MoveToPoint)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToPoint);
                moveToPoint.Setup(firstHoneybee.Squad, false, null, null, new Vector2(70, -30)); // Center of Pluto
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

            // [alert] maybe this should be for all levels
            Stage.CutsceneManager.Setup();
            Stage.CutsceneManager.StartCutScene();

            //Stage.CutsceneManager.ShowDialogue();
            //Stage.CutsceneManager.StartDialogue();

            // Hide the mini map and button
            Stage.Menus.ToggleMiniMapDisplay();
            Stage.Menus.MiniMapOpenButton.SetActive(false);

            Triggers.AddRange(new List<Trigger>(){
                new Trigger(() =>
                {
                    Debug.Log($"Looking for {firstHoneybee}");
                    return firstScout.ProximityCollider.NearbyEnemyShips.Contains(firstHoneybee);
                },
                () =>
                {
                    Debug.Log("Scout spotted the honeybee!");

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
                        firstScout.Squad.CommandQueueEmptyAction = () => {
                            Debug.Log($"Scout has reached out of sight position: {firstScout.GetPosition()}");
                            Stage.IsFollowingShip = false;
                            Stage.SetupCamera(); // Reset the camera position

                            // Hide the minimap and button again
                            Stage.Menus.ToggleMiniMapDisplay();
                            Stage.Menus.MiniMapOpenButton.SetActive(false);


                            Stage.CutsceneManager.StartDialogue(DialogueManager.Dialogues.Pluto_Anomaly);
                            firstScout.EndKill();
                        };

                        firstScout.CanOverrideBounds = true;
                        MoveToPoint moveToPoint = (MoveToPoint)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToPoint);
                        moveToPoint.Setup(firstScout.Squad, false, null, null, StartingPositions[firstScout.Side - 1] - new Vector2(150, 150)); // Scout starting position - 100 
                        firstScout.Squad.CommandQueue.Enqueue(moveToPoint);

                        firstScout.Squad.RunCommandQueue();


                    });

                },
                "Level 0 Looking for Honeybee Trigger"),

                new Trigger(() =>
                {
                    Debug.Log($"Waiting for user to control Scout");
                    checksForUserControl++;
                    hasBeenUserControlled = firstScout.DistanceToPoint(StartingPositions[firstScout.Side - 1]) > 3;
                    return hasBeenUserControlled || checksForUserControl >= 3; // Max of 15s / 3 checks
                },
                () =>
                {
                    if (!hasBeenUserControlled)
                    {
                        Debug.Log("Showing tooltip for controlling Scout");
                        moveScoutTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform);
                        moveScoutTooltip.SetActive(true);
                        moveScoutTooltip.transform.Find("Vertical/Message").GetComponent<TMP_Text>().text = "You can move the ship by right clicking somewhere in space.";
                        moveScoutTooltip.transform.GetChild(0).GetComponent<RectTransform>().sizeDelta = new Vector2(150, 100);
                    }
                    else
                    {
                        Debug.Log($"Waited 15 seconds and the ship has been user controlled, not showing tooltip");
                    }

                },
                "Level 0 Waiting for user to control Scout Trigger"),
                new Trigger(() =>
                {
                    Debug.Log($"Waiting to hide tooltip");
                    hasBeenUserControlled = firstScout.DistanceToPoint(StartingPositions[firstScout.Side - 1]) > 3;
                    return hasBeenUserControlled; // Max of 15s / 3 checks
                },
                () =>
                {
                    if (hasBeenUserControlled)
                    {

                        if (moveScoutTooltip != null)
                        {
                            Destroy(moveScoutTooltip);
                        }
                        Stage.Menus.WASDTooltip.SetActive(true);

                        NextTriggers.Add(new Trigger(() =>
                            {
                                Debug.Log($"Waiting to hide WASD tooltip");
                                return Vector2.Distance(Stage.Camera.transform.position, initialCameraPosition) > 10;
                            },
                            () =>
                            {
                                Stage.Menus.WASDTooltip.SetActive(false);

                            },
                            "Level 0 Waiting for user to control camera Trigger")
                        );
                    }
                },
                "Level 0 Waiting for user to control Scout to hide tooltip Trigger"),
                new Trigger(() =>
                {
                    return Stage.CutsceneManager.HitDialogueBreak;
                },
                () =>
                {
                    Debug.Log("Technician intro completed, spawning gunship");

                    // Spawn the gunship squad, take away user control, put it on cease fire
                    LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() {ConfigData.CurrentShips.GetSavedSquad(1) }, StartingPositions[ConfigData.Configuration.UserSide - 1] - new Vector2(0, 100), StartingPositions[ConfigData.Configuration.UserSide - 1] - new Vector2(0, 100));

                    firstGunship = (Gunship)State.GetHumanShips().First(); // Still the first ship since the Scout was removed
                    firstGunship.Squad.CanAcceptUserInput = false;
                    firstGunship.Squad.FinalizeUserCommand();
                    firstGunship.Squad.SetSquadCeaseFire(true);


                    firstGunship.Squad.HasCommandQueue = true;
                    firstGunship.Squad.CommandQueueEmptyAction = () => {
                        Debug.Log($"Gunship has reached center position: {firstGunship.GetPosition()}");
                        gunshipHasReachedCenterPosition = true;
                                
                        // Tom Dialogue
                        Stage.CutsceneManager.ContinueDialogue();
                    };

                    MoveToPoint moveToPoint = (MoveToPoint)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToPoint);
                    moveToPoint.Setup(firstGunship.Squad, false, null, null, StartingPositions[ConfigData.Configuration.UserSide - 1] + new Vector2(0, 40)); // Gunship center-ish position
                    firstGunship.Squad.CommandQueue.Enqueue(moveToPoint);

                    firstGunship.Squad.RunCommandQueue();

                },
                "Level 0 Waiting for dialogue break after Lt. Tom Intro"),
                new Trigger(() =>
                {
                    return gunshipHasReachedCenterPosition;
                },
                () =>
                {
                    // Gunship goes to pursue honeybee
                    Debug.Log($"Gunship will now pursue honeybee");

                    firstGunship.Squad.CommandQueueEmptyAction = () => {
                        Debug.Log($"Gunship has reached honeybee position and finished aggressive command");
                    };

                    Aggressive aggressive = (Aggressive)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Aggressive);
                    aggressive.Setup(firstGunship.Squad, false, firstHoneybee.Squad, null); // Gunship pursuing Honeybee
                    firstGunship.Squad.CommandQueue.Enqueue(aggressive);

                    firstGunship.Squad.RunCommandQueue();
                    Stage.CameraShip = firstGunship;
                    Stage.IsFollowingShip = true;

                    NextTriggers.Add(new Trigger(() =>
                        {
                            return firstGunship.Weapons.First().GetEnemyShipsWithinRange().Count > 0;
                        },
                        () =>
                        {
                            // Gunship goes to pursue honeybee
                            Debug.Log($"Gunship has honeybee within range");

                            Stage.CutsceneManager.ContinueDialogue();

                            NextTriggers.Add(new Trigger(() =>
                                {
                                    return Stage.CutsceneManager.HitDialogueBreak;
                                },
                                () =>
                                {
                                    // Show HUD and minimap  
                                    Stage.Menus.MiniMapOpenButton.SetActive(true);
                                    Stage.Menus.ToggleMiniMapDisplay();
                                    Stage.CutsceneManager.ContinueDialogue();

                                    CurrentLevelOptions.HasSquadActionBox = true;
                                    Stage.Menus.ActionBox.Setup(Stage, this, Stage.EventSystem, ConfigData.Configuration.UserSide);

                                    NextTriggers.Add(new Trigger(() =>
                                        {
                                            return Stage.CutsceneManager.HitDialogueBreak;
                                        },
                                        () =>
                                        {
                                            firstGunship.Squad.CanAcceptUserInput = true;
                                            Stage.IsFollowingShip = false;
                                            aggressive.SetFinalize("Honeybee reached by gunship, ceding to user control");

                                            controlGunshipTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform);
                                            controlGunshipTooltip.SetActive(true);
                                            TMP_Text tooltipText =  controlGunshipTooltip.transform.Find("Vertical/Message").GetComponent<TMP_Text>();
                                            RectTransform tooltipRectTransformPosition = controlGunshipTooltip.GetComponent<RectTransform>();
                                            RectTransform tooltipRectTransformSize = controlGunshipTooltip.transform.GetChild(0).GetComponent<RectTransform>();

                                            tooltipRectTransformSize.sizeDelta = new Vector2(150, 150);
                                            tooltipText.text = "You can select the ship by left clicking on it or by left clicking and dragging a selection box around it.";
                                            //controlGunshipTooltip.transform.Find("Vertical/Message").GetComponent<TMP_Text>().text = "You can select the ship by left clicking on it or by clicking and dragging a selection box around it. <br><br> Once you do, you'll want to familiarize yourself with the controls to your bottom left. They aren't usually necessary but they are helpful. <br><br> When you're ready to engage the Honeybee, click \"Attack on Sight\" to disable the Cease Fire. Once the Gunship is within range it will automatically fire upon the Honeybee.";

                                            NextTriggers.Add(new Trigger(() =>
                                                {
                                                    return firstGunship.Squad.IsSelected;
                                                },
                                                () =>
                                                {
                                                    tooltipRectTransformSize.sizeDelta = new Vector2(150, 150);
                                                    tooltipRectTransformPosition.localPosition = new Vector2(-500, 0);
                                                    tooltipText.text = "You'll want to familiarize yourself with the controls to your bottom left. They aren't usually required but they are helpful.";

                                                    NextTriggers.Add(new Trigger(() =>
                                                        {
                                                            passes++;
                                                            return passes > 2;
                                                        },
                                                        () =>
                                                        {
                                                            attackOnSightTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform);
                                                            attackOnSightTooltip.SetActive(true);
                                                            TMP_Text tooltipText =  attackOnSightTooltip.transform.Find("Vertical/Message").GetComponent<TMP_Text>();
                                                            attackOnSightTooltip.transform.GetChild(0).GetComponent<RectTransform>().sizeDelta = new Vector2(150, 200);
                                                            attackOnSightTooltip.GetComponent<RectTransform>().localPosition = new Vector2(0, -150);
                                                            tooltipText.text = "When you're ready to engage the Honeybee, click \"Attack on Sight\" to disable the Cease Fire. Once the Gunship is within range it will automatically fire upon the Honeybee.";

                                                            NextTriggers.Add(new Trigger(() =>
                                                                {
                                                                    return firstGunship.Squad.CeaseFire == false;
                                                                },
                                                                () =>
                                                                {
                                                                    Destroy(attackOnSightTooltip);
                                                                    Destroy(controlGunshipTooltip);

                                                                },
                                                                "Level 0 Removing Gunship Tooltips")
                                                            );

                                                        },
                                                        "Level 0 Showing Attack on Sight tooltip prompt")
                                                    );



                                                },
                                                "Level 0 Showing squad controls tooltip when squad is selected")
                                            );


                                            // Create bee reinforcement squads. One Squad of 3 hornets, one squad of 2 wasps
                                            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() {ConfigData.CurrentShips.GetSavedSquad(3), ConfigData.CurrentShips.GetSavedSquad(4), ConfigData.CurrentShips.GetSavedSquad(5), ConfigData.CurrentShips.GetSavedSquad(6), ConfigData.CurrentShips.GetSavedSquad(7)}, StartingPositions[ConfigData.Configuration.AISide - 1] + new Vector2(0, 50), StartingPositions[ConfigData.Configuration.AISide - 1] + new Vector2(0, 50));


                                            NextTriggers.Add(new Trigger(() =>
                                                {
                                                    return firstHoneybee.IsDead;
                                                },
                                                () =>
                                                {
                                                    Debug.Log("Honeybee has been defeated");
                                                    Stage.CutsceneManager.ContinueDialogue();
                                                    State.GetSquadsBySide(ConfigData.Configuration.AISide).ForEach((squad) =>
                                                    {
                                                        squad.SetSquadCeaseFire(true); // Cease fire for all bee squads

                                                        // Move to point where gun ship is

                                                        squad.HasCommandQueue = true;
                                                        squad.CommandQueueEmptyAction = () => {
                                                            Debug.Log($"{squad} has finished command against gunship");

                                                            MoveToPoint moveToPoint = (MoveToPoint)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToPoint);
                                                            moveToPoint.Setup(squad, false, null, null, firstGunship.GetPosition() - new Vector2(30, 30)); // Go near gunship
                                                            squad.CommandQueue.Enqueue(moveToPoint);

                                                            squad.RunCommandQueue();

                                                        };

                                                        squad.RunCommandQueue();


                                                    });

                                                    NextTriggers.Add(new Trigger(() =>
                                                        {
                                                            return firstGunship.Weapons.First().GetEnemyShipsWithinRange().Count > 0;
                                                        },
                                                        () =>
                                                        {
                                                            Stage.CutsceneManager.ContinueDialogue(); // Gunship Sees bees

                                                            flydownTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform);
                                                            flydownTooltip.SetActive(true);
                                                            flydownTooltip.transform.Find("Vertical/Message").GetComponent<TMP_Text>().text = "Fly down to safety!";
                                                                    
                                                            // Wait a few seconds and then the bees stop ceasefire, and pursue the gunship with aggressive command
                                                            _beesPursuitTimer.Reuse(5, () =>
                                                                {
                                                                    State.GetSquadsBySide(ConfigData.Configuration.AISide).ForEach((squad) =>
                                                                    {
                                                                        squad.SetSquadCeaseFire(false); // Turn on fire for all bee squads

                                                                        squad.CommandQueueEmptyAction = () => { };

                                                                        squad.GetCommand().SetFinalize("Time to attack gunship"); // End whatever they were doing before
                                                                        squad.CommandQueue.Clear(); // Clear the command queue
                                                                        Aggressive aggressive = (Aggressive)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Aggressive);
                                                                        aggressive.Setup(squad, false, firstGunship.Squad, null); // Bees pursuing Gunship
                                                                        squad.CommandQueue.Enqueue(aggressive);

                                                                        squad.RunCommandQueue();
                                                                    });

                                                                    Destroy(flydownTooltip);
                                                                }
                                                            );
                                                            AddTimer(_beesPursuitTimer);


                                                            // Green exit zone at bottom of the screen lights up
                                                            GameObject exitBox = Instantiate(Stage.Prefabs.ExitZonePrefab, Map.transform);
                                                            exitZone = exitBox.GetComponent<ExitZone>();

                                                            exitZone.OnShipEnter = (ship) =>
                                                            {
                                                                if (ship == firstGunship)
                                                                {
                                                                    firstGunship.FogOfWarVision.Kill(0, true); // Remove fog of war vision immediately
                                                                    firstGunship.EndKill();
                                                                }
                                                            };


                                                            NextTriggers.Add(new Trigger(() =>
                                                                {
                                                                    return firstGunship.IsDead; // Once gunship dies
                                                                },
                                                                () =>
                                                                {

                                                                    NextTriggers.Add(new Trigger(() =>
                                                                        {
                                                                            return Stage.CutsceneManager.PlutoLines_Anomaly_Completed;
                                                                        },
                                                                        () =>
                                                                        {
                                                                            Level0Ending();
                                                                        },
                                                                        "Level 0 Level completing after the end of dialogue")
                                                                    );
                                                                    Stage.CutsceneManager.ContinueDialogue(); // Play the rest of the dialogue

                                                                },
                                                                "Level 0 Hiding map after player dies or leaves")
                                                            );

                                                        },
                                                        "Level 0 Showing dialogue after Bees approach")
                                                    );

                                                },
                                                "Level 0 Showing Dialogue after defeating Honeybee")
                                            );

                                        },
                                        "Level 0 Showing prompt for user controls")
                                    );

                                },
                                "Level 0 Displaying HUD and Minimap")
                            );

                        },
                        "Level 0 Triggering dialogue when Honeybee is reached by Gunship")
                    );

                },
                "Level 0 Pursuing Honeybee"),

            });
        }

        public void Level0Ending()
        {
            Debug.Log("Level complete!");
            Debug.Log("Updated Campaign State");

            // Add new human ships to the game
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 1); // 1 Gunship
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 3); // 3 Frigates
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Dreadnought, 2); // 2 Dreadnoughts

            // Add new ships to the human's available ships
            SavedSquad gunshipSquad = ConfigData.CurrentShips.GetSavedSquad(1);
            FleetShip gunship = CurrentShips.GetAvailableShips().Where((s) => s.Type == ShipTypes.Gunship).First();
            gunshipSquad.AddShipToSquad(new SquadShip(gunship.Id, gunship.Type, Vector2.zero, gunshipSquad));

            // Starting Dreadnought squad #8
            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Dreadnought, 2);

            // Starting Frigate squad #9
            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Frigate, 3);


            // Add Honeybee, Frigate, and Dreadnought to the codex and visibility
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Honeybee);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Frigate);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Dreadnought);

            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Hornet);
            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Wasp);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Frigate);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Dreadnought);

            ConfigData.UserProgressData.SetShipTypes();

            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Debug.Log("Did Standard level complete, showing dialogue");
            Stage.Menus.ShowLevelContinueDialogue();

        }
    }
}