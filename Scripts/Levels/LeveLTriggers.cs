using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.UI_Components;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static Assets.Scripts.ConfigData;
using static UnityEngine.GraphicsBuffer;

namespace Assets.Scripts.Levels
{
    public partial class Level : MonoBehaviour
    {
        private ScaledTimer _dialogueTimer = new ScaledTimer();
        private ScaledTimer _egg = new ScaledTimer();
        private ScaledTimer _fishTank = new ScaledTimer();

        /// <summary>
        /// This represents an amount of value that the player has accomplished for a given level like rescuing personnel that in turn translates to reinforcements or some other bonus
        /// </summary>
        private int _questPoints;
        //private bool _someShipsHaveRetreated;
        private bool _lastShipRetreated, _hasSeenCarrierIntroIfNeeded;
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
                case 1:
                    Level1Triggers();
                    break;
                case 2:
                    Level2Triggers();
                    break;
                case 3:
                    Level3Triggers();
                    break;
                case 4:
                    Level4Triggers();
                    break;
                case 5:
                    Level5Triggers();
                    break;
                case 6:
                    Level6Triggers();
                    break;
                case 7:
                    Level7Triggers();
                    break;
                case 8:
                    Level8Triggers();
                    break;
            }
        }

        public void Level0Triggers()
        {
            FishTankTrigger();
            Debug.Log("Setting triggers for level 0");
            // Setup specifics for the level
            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Scout, 1),
            }, StartingPositions[ConfigData.Configuration.UserSide - 1], Vector2.zero, false);
            Scout firstScout = (Scout)State.GetHumanShips().First();

            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 1),
            }, StartingPositions[ConfigData.Configuration.AISide - 1], Vector2.zero, false);
            Honeybee firstHoneybee = (Honeybee)State.GetBeeShips().First();

            Gunship firstGunship = null;
            int passes = 0;
            bool hasBeenUserControlled = false;
            bool gunshipHasReachedCenterPosition = false;
            HasContinuousTriggers = true;
            ScaledTimer _beesPursuitTimer = new ScaledTimer();
            ScaledTimer _endLevelTimer = new ScaledTimer();
            Zone exitZone = null;
            Tooltip moveScoutTooltip = null;


            // Setup proximity collider for the scout
            firstScout.ProximityCollider = Instantiate(Stage.Prefabs.HumanProximityColliderPrefab, Vector3.zero, Quaternion.identity, firstScout.transform).GetComponent<ProximityCollider>();
            firstScout.HasProximityCollider = true;

            int originalSight = firstScout.Sight;
            firstScout.Sight = 60; // reduce sight for proximity collider
            firstScout.ProximityCollider.Create(firstScout);
            firstScout.Sight = originalSight; // Restore original sight value
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

            Stage.CutsceneManager.Setup(() =>
            {
                Level0Ending(firstGunship.Squad.SavedSquad);
            });
            Stage.CutsceneManager.StartCutScene();
            Stage.EnablePlayerControl();
            State.SelectSquads(new List<Squad>());

            //Stage.CutsceneManager.ShowDialogue();
            //Stage.CutsceneManager.StartDialogue();

            // Hide the mini map and button
            Stage.Menus.ToggleMiniMapDisplay();
            Stage.Menus.MiniMapOpenButton.SetActive(false);

            Stage.Menus.SetMissionStatus("Scout and explore around Pluto");

            float startTime = Time.realtimeSinceStartup;

            Triggers.AddRange(new List<Trigger>(){
                new Trigger(() =>
                {
                    //Debug.Log($"Looking for {firstHoneybee}");
                    return firstScout.ProximityCollider.NearbyEnemyShips.Contains(firstHoneybee);
                },
                () =>
                {
                    Debug.Log("Scout spotted the honeybee!");
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
                        firstScout.Squad.CommandQueueEmptyAction = () => {
                            Debug.Log($"Scout has reached out of sight position: {firstScout.GetPosition()}");
                            Stage.IsFollowingShip = false;
                            Stage.SetupCamera(); // Reset the camera position

                            // Hide the minimap and button again
                            Stage.Menus.ToggleMiniMapDisplay();
                            Stage.Menus.MiniMapOpenButton.SetActive(false);


                            Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Anomaly.GetRange(0, 11));
                            firstScout.EndKill();
                        };

                        firstScout.CanOverrideBounds = true;
                        MoveToPoint moveToPoint = (MoveToPoint)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToPoint);
                        moveToPoint.Setup(firstScout.Squad, false, null, null, StartingPositions[ConfigData.Configuration.UserSide - 1] - new Vector2(0, 150)); // Scout starting position - 100 
                        firstScout.Squad.CommandQueue.Enqueue(moveToPoint);

                        firstScout.Squad.RunCommandQueue();


                    });

                },
                "Level 0 Looking for Honeybee Trigger"),

                new Trigger(() =>
                {
                    //Debug.Log($"Waiting for user to control Scout");
                    hasBeenUserControlled = firstScout.DistanceToPoint(StartingPositions[firstScout.Side - 1]) > 3;
                    return hasBeenUserControlled || Time.realtimeSinceStartup - startTime > 10; // Max of 10s 
                },
                () =>
                {
                    if (!hasBeenUserControlled)
                    {
                        Debug.Log("Showing tooltip for controlling Scout");

                        Tooltip moveScoutTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                        moveScoutTooltip.Show("You can select the ship by left clicking on it. You can then move the ship by right clicking somewhere in space.", true);
                        moveScoutTooltip.Place(Vector2.zero, new Vector2(150, 150));
                    }
                    else
                    {
                        Debug.Log($"Waited 15 seconds and the ship has been user controlled, not showing tooltip");
                    }

                },
                "Level 0 Waiting for user to control Scout Trigger"),
                new Trigger(() =>
                {
                    //Debug.Log($"Waiting to hide tooltip");
                    hasBeenUserControlled = firstScout.DistanceToPoint(StartingPositions[firstScout.Side - 1]) > 3;
                    return hasBeenUserControlled; 
                },
                () =>
                {
                    if (hasBeenUserControlled)
                    {

                        if (moveScoutTooltip != null)
                        {
                            Destroy(moveScoutTooltip.gameObject);
                        }
                        Stage.Menus.WASDTooltip.SetActive(true);
                        Vector2 initialCameraPosition = Stage.Camera.transform.position;


                        NextTriggers.Add(new Trigger(() =>
                            {
                                //Debug.Log($"Waiting to hide WASD tooltip");
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
                     AddReinforcementSquads(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Gunship, 1),
                    }, StartingPositions[ConfigData.Configuration.UserSide - 1] - new Vector2(0, 100), Vector2.zero);

                    firstGunship = (Gunship)State.GetHumanShips().First(); // Still the first ship since the Scout was removed
                    firstGunship.Squad.CanAcceptUserInput = false;
                    firstGunship.Squad.FinalizeUserCommand();
                    firstGunship.Squad.SetSquadCeaseFire(true);
                    Stage.CameraShip = firstGunship;
                    Stage.IsFollowingShip = true;



                    firstGunship.Squad.HasCommandQueue = true;
                    firstGunship.Squad.CommandQueueEmptyAction = () => {
                        Debug.Log($"Gunship has reached center position: {firstGunship.GetPosition()}");
                        gunshipHasReachedCenterPosition = true;
                                
                        // Tom Dialogue
                       Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.PlutoLines_Anomaly[11]);
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

                    NextTriggers.Add(new Trigger(() =>
                        {
                            return firstGunship.Weapons.First().GetEnemyShipsWithinRange().Count > 0;
                        },
                        () =>
                        {
                            // Gunship goes to pursue honeybee
                            Debug.Log($"Gunship has honeybee within range");

                            Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Anomaly.GetRange(12, 9));

                            NextTriggers.Add(new Trigger(() =>
                                {
                                    return Stage.CutsceneManager.HitDialogueBreak;
                                },
                                () =>
                                {
                                    // Show HUD and minimap  
                                    Stage.Menus.MiniMapOpenButton.SetActive(true);
                                    Stage.Menus.ToggleMiniMapDisplay();
                                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Anomaly.GetRange(21, 2));

                                    CurrentLevelOptions.HasSquadActionBox = true;
                                    Stage.Menus.ActionBox.Setup(Stage, this, Stage.EventSystem, ConfigData.Configuration.UserSide);

                                    NextTriggers.Add(new Trigger(() =>
                                        {
                                            return Stage.CutsceneManager.HitDialogueBreak;
                                        },
                                        () =>
                                        {
                                            Stage.Menus.MissionStatus.SetActive(true);
                                            Stage.Menus.SetMissionStatus("Find and destroy the enemy ship");
                                            firstGunship.Squad.CanAcceptUserInput = true;
                                            firstGunship.Squad.HasCommandQueue = false;
                                            Stage.IsFollowingShip = false;
                                            aggressive.SetFinalize("Honeybee reached by gunship, ceding to user control");


                                            Tooltip controlGunshipTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                                            controlGunshipTooltip.Show("You can select the ship by left clicking on it or by left clicking and dragging a selection box around it.", true);
                                            controlGunshipTooltip.Place(Vector2.zero, new Vector2(150, 150));


                                            NextTriggers.Add(new Trigger(() =>
                                                {
                                                    return firstGunship.Squad.IsSelected;
                                                },
                                                () =>
                                                {

                                                    controlGunshipTooltip.Show("You'll want to familiarize yourself with the controls to your bottom left. They aren't usually required, but they are helpful.", true);
                                                    controlGunshipTooltip.Place(new Vector2(-500, 0), new Vector2(150, 150));


                                                    NextTriggers.Add(new Trigger(() =>
                                                        {
                                                            passes++;
                                                            return passes > 2;
                                                        },
                                                        () =>
                                                        {
                                                            Tooltip attackOnSightTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                                                            attackOnSightTooltip.Show("When you're ready to engage the Honeybee, click \"Attack on Sight\" (the red exclamation point) to disable the Cease Fire. Once the Gunship is within range it will automatically fire upon the Honeybee. To chase after an enemy ship, right click on it.", true);
                                                            attackOnSightTooltip.Place(new Vector2(0, -50), new Vector2(150, 300));


                                                            //NextTriggers.Add(new Trigger(() =>
                                                            //    {
                                                            //        return firstGunship.Squad.CeaseFire == false;
                                                            //    },
                                                            //    () =>
                                                            //    {
                                                            //        Destroy(attackOnSightTooltip);
                                                            //        Destroy(controlGunshipTooltip);

                                                            //    },
                                                            //    "Level 0 Removing Gunship Tooltips")
                                                            //);

                                                        },
                                                        "Level 0 Showing Attack on Sight tooltip prompt")
                                                    );



                                                },
                                                "Level 0 Showing squad controls tooltip when squad is selected")
                                            );


                                            // Create bee reinforcement squads. One Squad of 3 hornets, one squad of 2 wasps
                                            AddReinforcementSquads(new List<SavedSquad>() {
                                                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 3),
                                                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 3),
                                                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 3),
                                                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2),
                                                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2),


                                            }, StartingPositions[ConfigData.Configuration.AISide - 1] + new Vector2(0, 50), StartingPositions[ConfigData.Configuration.AISide - 1]);


                                            NextTriggers.Add(new Trigger(() =>
                                                {
                                                    return firstHoneybee.IsDead;
                                                },
                                                () =>
                                                {
                                                    Debug.Log("Honeybee has been defeated");
                                                    Stage.Menus.TogglePausePanel();
                                                    //Stage.Menus.SetMissionStatus("Keep a look out for other ships");
                                                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Anomaly.GetRange(23, 2));

                                                    List<Squad> beeSquads = State.GetSquadsBySide(ConfigData.Configuration.AISide);
                                                    for (int i = 1; i < beeSquads.Count; i++)
                                                    {

                                                        beeSquads[i].SetStartingPosition(beeSquads[i].GetPosition() + new Vector2(((i % 2 == 0 ? 1 : -1) * 25 * i), 0));
                                                    }

                                                    //State.GetSquadsBySide(ConfigData.Configuration.AISide).ForEach((squad) =>
                                                    //{
                                                    //    squad.SetSquadCeaseFire(true); // Cease fire for all bee squads

                                                    //    // Move to point where gun ship is

                                                    //    squad.HasCommandQueue = true;
                                                    //    squad.CommandQueueEmptyAction = () => {
                                                    //        //Debug.Log($"{squad} has finished command against gunship");

                                                    //        MoveToPoint moveToPoint = (MoveToPoint)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToPoint);
                                                    //        moveToPoint.Setup(squad, false, null, null, firstGunship.GetPosition() - new Vector2(30, 30)); // Go near gunship
                                                    //        squad.CommandQueue.Enqueue(moveToPoint);

                                                    //        squad.RunCommandQueue();

                                                    //    };

                                                    //    squad.RunCommandQueue();


                                                    //});

                                                    NextTriggers.Add(new Trigger(() =>
                                                        {
                                                            return Stage.CutsceneManager.HitDialogueBreak;
                                                        },
                                                        () =>
                                                        {
                                                            Stage.Menus.TogglePausePanel();
                                                            Stage.Menus.ToggleFogOfWar();
                                                            Stage.Menus.SetMissionStatus("Fly down to safety!");
                                                            Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Anomaly.GetRange(25, 2)); // Gunship Sees bees

                                                            //Tooltip flydownTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                                                            //flydownTooltip.Show("Fly down to safety!", true);

                                                            Stage.Camera.transform.position = new Vector3(StartingPositions[ConfigData.Configuration.AISide - 1].x, StartingPositions[ConfigData.Configuration.AISide - 1].y, Stage.Camera.transform.position.z);
                                                            Stage.InputManager.MaintainScrollBoundary();

                                                            Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Anomaly.GetRange(27, 5), true);

                                                            _endLevelTimer.Reuse(2, () =>
                                                            {
                                                                CloseLevel();
                                                            });
                                                            AddTimer(_endLevelTimer);   

                                                            // Wait a few seconds and then the bees stop ceasefire, and pursue the gunship with aggressive command
                                                            //_beesPursuitTimer.Reuse(10, () =>
                                                            //    {
                                                            //        State.GetSquadsBySide(ConfigData.Configuration.AISide).ForEach((squad) =>
                                                            //        {
                                                            //            squad.SetSquadCeaseFire(false); // Turn on fire for all bee squads

                                                            //            squad.CommandQueueEmptyAction = () => { };

                                                            //            squad.GetCommand().SetFinalize("Time to attack gunship"); // End whatever they were doing before
                                                            //            squad.CommandQueue.Clear(); // Clear the command queue
                                                            //            Aggressive aggressive = (Aggressive)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Aggressive);
                                                            //            aggressive.Setup(squad, false, firstGunship.Squad, null); // Bees pursuing Gunship
                                                            //            squad.CommandQueue.Enqueue(aggressive);

                                                            //            squad.RunCommandQueue();
                                                            //        });

                                                            //        Destroy(flydownTooltip);
                                                            //    }
                                                            //);
                                                            //AddTimer(_beesPursuitTimer);


                                                            // Green exit zone at bottom of the screen lights up
                                                            //GameObject exitBox = Instantiate(Stage.Prefabs.ExitZonePrefab, Map.transform);
                                                            //exitZone = exitBox.GetComponent<Zone>();

                                                            //exitZone.OnShipEnter = (ship) =>
                                                            //{
                                                            //    if (ship == firstGunship)
                                                            //    {
                                                            //        firstGunship.FogOfWarVision.Kill(0, true); // Remove fog of war vision immediately
                                                            //        firstGunship.EndKill();
                                                            //    }
                                                            //};


                                                            //NextTriggers.Add(new Trigger(() =>
                                                            //    {
                                                            //        return firstGunship.IsDead; // Once gunship dies or retreats
                                                            //    },
                                                            //    () =>
                                                            //    {
                                                            //        CloseLevel();
                                                            //        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Anomaly.GetRange(27, 5), true); // Play the rest of the dialoguew

                                                            //    },
                                                            //    "Level 0 Hiding map after player dies or leaves")
                                                            //);

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
        public void Level1Triggers()
        {
            Stage.Menus.MissionStatus.SetActive(false);
            // Hide the surrender button
            Stage.SurrenderButton.SetActive(false);
            FishTankTrigger();
            Debug.Log("Setting triggers for level 1");
            HasContinuousTriggers = true;
            Tooltip basicTooltip = null;
            Tooltip selectMultiple = null;
            Tooltip rangeTooltip = null;
            GameObject highlightTooltipObject = null;


            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Scout, 1),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Gunship, 2, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Dreadnought, 2),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Frigate, 4),
            }, StartingPositions[ConfigData.Configuration.UserSide - 1], Vector2.zero, false);

            Squad scoutSquad = State.GetSquadByNumber(ConfigData.Configuration.HumanSide, 1); // Scout squad that starts in the level
            Squad gunshipSquad = State.GetSquadByNumber(ConfigData.Configuration.HumanSide, 2);
            Squad dreadnoughtSquad = State.GetSquadByNumber(ConfigData.Configuration.HumanSide, 3);
            Squad frigateSquad = State.GetSquadByNumber(ConfigData.Configuration.HumanSide, 4); // Frigate squad that starts in the level
            RectTransform rectTransform = null;


            // Deselect all squads
            State.SelectSquads(new List<Squad>());
            frigateSquad.CanAcceptUserInput = false;
            gunshipSquad.CanAcceptUserInput = false;
            dreadnoughtSquad.CanAcceptUserInput = false;

            // Prevent the Hivemind from giving commands
            Stage.ActivateHiveMind = false;

            // Start the dialogue
            Stage.CutsceneManager.Setup(() =>
            {
                Level1Ending();
            });
            Stage.Menus.TogglePausePanel();


            _dialogueTimer.Reuse(1.5f, () =>
            {
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.PlutoLines_Reinforcements[1]);
            });
            AddTimer(_dialogueTimer);

            Triggers.Add(new Trigger(() =>
                {
                    return Stage.CutsceneManager.HitDialogueBreak;
                },
                () =>
                {
                    Stage.Menus.TogglePausePanel();
                    Stage.Menus.MissionStatus.SetActive(true);
                    Stage.Menus.SetMissionStatus("Use the Scout");
                    Stage.EnablePlayerControl();
                    basicTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                    
                    basicTooltip.Show("Select the Scout squad with the left mouse button.", true);
                    basicTooltip.Place(new Vector2(200, 0), new Vector2(150, 100));

                    //TMP_Text tooltipText = basicTooltipObject.transform.Find("Vertical/Message").GetComponent<TMP_Text>();
                    //RectTransform tooltipRectTransformPosition = basicTooltipObject.GetComponent<RectTransform>();
                    //RectTransform tooltipRectTransformSize = basicTooltipObject.transform.GetChild(0).GetComponent<RectTransform>();

                    //tooltipText.text = "Select the Scout squad with the left mouse button.";
                    //tooltipRectTransformPosition.localPosition = new Vector2(200, 0);
                    //tooltipRectTransformSize.sizeDelta = new Vector2(150, 50);

                    highlightTooltipObject = Instantiate(Stage.Menus.HighlightTooltipPrefab, Map.transform);
                    highlightTooltipObject.SetActive(true);
                    highlightTooltipObject.transform.position = scoutSquad.GetPosition();
                    highlightTooltipObject.transform.localScale = new Vector2(scoutSquad.GetWidth() + 2, scoutSquad.GetHeight() + 2);

                    NextTriggers.Add(new Trigger(() =>
                        {
                            return scoutSquad.IsSelected;
                        },
                        () =>
                        {
                            highlightTooltipObject.SetActive(false);
                            Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Reinforcements.GetRange(2, 4));
                            Stage.Menus.TogglePausePanel();

                            NextTriggers.Add(new Trigger(() =>
                                {
                                    return Stage.CutsceneManager.HitDialogueBreak && scoutSquad.IsSelected;
                                },
                                () =>
                                {
                                    Stage.Menus.TogglePausePanel();
                                    basicTooltip.Show("Here are different settings for your ship.You can determine your squad’s flight pattern and shooting strategies here. Take some time to familiarize yourself with these options.", true);
                                    basicTooltip.Place(new Vector2(-175, -150), new Vector2(150, 225));


                                    GameObject pointerA = Instantiate(Stage.Menus.PointerArrow, Stage.Menus.UIOverlay.transform);
                                    rectTransform = pointerA.GetComponent<RectTransform>();
                                    rectTransform.localPosition = new Vector2(-447, -238);
                                    rectTransform.eulerAngles = new Vector3(0, 0, 170);
                                    pointerA.SetActive(true);

                                    GameObject pointerB = Instantiate(Stage.Menus.PointerArrow, Stage.Menus.UIOverlay.transform);
                                    rectTransform = pointerB.GetComponent<RectTransform>();
                                    rectTransform.localPosition = new Vector2(-330, -340);
                                    rectTransform.eulerAngles = new Vector3(0, 0, 90);
                                    rectTransform.localScale = new Vector2(0.25f, 0.5f);
                                    pointerB.SetActive(true);

                                    NextTriggers.Add(new Trigger(() =>
                                        {
                                            return !basicTooltip.gameObject.activeSelf;
                                        },
                                        () =>
                                        {
                                            Destroy(pointerA);
                                            Destroy(pointerB);

                                            Stage.Menus.SetMissionStatus("Try out the different squads");

                                            GameObject squadNumberHighlight = Instantiate(Stage.Menus.UIHighlightTooltipPrefab, Stage.Menus.UIOverlay.transform);
                                            squadNumberHighlight.SetActive(true);
                                            squadNumberHighlight.transform.localPosition = new Vector2(-610, 370);
                                            squadNumberHighlight.transform.localScale = new Vector2(150, 30);
                                            squadNumberHighlight.transform.SetAsFirstSibling();

                                            basicTooltip.Show("You can also select squads with the number hotkeys on your keyboard. These are displayed at the top of the screen.", true);
                                            basicTooltip.Place(new Vector2(-550, 300), new Vector2(150, 150));

                                            selectMultiple = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                                            selectMultiple.Show("If you need to select multiple squads, click and drag the mouse over the squads.", true);
                                            selectMultiple.Place(new Vector2(-200, 0) ,new Vector2(150, 100));

                                            //GameObject multiSelectMessage = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform);
                                            //multiSelectMessage.SetActive(true);
                                            //multiSelectMessage.transform.Find("Vertical/Message").GetComponent<TMP_Text>().text = "If you need to select multiple squads, click and drag the mouse over the squads.";
                                            //multiSelectMessage.transform.GetChild(0).GetComponent<RectTransform>().sizeDelta = new Vector2(150, 100);
                                            //multiSelectMessage.transform.GetComponent<RectTransform>().localPosition = new Vector2(-200, 0);


                                            rangeTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                                            rangeTooltip.Show("Your ships with weapons will automatically shoot at any enemies in range. You can view your selected ships’ range at any time by holding <b>R</b>.You can also manually fire towards your cursor with any selected ships by pressing <b>F</b>", true);
                                            rangeTooltip.Place(new Vector2(200, 0), new Vector2(150, 280));

                                            //GameObject rangeMessage = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform);
                                            //rangeMessage.SetActive(true);
                                            //rangeMessage.transform.Find("Vertical/Message").GetComponent<TMP_Text>().text = "Your ships with weapons will automatically shoot at any enemies in range. You can view your selected ships’ range at any time by holding <b>R</b>.You can also manually fire towards your cursor with any selected ships by pressing <b>F</b>";
                                            //rangeMessage.transform.GetChild(0).GetComponent<RectTransform>().sizeDelta = new Vector2(150, 250);
                                            //rangeMessage.transform.GetComponent<RectTransform>().localPosition = new Vector2(200, 0);

                                            frigateSquad.CanAcceptUserInput = true;
                                            gunshipSquad.CanAcceptUserInput = true;
                                            dreadnoughtSquad.CanAcceptUserInput = true;

                                            float startTime = Time.realtimeSinceStartup;

                                            NextTriggers.Add(new Trigger(() =>
                                                {
                                                    return (frigateSquad.IsSelected || gunshipSquad.IsSelected || dreadnoughtSquad.IsSelected) && Time.realtimeSinceStartup - startTime > 10;
                                                },
                                                () =>
                                                {
                                                    basicTooltip.Hide();
                                                    selectMultiple.Hide();
                                                    rangeTooltip.Hide();

                                                    //Destroy(squadNumberHighlight);
                                                    //Destroy(multiSelectMessage);
                                                    //Destroy(rangeMessage);

                                                    Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.PlutoLines_Reinforcements[6]);

                                                    NextTriggers.Add(new Trigger(() =>
                                                        {
                                                            return Stage.CutsceneManager.HitDialogueBreak;
                                                        },
                                                        () =>
                                                        {
                                                            NextTriggers.Add(new Trigger(() =>
                                                                {
                                                                    return gunshipSquad.IsSelected && !scoutSquad.IsSelected && !frigateSquad.IsSelected && !dreadnoughtSquad.IsSelected;
                                                                },
                                                                () =>
                                                                {
                                                                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Reinforcements.GetRange(7, 2));
                                                                },
                                                                "Level 1 Showing Marco Gunship Dialogue")
                                                            );
                                                            NextTriggers.Add(new Trigger(() =>
                                                                {
                                                                    return dreadnoughtSquad.IsSelected && !scoutSquad.IsSelected && !gunshipSquad.IsSelected && !frigateSquad.IsSelected;
                                                                },
                                                                () =>
                                                                {
                                                                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Reinforcements.GetRange(9, 2));
                                                                },
                                                                "Level 1 Showing Yoshiko Dreadnought Dialogue")
                                                            );
                                                            NextTriggers.Add(new Trigger(() =>
                                                                {
                                                                    return frigateSquad.IsSelected && !scoutSquad.IsSelected && !gunshipSquad.IsSelected && !dreadnoughtSquad.IsSelected;
                                                                },
                                                                () =>
                                                                {
                                                                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Reinforcements.GetRange(11, 2));
                                                                },
                                                                "Level 1 Showing Joey Frigate Dialogue")
                                                            );

                                                            Stage.Menus.SetMissionStatus("Find and destroy the enemy ships!");

                                                            // Create bee starting squads. One squad of 1 honeybee, one Squads of 3 hornets, one squad of 2 wasps
                                                            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() {
                                                                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 1),
                                                                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 3, true),
                                                                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2, true),
                                                            }, new Vector2(0, 316), StartingPositions[ConfigData.Configuration.AISide - 1], true);

                                                            Stage.ActivateHiveMind = true;
                                                            SetupHivemind();

                                                            float startTime = Time.time;
                                                            NextTriggers.Add(new Trigger(() =>
                                                                {
                                                                    return Time.time - startTime >= 60f && !State.IsSideKilled(ConfigData.Configuration.AISide);
                                                                },
                                                                () =>
                                                                {
                                                                    Debug.Log($"Spawning Bee reinforcements");

                                                                    // Create bee reinforcement squads. Two Squads of 3 hornets, one squad of 2 wasps
                                                                    AddReinforcementSquads(new List<SavedSquad>() {
                                                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 3, true),
                                                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 3, true),
                                                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2, true),
                                                                    }, new Vector2(0, 316), new Vector2(0, 225));

                                                                    AddReinforcementsToHivemindCommandQueue();

                                                                    
                                                                },
                                                                "Level 1 Showing select scout squad tooltip")
                                                            );
                                                            NextTriggers.Add(new Trigger(() =>
                                                                {
                                                                    return State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide);
                                                                },
                                                                () =>
                                                                {
                                                                    CloseLevel();
                                                                    if (State.IsSideKilled(ConfigData.Configuration.AISide)) // Player won
                                                                    {
                                                                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Reinforcements.GetRange(13, 3), true);

                                                                        
                                                                    }
                                                                    else
                                                                    {
                                                                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Reinforcements.GetRange(14, 2), true);
                                                                    }
                                                                },
                                                                "Level 1 Showing select scout squad tooltip")
                                                            );
                                                        },
                                                        "Level 1 Starting full level action")
                                                    );
                                                },
                                                "Level 1 Showing finalizing tutorial")
                                            );
                                        },
                                        "Level 1 Showing select scout squad tooltip")
                                    );
                                },
                                "Level 1 Show HUD controls")
                            );
                        },
                        "Level 1 Showing Oyiva dialogue")
                    );
                },
                "Level 1 Showing select scout squad tooltip")
            );


        }
        public void Level2Triggers()
        {
            FishTankTrigger();
            Debug.Log("Setting triggers for level 2");
            Stage.EnablePlayerControl();
            HasContinuousTriggers = true;

            int personnelLost = 0;
            int personnelEvacuated = 0;

            ScaledTimer clock = new ScaledTimer();


            // Every time the trigger is checked, 1 person is evacuated. If 15 people are lost then the level ends. If 15 people aren't lost then the level ends after 5 minutes and roughly 60 people are evacuated. The "ship" will have a ton of health and no weapons but in theory should still be physical so that projectiles can hit it and explode and when it loses health, personnel are lost. It should have sufficient TSV so that it's a very valuable target for the Bees even if they only do a tiny bit of damage relative to its significant health.

            // Prevent the Hivemind from giving commands
            Stage.ActivateHiveMind = false;

            // Move the button for the game speed over
            Stage.Menus.GameSpeedButton.GetComponent<RectTransform>().anchoredPosition = new Vector2(-290, -15);

            // Start the dialogue
            Stage.CutsceneManager.Setup(() =>
            {
                Level2Ending();
            });
            Stage.Menus.TogglePausePanel();
            _dialogueTimer.Reuse(1.5f, () =>
            {
                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(1, 4));
            });
            AddTimer(_dialogueTimer);

            Stage.Menus.SetMissionStatus("Survive and defend Pluto!");

            NextTriggers.Add(new Trigger(() =>
                {
                    return Stage.CutsceneManager.HitDialogueBreak;
                },
                () =>
                {
                    GameObject plutoCircle = Instantiate(Stage.Menus.PlutoCircle, Map.transform);
                    plutoCircle.SetActive(true);
                    List<DialogueLine> plutoLines = Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(5, 2);
                    HashSet<ShipTypes> shipTypes = State.GetHumanShipTypes();
                    if (shipTypes.Contains(ConfigData.ShipTypes.Dreadnought))
                    {
                        plutoLines.Add(Stage.CutsceneManager.PlutoLines_BluerPastures[7]);
                    }
                    if (shipTypes.Contains(ConfigData.ShipTypes.Gunship))
                    {
                        plutoLines.Add(Stage.CutsceneManager.PlutoLines_BluerPastures[8]);
                    }
                    if (shipTypes.Contains(ConfigData.ShipTypes.Frigate))
                    {
                        plutoLines.Add(Stage.CutsceneManager.PlutoLines_BluerPastures[9]);
                    }
                    plutoLines.AddRange(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(10, 3));
                    Stage.CutsceneManager.PlayDialogueSection(plutoLines);

                    NextTriggers.Add(new Trigger(() =>
                        {
                            return Stage.CutsceneManager.HitDialogueBreak;
                        },
                        () =>
                        {
                            Destroy(plutoCircle);

                            // Show tooltips
                            bool hasSeenFleetMessages = false;


                            Tooltip basicTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                            basicTooltip.Show("As the war campaign progresses, you may lose ships that you bring into battles. These ships are gone forever. Your fleet will still find a way forward, even if you lose all the ships you brought into battle. But if you lose all of the ships in your fleet, the campaign will end.", true);
                            basicTooltip.Place(Vector2.zero, new Vector2(150, 350));

                            NextTriggers.Add(new Trigger(() =>
                                {
                                    return !basicTooltip.gameObject.activeSelf;
                                },
                                () =>
                                {
                                    // Show the second message
                                    basicTooltip.Show("Similarly, the Bees have a finite number of resources. The more enemy ships you destroy in each mission, the less the Bee threat will have for their entire invasion. But the same is true in reverse: if you don’t destroy many ships, they can come back to haunt you later on.", true);

                                    NextTriggers.Add(new Trigger(() =>
                                        {
                                            return !basicTooltip.gameObject.activeSelf;
                                        },
                                        () =>
                                        {
                                            // Show the 3rd message
                                            basicTooltip.Show("In this mission, the more personnel you evacuate, the more ships you'll have for your fleet. Play strategically to preserve as much of your own fleet while whittling down the Bees' numbers. Good luck, Commander.", true);
                                            NextTriggers.Add(new Trigger(() =>
                                                {
                                                    return !basicTooltip.gameObject.activeSelf;
                                                },
                                                () =>
                                                {
                                                    Stage.Menus.TogglePausePanel();
                                                    hasSeenFleetMessages = true;
                                                    Destroy(basicTooltip.gameObject);
                                                },
                                                "Level 2 Hiding the 3rd message")
                                            );
                                        },
                                        "Level 2 Showing 3rd message")
                                    );
                                },
                                "Level 2 Showing 2nd message")
                            );

                            NextTriggers.Add(new Trigger(() =>
                                {
                                    return hasSeenFleetMessages;
                                },
                                () =>
                                {
                                    float endTime = Time.time + 300.49f; // 5 minutes to complete the level
                                    float timeLeft = endTime - Time.time;

                                    int minutesLeft;
                                    int secondsLeft;

                                    TMP_Text clockText = Stage.Menus.Clock.transform.GetChild(0).GetComponent<TMP_Text>();
                                    TMP_Text counterText = Stage.Menus.Counter.transform.GetChild(0).GetComponent<TMP_Text>();
                                    Stage.Menus.Counter.transform.GetChild(1).GetComponent<TMP_Text>().text = "Evacuated";


                                    Stage.Menus.Clock.SetActive(true);
                                    Stage.Menus.Counter.SetActive(true);
                                    Stage.Menus.PlutoShield.SetActive(true);

                                    // Create target on pluto
                                    HumanTarget humanTarget = CreateHumanTarget(new Vector2(68, -28));
                                    //humanTarget.transform.localScale = new Vector2(80, 80);

                                    clock.Reuse(1f, () =>
                                    {
                                        timeLeft = endTime - Time.time;
                                        minutesLeft = Mathf.FloorToInt(timeLeft / 60f);
                                        secondsLeft = Mathf.FloorToInt(timeLeft % 60f);

                                        personnelLost = (humanTarget.MaxHealth - humanTarget.Health) / 200;

                                        //Debug.Log($"personnel Lost: {personnelLost}, shield: {((float)(15 - personnelLost) / 15)}");

                                        Stage.Menus.PlutoShieldHealthBar.transform.localScale = new Vector2(((float)(15 - personnelLost) / 15) * 150, 1);

                                        if (timeLeft <= 0 || personnelLost >= 15)
                                        {
                                            _questPoints = personnelEvacuated;
                                            CancelTimer(clock);
                                            if (personnelLost >= 15)
                                            {
                                                Debug.Log($"Level over, too many personnel lost: {personnelLost}");
                                                CloseLevel();
                                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(18, 4), true);
                                            }
                                            else // Time ran out, player won
                                            {
                                                CloseLevel();
                                                if (personnelLost == 0)
                                                {
                                                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(22, 6), true);
                                                }
                                                else
                                                {
                                                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(22, 4), true);
                                                }

                                            }
                                        }
                                        else
                                        {
                                            // Every 5 seconds, evacuate 1 person
                                            if (Mathf.RoundToInt(timeLeft) % 5 == 0)
                                            {
                                                personnelEvacuated++;
                                            }

                                            // Display clock and personnel lost/evacuated on screen
                                            counterText.text = $"{personnelEvacuated}";
                                            clockText.text = $"{minutesLeft}:{secondsLeft:D2}";

                                        }

                                    }, true);
                                    AddTimer(clock);

                                    // Add in the first wave of Bees and give them commands

                                    // Pull the veterans if they're still alive
                                    List<SavedSquad> firstSquads = ConfigData.CurrentShips.GetSavedSquads().Where(s => s.Side == ConfigData.Configuration.AISide && s.Stats.BattlesFought > 0 && s.GetAliveSquadShips().Count > 0).ToList();

                                    // Bring in the new squads:
                                    // 1 Squad of 2 Honeybees
                                    // 1 Squad of 4 Wasps
                                    firstSquads.AddRange(new List<SavedSquad>() { 
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true)
                                        //ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                                        //ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                                        //ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                                        //ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true)
                                    });


                                    // Spawn the squads
                                    AddReinforcementSquads(firstSquads, StartingPositions[ConfigData.Configuration.AISide - 1] + new Vector2(0, 50), StartingPositions[ConfigData.Configuration.AISide - 1]);


                                    Stage.ActivateHiveMind = true;
                                    SetupHivemind();

                                    // Set timers for the subsequent waves of Bees reinforcements
                                    // Wave 2 4:00 left
                                    // 1 Squad of 2 Honeybees
                                    // 1 Squad of 4 Wasps
                                    // 1 Squad of 4 Hornets

                                    ScaledTimer wave2 = new ScaledTimer(60f, () =>
                                    {
                                        Debug.Log($"Spawning Bee reinforcements wave 2");

                                        AddReinforcementSquads(new List<SavedSquad>() {
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true)
                                        }, new Vector2(-295, -195), new Vector2(-185, -170));
                                        AddReinforcementsToHivemindCommandQueue();
                                    });

                                    AddTimer(wave2);

                                    // Wave 3 3:00 left
                                    // 2 Squad of 2 Honeybees
                                    // 1 Squad of 4 Wasps
                                    // 2 Squads of 4 Hornets
                                    // 1 Squad of 4 Yellow Jackets

                                    ScaledTimer wave3 = new ScaledTimer(120f, () =>
                                    {
                                        Debug.Log($"Spawning Bee reinforcements wave 3");
                                        AddReinforcementSquads(new List<SavedSquad>() {
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true)
                                        }, new Vector2(395, 245), new Vector2(180, 200));
                                        AddReinforcementsToHivemindCommandQueue();
                                    });

                                    AddTimer(wave3);

                                    // Wave 4 1:30 left
                                    // 1 Squad of 2 Honeybees
                                    // 1 Squad of 4 Yellow Jackets
                                    // 2 Squads of 2 Leafcutters

                                    ScaledTimer wave4 = new ScaledTimer(210f, () =>
                                    {
                                        Debug.Log($"Spawning Bee reinforcements wave 4");
                                        AddReinforcementSquads(new List<SavedSquad>() {
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2, true, true)
                                        }, StartingPositions[ConfigData.Configuration.AISide - 1] + new Vector2(0, 50), StartingPositions[ConfigData.Configuration.AISide - 1]);
                                        AddReinforcementsToHivemindCommandQueue();
                                    });

                                    AddTimer(wave4);

                                    // Set triggers for dialogues



                                    NextTriggers.Add(new Trigger(() =>
                                    {
                                        return Stage.Pool.SplitShotProjectilePool.CountAll > 0;
                                    },
                                        () =>
                                        {
                                            Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.PlutoLines_BluerPastures[14]);
                                        },
                                        "Level 2 Leafcutter splitter shot")
                                    );

                                    NextTriggers.Add(new Trigger(() =>
                                    {
                                        return State.ShipsToRelease.Any((s) => s.ShipType == ConfigData.ShipTypes.YellowJacket && s.IsDead && ((YellowJacket)s).ContactedShip != null) && !(timeLeft <= 0 || personnelLost >= 15);
                                    },
                                        () =>
                                        {
                                            Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(15, 3));
                                        },
                                        "Level 2 Yellow Jacket Hitting ship")
                                    );
                                },
                                "Level 2 Starting combat")
                            );

                        },
                        "Level 2 Starting combat messages")
                    );
                },
                "Level 2 Showing Pluto outline and dialogue")
            );
        }
        public void Level3Triggers()
        {
            Stage.Menus.SetMissionStatus("Find and destroy all the Bees!");
            //Stage.EnablePlayerControl();
            HasContinuousTriggers = true;

            // Prevent the Hivemind from giving commands
            Stage.ActivateHiveMind = false;

            Stage.CutsceneManager.Setup(() =>
            {
                Level3Ending();
            });

            // Spawn Position mining asteroids
            SpawnMiningAsteroids(5, 5);
            List<MiningAsteroid> miningAsteroids = State.MiningAsteroids.ToList();
            miningAsteroids[0].transform.localPosition = new Vector2(80, 300);
            miningAsteroids[1].transform.localPosition = new Vector2(-320, -245);
            miningAsteroids[2].transform.localPosition = new Vector2(-270, 390);
            miningAsteroids[3].transform.localPosition = new Vector2(-20, -175);
            miningAsteroids[4].transform.localPosition = new Vector2(155, -120);

            // Spawn the Bees
            AddReinforcementSquads(new List<SavedSquad>() { ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 2), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 2, true, true) }, miningAsteroids[0].transform.localPosition, Vector2.zero);

            AddReinforcementSquads(new List<SavedSquad>() { ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 1), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 1) }, miningAsteroids[1].transform.localPosition, Vector2.zero);

            AddReinforcementSquads(new List<SavedSquad>() { ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 1), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 1) }, miningAsteroids[2].transform.localPosition, Vector2.zero);

            AddReinforcementSquads(new List<SavedSquad>() { ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 3), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 1), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 2, true, true) }, miningAsteroids[3].transform.localPosition, Vector2.zero);

            AddReinforcementSquads(new List<SavedSquad>() { ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 1), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 2, true, true) }, miningAsteroids[4].transform.localPosition, Vector2.zero);


            Stage.Menus.TogglePausePanel();
            // Start the dialogue
            _dialogueTimer.Reuse(1.5f, () =>
            {
                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_SeizeTheMeans.GetRange(2, 6));
            });
            AddTimer(_dialogueTimer);

            NextTriggers.Add(new Trigger(() =>
                {
                    return Stage.CutsceneManager.HitDialogueBreak;
                },
                () =>
                {
                    Stage.Menus.TogglePausePanel();
                    // Enable player control and hive mind
                    Stage.EnablePlayerControl();
                    Stage.ActivateHiveMind = true;
                    SetupHivemind();

                    // Set the dialogue trigger
                    //NextTriggers.Add(new Trigger(() =>
                    //    {
                    //        return State.GetBeeShips().Any((s) => s.ShipType == ConfigData.ShipTypes.CarpenterBee && s.Health < s.MaxHealth && ((CarpenterBee)s).ShipAnimation.activeSelf);
                    //    },
                    //    () =>
                    //    {
                    //        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_SeizeTheMeans.GetRange(8, 4));
                    //    },
                    //    "Level 3 Carpenter Bee Dialogue")
                    //);

                    // Set level end trigger
                    NextTriggers.Add(new Trigger(() =>
                        {
                            return State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide);
                        },
                        () =>
                        {
                            WinningSide = State.IsSideKilled(ConfigData.Configuration.UserSide) ? ConfigData.Configuration.AISide : ConfigData.Configuration.UserSide;
                            Debug.Log($"Winning Side: {WinningSide}");
                            CloseLevel();
                            if (WinningSide == ConfigData.Configuration.UserSide)
                            {
                                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_SeizeTheMeans[12]);

                                NextTriggers.Add(new Trigger(() =>
                                    {
                                        return Stage.CutsceneManager.HitDialogueBreak;
                                    },
                                    () =>
                                    {
                                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_SeizeTheMeans.GetRange(17, 31), true);
                                    },
                                    "Level 3 Post-success dialogue")
                                );
                            }
                            else
                            {
                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_SeizeTheMeans.GetRange(13, 4), true);

                            }


                        },
                        "Level 3 Mission Success or Fail dialogues")
                    );
                },
                "Level 3 Start Level")
            );
        }
        public void Level4Triggers()
        {
            Stage.Menus.SetMissionStatus("Survive and mine as many minerals as you can");
            HasContinuousTriggers = true;

            Stage.CutsceneManager.Setup(() =>
            {
                Level4Ending();
            });

            // Spawn Position mining asteroids
            SpawnMiningAsteroids(5, 5);
            List<MiningAsteroid> miningAsteroids = State.MiningAsteroids.ToList();
            miningAsteroids[0].transform.localPosition = new Vector2(80, 300);
            miningAsteroids[1].transform.localPosition = new Vector2(-320, -245);
            miningAsteroids[2].transform.localPosition = new Vector2(-270, 390);
            miningAsteroids[3].transform.localPosition = new Vector2(-20, -175);
            miningAsteroids[4].transform.localPosition = new Vector2(155, -120);

            // Start the dialogue
            Stage.Menus.TogglePausePanel();
            _dialogueTimer.Reuse(1.5f, () =>
            {
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[2]);
            });
            AddTimer(_dialogueTimer);

            NextTriggers.Add(new Trigger(() =>
                {
                    return Stage.CutsceneManager.HitDialogueBreak;
                },
                () =>
                {
                    // Show mining locations on Mini map
                    GameObject[] minimapIcons = new GameObject[5];
                    for (int i = 0; i < 5; i++)
                    {
                        minimapIcons[i] = Instantiate(Stage.Prefabs.MinimapCircle, Map.transform);
                        minimapIcons[i].transform.localPosition = miningAsteroids[i].transform.localPosition;
                        minimapIcons[i].GetComponent<SpriteRenderer>().color = Color.red;
                        minimapIcons[i].transform.localScale = new Vector2(24, 24);

                    }

                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_OfProduction.GetRange(3, 5));

                    NextTriggers.Add(new Trigger(() =>
                        {
                            return Stage.CutsceneManager.HitDialogueBreak;
                        },
                        () =>
                        {
                            // Hide the mining locations on the mini map
                            for (int i = 0; i < minimapIcons.Length; i++)
                            {
                                Destroy(minimapIcons[i]);
                            }




                            // Show the mining tooltips

                            Tooltip basicTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                            basicTooltip.Show("To mine, first select your Factory ships, then right click on ore-rich asteroids. Once the Factory arrives, it will automatically begin collecting materials.", true);
                            basicTooltip.Place(Vector2.zero, new Vector2(150, 200));


                            Tooltip endMissionTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                            endMissionTooltip.Show("You can retreat from the mission at any time. Just send your ships to the green zone on the left side of the map where they can retreat to safety.", true);
                            endMissionTooltip.Place(new Vector2(-400, 0), new Vector2(150, 200));


                            // Green exit zone at left of the screen lights up
                            GameObject exitBox = Instantiate(Stage.Prefabs.ExitZonePrefab, Map.transform);
                            exitBox.transform.localPosition = new Vector2(-512, 0);
                            exitBox.transform.localScale = new Vector2(50, 256);
                            Zone exitZone = exitBox.GetComponent<Zone>();

                            exitZone.OnShipEnter = (ship) =>
                            {
                                if (ship.Side == ConfigData.Configuration.UserSide)
                                {
                                    _lastShipRetreated = State.GetShips(ship.Side).Where((s) => s.IsMobile).Count() == 1;
                                    ship.EndKill();
                                }
                            };

                            NextTriggers.Add(new Trigger(() =>
                                {
                                    return !basicTooltip.gameObject.activeSelf && !endMissionTooltip.gameObject.activeSelf;
                                },
                                () =>
                                {
                                    Destroy(basicTooltip);
                                    Destroy(endMissionTooltip);

                                    Stage.Menus.TogglePausePanel();
                                    Stage.EnablePlayerControl();

                                    // Set timers for Bee waves

                                    // Wave 1 @ 2 Minutes
                                    // 2 Squads of 2 Honeybees
                                    // 2 Squads of 4 Hornets

                                    ScaledTimer wave1 = new ScaledTimer(120f, () =>
                                    {
                                        Debug.Log($"Spawning Bee reinforcements wave 1");

                                        AddReinforcementSquads(new List<SavedSquad>() {
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true)
                                        }, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);

                                        AddReinforcementsToHivemindCommandQueue();
                                        Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[8]);
                                    });

                                    AddTimer(wave1);

                                    // Wave 2 @ 4 Minutes
                                    // 1 Squad of 2 Honeybees
                                    // 1 Squad of 4 Wasps
                                    // 2 Squads of 4 Hornets

                                    ScaledTimer wave2 = new ScaledTimer(240f, () =>
                                    {
                                        Debug.Log($"Spawning Bee reinforcements wave 2");

                                        AddReinforcementSquads(new List<SavedSquad>() {
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true)
                                        }, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);

                                        AddReinforcementsToHivemindCommandQueue();
                                        Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[Utilities.RandomInt(5) + 10]);
                                    });

                                    AddTimer(wave2);

                                    // Wave 3 @ 6 Minutes
                                    // 1 Squad of 2 Honeybees
                                    // 2 Squads of 4 Wasps
                                    // 2 Squads of 6 Hornets
                                    // 3 Squads of 4 Yellow Jackets

                                    ScaledTimer wave3 = new ScaledTimer(360f, () =>
                                    {
                                        Debug.Log($"Spawning Bee reinforcements wave 3");

                                        AddReinforcementSquads(new List<SavedSquad>() {
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


                                    // Wave 4 @ 8 Minutes
                                    // 1 Squad of 2 Honeybees
                                    // 2 Squad of 6 Wasps
                                    // 2 Squad of 8 Hornets
                                    // 3 Squads of 4 Yellow Jackets
                                    // 2 Squads of 4 Leafcutters
                                    // All squads not previously killed
                                    ScaledTimer wave5 = new ScaledTimer();
                                    ScaledTimer wave4 = new ScaledTimer(480f, () =>
                                    {
                                        Debug.Log($"Spawning Bee reinforcements wave 4");

                                        // Pull the veterans if they're still alive
                                        List<SavedSquad> reinforcementSquads = ConfigData.CurrentShips.GetSavedSquads().Where(s => s.Side == ConfigData.Configuration.AISide && s.Stats.BattlesFought > 0 && !s.IsLoadedIntoLevel && s.GetAliveSquadShips().Count > 0).ToList();

                                        // Bring in the new squads:
                                        // 1 Squad of 2 Honeybees
                                        // 1 Squad of 4 Wasps
                                        reinforcementSquads.AddRange(new List<SavedSquad>() {
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

                                        // Wave 5+ @ 9+ Minutes
                                        // 1 Squad of 2 Honeybees
                                        // 2 Squad of 6 Wasps + 2 / per wave
                                        // 2 Squad of 8 Hornets + 1 / per wave
                                        // 3 Squads of 4 Yellow Jackets + 2 / per wave
                                        // 2 Squads of 6 Leafcutters + 2 / per wave
                                        int waveCount = 4;
                                        wave5.Reuse(60f, () =>
                                        {
                                            waveCount++;
                                            Debug.Log($"Spawning Bee reinforcements wave 5+");

                                            reinforcementSquads = new List<SavedSquad>() {
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
                                                reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true));
                                                reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true));

                                                reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true));

                                                reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true));
                                                reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true));

                                                reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 6, true, true));
                                                reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 6, true, true));
                                            }

                                            AddReinforcementSquads(reinforcementSquads, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);

                                            AddReinforcementsToHivemindCommandQueue();
                                            Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[Utilities.RandomInt(5) + 10]);
                                        }, true);

                                        AddTimer(wave5);

                                        NextTriggers.Add(new Trigger(() =>
                                            {
                                                return State.IsSideKilled(ConfigData.Configuration.AISide); // player won somehow?
                                            },
                                            () =>
                                            {
                                                CloseLevel();
                                                CancelTimer(wave5);
                                                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[17], true);
                                            },
                                            "Level 4 Player Won? dialogue")
                                        );
                                    });

                                    AddTimer(wave4);




                                    // Set dialogue triggers
                                    NextTriggers.Add(new Trigger(() =>
                                        {
                                            return State.IsSideKilled(ConfigData.Configuration.UserSide) && !_lastShipRetreated;
                                        },
                                        () =>
                                        {
                                            CloseLevel();
                                            CancelTimer(wave5);
                                            Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[16], true);
                                        },
                                        "Level 4 Player losing dialogue")
                                    );

                                    NextTriggers.Add(new Trigger(() =>
                                        {
                                            return State.IsSideKilled(ConfigData.Configuration.UserSide) && _lastShipRetreated;
                                        },
                                       () =>
                                       {
                                           CloseLevel();
                                           CancelTimer(wave5);
                                           Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[17], true);
                                       },
                                       "Level 4 Player retreating dialogue")
                                    );

                                },
                                "Level 4 Starting Level")
                            );
                        },
                        "Level 4 Show mining tooltips")
                    );
                },
                "Level 4 Highlighting Mining Locations")
            );
        }
        /// <summary>
        /// Reveals the exit zone for level 4
        /// </summary>
        public void SetRetreatForLevel4()
        {
            Stage.Menus.CloseDialogue();
            Stage.Menus.RetreatButton.SetActive(false);
            // Make exit zone
            ScaledTimer retreatDialogue = new ScaledTimer(1, () =>
            {
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[15]);
            });
            AddTimer(retreatDialogue);

            // Green exit zone at left of the screen lights up
            GameObject exitBox = Instantiate(Stage.Prefabs.ExitZonePrefab, Map.transform);
            exitBox.transform.localPosition = new Vector2(-512, 0);
            exitBox.transform.localScale = new Vector2(50, 256);
            Zone exitZone = exitBox.GetComponent<Zone>();

            exitZone.OnShipEnter = (ship) =>
            {
                if (ship.Side == ConfigData.Configuration.UserSide)
                {
                    _lastShipRetreated = State.GetShips(ship.Side).Where((s) => s.IsMobile).Count() == 1;
                    ship.EndKill();
                }
            };
        }
        public void Level5Triggers()
        {
            Stage.Menus.SetMissionStatus("Destroy all the Bees to break through the blockade");
            HasContinuousTriggers = true;

            Stage.CutsceneManager.Setup(() =>
            {
                Level5Ending();
            });

            // Spawn the Bees

            // 1 Squad of 2 Leafcutters
            // 2 Squads of 6 Hornets
            // 2 Squads of 2 Honeybees
            // 1 Squad of 4 Wasps

            AddReinforcementSquads(new List<SavedSquad>() {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
            }, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);

            Stage.EnablePlayerControl();
            // Spawn the exit zone
            //GameObject exitBox = Instantiate(Stage.Prefabs.ExitZonePrefab, Map.transform);
            //exitBox.transform.localPosition = new Vector2(512, 0);
            //exitBox.transform.localScale = new Vector2(50, 256);
            //Zone exitZone = exitBox.GetComponent<Zone>();

            //exitZone.OnShipEnter = (ship) =>
            //{
            //    if (ship.Side == ConfigData.Configuration.UserSide)
            //    {
            //        _someShipsHaveRetreated = true;
            //        ship.EndKill();
            //    }
            //};

            // Set dialogue triggers
            NextTriggers.Add(new Trigger(() =>
            {
                return State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide);
            },
                () =>
                {
                    WinningSide = State.IsSideKilled(ConfigData.Configuration.UserSide) ? ConfigData.Configuration.AISide : ConfigData.Configuration.UserSide;
                    Debug.Log($"Winning Side: {WinningSide}");
                    CloseLevel();
                    if (WinningSide == ConfigData.Configuration.UserSide) // Player won
                    {
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_PressingForward.GetRange(1, 4), true);

                    }
                    else
                    {
                        CloseLevel();
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_PressingForward.GetRange(5, ConfigData.CurrentShips.HasShipsOfType(ConfigData.ShipTypes.Factory) ?  7 : 2), true);
                    }
                },
                "Level 5 Ending dialogue")
            );

        }
        public void Level6Triggers()
        {
            Stage.Menus.SetMissionStatus("Find and destroy all the Bees");
            HasContinuousTriggers = true;
            Stage.ActivateHiveMind = false;

            Stage.CutsceneManager.Setup(() =>
            {
                Level6Ending();
            });

            // Spawn the Bees

            // 2 Squads of 1 Leafcutter
            // 2 Squads of 4 Hornets
            // 1 Squad of 8 Hornets
            // 1 Squad of 4 Yellow Jackets
            // 2 Squads of 2 Wasps
            // 1 Squad of 1 Bumblebee

            AddReinforcementSquads(new List<SavedSquad>() {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
            }, new Vector2(250, 250), Vector2.zero);

            AddReinforcementSquads(new List<SavedSquad>() {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 1, false, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 1, false, true),
            }, Vector2.zero, Vector2.zero);

            AddReinforcementSquads(new List<SavedSquad>() {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Bumblebee, 1),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
            }, new Vector2(-250, -250), Vector2.zero);

            State.GetHumanShips().ForEach((ship) =>
            {
                if (ship.ShipType == ConfigData.ShipTypes.Scout)
                {
                    ship.ProximityCollider = Instantiate(Stage.Prefabs.HumanProximityColliderPrefab, Vector3.zero, Quaternion.identity, ship.transform).GetComponent<ProximityCollider>();
                    ship.HasProximityCollider = true;
                    ship.ProximityCollider.Create(ship);
                    ship.ProximityCollider.transform.localPosition = Vector3.zero;
                    ship.ProximityCollider.Activate();
                }
            });

            // Start the dialogue
            Stage.Menus.TogglePausePanel();
            _dialogueTimer.Reuse(1.5f, () =>
            {
                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(2, 4));
            });
            AddTimer(_dialogueTimer);

            NextTriggers.Add(new Trigger(() =>
                {
                    return Stage.CutsceneManager.HitDialogueBreak;
                },
                () =>
                {
                    SelectedCarrierTrigger();
                }, "Level 6 Carrier trigger")
            );

            NextTriggers.Add(new Trigger(() =>
                {
                    return _hasSeenCarrierIntroIfNeeded;
                },
                () =>
                {
                    Stage.Menus.TogglePausePanel();
                    // Spawn the cruiser after a little bit and show dialogue. Have hornets or something attacking it
                    ScaledTimer cruiserTimer = new ScaledTimer(5f, () =>
                    {
                        ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Cruiser, 2);
                        SavedSquad cruiserSquad = CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Cruiser, 2);

                        LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() { cruiserSquad }, new Vector2(200, 200), Vector2.zero, true);

                        // Add hornets attacking cruisers
                        AddReinforcementSquads(new List<SavedSquad>() {
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true),
                        }, new Vector2(280, 200), Vector2.zero);
                        AddReinforcementsToHivemindCommandQueue();

                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(6, 6));

                        

                    });
                    AddTimer(cruiserTimer);



                    // Set dialogue triggers
                    // spotting bumblebee
                    Ship bumblebee = State.GetBeeShips().Where((s) => s.ShipType == ConfigData.ShipTypes.Bumblebee).First();
                    NextTriggers.Add(new Trigger(() =>
                        {
                            return State.GetShips(ConfigData.Configuration.UserSide).Any((s) => s.ShipsWithinRange.Contains(bumblebee) || s.HasProximityCollider && s.ProximityCollider.NearbyEnemyShips.Contains(bumblebee));
                        },
                        () =>
                        {
                            Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheOffensive[12]);
                            bumblebee.ShipsHit.Clear();

                            // Getting hit by bumblebee
                            NextTriggers.Add(new Trigger(() =>
                                {
                                    return bumblebee.ShipsHit.Count > 0;
                                },
                                () =>
                                {
                                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(13, 3));
                                },
                                "Level 6 Hit by Bumblebee")
                            );

                            // Kiling bumblebee
                            NextTriggers.Add(new Trigger(() =>
                                {
                                    return bumblebee.IsDead && !(State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide));
                                },
                                () =>
                                {
                                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(16, 2));
                                },
                                "Level 6 Killed Bumblebee")
                            );
                        },
                        "Level 6 Discovering Bumblebee")
                    );

                    
                    // Level ending
                    NextTriggers.Add(new Trigger(() =>
                        {
                            return State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide);
                        },
                        () =>
                        {
                            WinningSide = State.IsSideKilled(ConfigData.Configuration.UserSide) ? ConfigData.Configuration.AISide : ConfigData.Configuration.UserSide;
                            Debug.Log($"Winning Side: {WinningSide}");
                            CloseLevel();
                            if (WinningSide == ConfigData.Configuration.UserSide) // Player won
                            {
                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(18, ConfigData.CurrentShips.HasShipsOfType(ConfigData.ShipTypes.Factory) ? 20 : 17), true);
                            }
                            else
                            {
                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(38, 3), true);
                            }

                        },
                        "Level 6 Ending dialogue")
                    );
                },
                "Level 6 Starting")
            );


            
        }
        public void SelectedCarrierTrigger()
        {
            if (!ConfigData.UserProgressData.HasSeenCarrierIntro && State.GetHumanShipTypes().Contains(ConfigData.ShipTypes.Carrier))
            {
                State.SelectSquads(State.GetSquadsBySide(ConfigData.Configuration.UserSide).Where((squad) => squad.IsCarrierSquad).ToList());
                Stage.IsPlayerControlling = false;
                Tooltip basicTooltip = null;
                //Debug.LogError("Selected carrier squad");
                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.SelectedCarrierSquad);

                NextTriggers.Add(new Trigger(() =>
                {
                    return Stage.CutsceneManager.HitDialogueBreak;
                },
                    () =>
                    {
                        basicTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                        basicTooltip.Show("To use the Strikers, simply right click on a target squad. The Strikers will fly towards their targets, drop their bombs, and return to the Carrier to reload. Right click anywhere else to move them and cancel the bombing run.", true);
                        basicTooltip.Place(Vector2.zero, new Vector2(150, 300));


                        NextTriggers.Add(new Trigger(() =>
                            {
                                return !basicTooltip.gameObject.activeSelf;
                            },
                            () =>
                            {
                                Destroy(basicTooltip);
                                ConfigData.UserProgressData.HasSeenCarrierIntro = true;
                                _hasSeenCarrierIntroIfNeeded = true;

                                if (!Stage.ActivateHiveMind)
                                {
                                    Stage.ActivateHiveMind = true;
                                    SetupHivemind();
                                }
                                if (!Stage.IsPlayerControlling)
                                {
                                    Stage.IsPlayerControlling = true;
                                }
                            },
                            "Level 6-8 Carrier Squads Selected End")
                        );
                    },
                    "Level 6-8 Carrier Squad Tooltip")
                );
                

            }
            else
            {
                if (!Stage.ActivateHiveMind)
                {
                    Stage.ActivateHiveMind = true;
                    SetupHivemind();
                }
                if (!Stage.IsPlayerControlling)
                {
                    Stage.IsPlayerControlling = true;
                }
                _hasSeenCarrierIntroIfNeeded = true;
            }
           


        }
        public void EasterEggTriggers()
        {
            _egg.Reuse(1f, () => // 30
            {
                if (Utilities.RandomInt(100) == 36 && Stage.CutsceneManager.HitDialogueBreak)
                {
                    Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.EasterEggLines[Utilities.RandomInt(Stage.CutsceneManager.EasterEggLines.Count)]);
                }
                
            }, true);

            AddTimer(_egg);
        }
        public void FishTankTrigger()
        {
            if (!ConfigData.UserProgressData.IsFishTankUnlocked)
            {
                _fishTank.Reuse(60 * 30f, () => // 30 minutes
                {

                    ConfigData.UserProgressData.IsFishTankUnlocked = true;
                    ConfigData.UserProgressData.Save();
                    Pause();

                    Dialogue fishTankAlert = new Dialogue(Stage.DialoguePrefab, "Are you sure this is for you?", "Perhaps you'd like to look at the fish tank instead?",
                    new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No }, new List<UnityAction>() { Stage.Menus.GoToFishTank });
                    fishTankAlert.Show();

                }, true);

                AddTimer(_fishTank);
            }

        }
        public void Level7Triggers()
        {
            Stage.ActivateHiveMind = false;
            HasContinuousTriggers = true;

            Stage.CutsceneManager.Setup(() =>
            {
                Level7Ending();
            });

            Vector2[] upperPositions = new Vector2[] { CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition };
            Vector2[] lowerPositions = new Vector2[] { new Vector2(600, -512), new Vector2(450, -450) };

            // Spawn and Position mining asteroids
            SpawnMiningAsteroids(6, 6);
            List<MiningAsteroid> miningAsteroids = State.MiningAsteroids.ToList();
            miningAsteroids[0].transform.localPosition = new Vector2(235, -105);
            miningAsteroids[1].transform.localPosition = new Vector2(0, -300);
            miningAsteroids[2].transform.localPosition = new Vector2(20, -400);
            miningAsteroids[3].transform.localPosition = new Vector2(-80, -370);
            miningAsteroids[4].transform.localPosition = new Vector2(-350, 80);
            miningAsteroids[5].transform.localPosition = new Vector2(-200, 200);


            // Set the dialogue list for reinforcements
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
            endMissionTooltip.Show("You can retreat from the mission at any time. Just send your ships to the green zone on the left side of the map where they can retreat to safety.", true);
            endMissionTooltip.Place(new Vector2(-400, 0), new Vector2(150, 250));


            // Green exit zone at left of the screen lights up
            GameObject exitBox = Instantiate(Stage.Prefabs.ExitZonePrefab, Map.transform);
            exitBox.transform.localPosition = new Vector2(-512, 0);
            exitBox.transform.localScale = new Vector2(50, 256);
            Zone exitZone = exitBox.GetComponent<Zone>();

            exitZone.OnShipEnter = (ship) =>
            {
                if (ship.Side == ConfigData.Configuration.UserSide)
                {
                    _lastShipRetreated = State.GetShips(ship.Side).Where((s) => s.IsMobile).Count() == 1;
                    ship.EndKill();
                }
            };

            Stage.Menus.SetMissionStatus("Survive and mine as many minerals as you can");

            NextTriggers.Add(new Trigger(() =>
                {
                    return !endMissionTooltip.gameObject.activeSelf;
                },
                () =>
                {
                    Destroy(endMissionTooltip);
                    SelectedCarrierTrigger();


                    NextTriggers.Add(new Trigger(() =>
                        {
                            return _hasSeenCarrierIntroIfNeeded;
                        },
                        () =>
                        {
                            Stage.Menus.TogglePausePanel();
                            // Set timers for Bee waves

                            // Wave 1 @ 2 Minutes
                            // 1 Squad of 1 Leafcutter
                            // 1 Squad of 2 Honeybees
                            // 1 Squad of 2 Wasps
                            // 2 Squads of 4 Hornets

                            ScaledTimer wave1 = new ScaledTimer(120f, () =>
                            {
                                Debug.Log($"Spawning Bee reinforcements wave 1");

                                AddReinforcementSquads(new List<SavedSquad>() {
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 1, false, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true)
                                }, upperPositions[0], upperPositions[1]);
                                AddReinforcementsToHivemindCommandQueue();

                                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheDefensive[1]);
                            });

                            AddTimer(wave1);

                            // Wave 2 @ 4 Minutes
                            // 1 Squad of 1 Leafcutter
                            // 1 Squad of 2 Honeybees
                            // 1 Squad of 2 Wasps
                            // 2 Squads of 4 Hornets

                            ScaledTimer wave2 = new ScaledTimer(240f, () =>
                            {
                                Debug.Log($"Spawning Bee reinforcements wave 2");

                                AddReinforcementSquads(new List<SavedSquad>() {
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 1, false, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true)
                                }, upperPositions[0], upperPositions[1]);


                                AddReinforcementsToHivemindCommandQueue();
                                Stage.CutsceneManager.PlaySingleDialogueLine(dialogueLines[Utilities.RandomInt(dialogueLines.Count)]);
                            });

                            AddTimer(wave2);

                            // Wave 3 @ 6 Minutes
                            // 2 Squads of 4 Yellow Jackets
                            // 1 Squad of 2 Honeybees
                            // 1 Squad of 4 Wasps
                            // 1 Squad of 4 Hornets

                            ScaledTimer wave3 = new ScaledTimer(360, () =>
                            {
                                Debug.Log($"Spawning Bee reinforcements wave 3");

                                AddReinforcementSquads(new List<SavedSquad>() {
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true)
                                }, upperPositions[0], upperPositions[1]);

                                AddReinforcementsToHivemindCommandQueue();
                                Stage.CutsceneManager.PlaySingleDialogueLine(dialogueLines[Utilities.RandomInt(dialogueLines.Count)]);
                            });

                            AddTimer(wave3);

                            // Wave 4 @ 8 Minutes
                            // 2 Squads of 6 Yellow Jackets
                            // 2 Squads of 2 Honeybees
                            // 2 Squads of 4 Wasps
                            // 1 Squad of 4 Leafcutters

                            ScaledTimer wave4 = new ScaledTimer(480, () =>
                            {
                                Debug.Log($"Spawning Bee reinforcements wave 4");

                                AddReinforcementSquads(new List<SavedSquad>() {
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 6, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 6, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                                }, upperPositions[0], upperPositions[1]);

                                AddReinforcementSquads(new List<SavedSquad>() {
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                                }, lowerPositions[0], lowerPositions[1]);


                                AddReinforcementsToHivemindCommandQueue();
                                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheDefensive[8]);
                            });

                            AddTimer(wave4);


                            // Wave 5 @ 9 Minutes
                            // 2 Squads of 2 Honeybees
                            // 2 Squads of 6 Wasps
                            // 2 Squads of 8 Hornets
                            // 1 Squad of 4 Yellow Jackets
                            // 2 Squads of 4 Leafcutters
                            // 1 Squad of 2 Bumblebees
                            // All squads not previously killed
                            ScaledTimer wave5 = new ScaledTimer(540, () =>
                            {
                                Debug.Log($"Spawning Bee reinforcements wave 5");

                                // Pull the veterans if they're still alive
                                List<SavedSquad> reinforcementSquads = ConfigData.CurrentShips.GetSavedSquads().Where(s => s.Side == ConfigData.Configuration.AISide && s.Stats.BattlesFought > 0 && !s.IsLoadedIntoLevel && s.GetAliveSquadShips().Count > 0).ToList();

                                // Bring in the new squads:
                                reinforcementSquads.AddRange(new List<SavedSquad>() {
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Bumblebee, 2, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                                });

                                AddReinforcementSquads(reinforcementSquads, upperPositions[0], upperPositions[1]);

                                reinforcementSquads = new List<SavedSquad>() {
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

                            // Wave 6 @ 10 Minutes
                            // 2 Squads of 2 Honeybees
                            // 2 Squads of 6 Wasps
                            // 2 Squads of 8 Hornets
                            // 1 Squad of 4 Yellow Jackets
                            // 2 Squads of 4 Leafcutters
                            // 2 Squads of 2 Bumblebees
                            ScaledTimer wave6 = new ScaledTimer(600, () =>
                            {
                                Debug.Log($"Spawning Bee reinforcements wave 6");

                                AddReinforcementSquads(new List<SavedSquad>() {
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Bumblebee, 2, true, true),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                                }, upperPositions[0], upperPositions[1]);

                                AddReinforcementSquads(new List<SavedSquad>() {
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

                                // Level ending
                                NextTriggers.Add(new Trigger(() =>
                                    {
                                        return State.IsSideKilled(ConfigData.Configuration.AISide); // Bees lose
                                    },
                                    () =>
                                    {
                                        WinningSide = ConfigData.Configuration.UserSide;
                                        Debug.Log($"Winning Side: {WinningSide}");
                                        CloseLevel();
                                        Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheDefensive[17]);
                                        NextTriggers.Add(new Trigger(() =>
                                            {
                                                return Stage.CutsceneManager.HitDialogueBreak;
                                            },
                                            () =>
                                            {
                                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheDefensive.GetRange(14, 3), true);

                                            },
                                            "Level 7 Ended dialogue")
                                        );

                                    },
                                    "Level 7 Ended dialogue")
                                );
                            });

                            AddTimer(wave6);

                            // Level ending
                            NextTriggers.Add(new Trigger(() =>
                                {
                                    return State.IsSideKilled(ConfigData.Configuration.UserSide); // Player loses
                                },
                                () =>
                                {
                                    WinningSide = ConfigData.Configuration.AISide;
                                    Debug.Log($"Winning Side: {WinningSide}");
                                    CloseLevel();
                                    if (WinningSide == ConfigData.Configuration.AISide) // Player lost or retreated
                                    {
                                        if (!_lastShipRetreated)
                                        {
                                            Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheDefensive[13]);
                                        }
                                        else
                                        {
                                            Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheDefensive[17]);
                                        }


                                    }
                                   
                                    NextTriggers.Add(new Trigger(() =>
                                        {
                                            return Stage.CutsceneManager.HitDialogueBreak;
                                        },
                                        () =>
                                        {
                                            Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheDefensive.GetRange(14, 3), true);

                                        },
                                        "Level 7 Ended dialogue")
                                    );

                                },
                                "Level 7 Ended dialogue")
                            );

                        },
                        "Level 7 Start Wave Timers")
                    );

                },
                "Level 7 Show carrier trigger dialogue")
            );

            

           

        }
        /// <summary>
        /// Reveals the exit zone for level 7
        /// </summary>
        public void SetRetreatForLevel7()
        {
            Stage.Menus.CloseDialogue();
            Stage.Menus.RetreatButton.SetActive(false);
            // Make exit zone
            ScaledTimer retreatDialogue = new ScaledTimer(1, () =>
            {
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheDefensive[12]);
            });
            AddTimer(retreatDialogue);

            // Green exit zone at left of the screen lights up
            GameObject exitBox = Instantiate(Stage.Prefabs.ExitZonePrefab, Map.transform);
            exitBox.transform.localPosition = new Vector2(-512, 0);
            exitBox.transform.localScale = new Vector2(50, 256);
            Zone exitZone = exitBox.GetComponent<Zone>();

            exitZone.OnShipEnter = (ship) =>
            {
                if (ship.Side == ConfigData.Configuration.UserSide)
                {
                    _lastShipRetreated = State.GetShips(ship.Side).Where((s) => s.IsMobile).Count() == 1;
                    ship.EndKill();
                }
            };
        }
        public void Level8Triggers()
        {
            Stage.Menus.SetMissionStatus("Rescue the Barges and destroy all the Bees!");
            Stage.ActivateHiveMind = false;
            HasContinuousTriggers = true;

            Stage.CutsceneManager.Setup(() =>
            {
                Level8Ending();
            });

            Vector2[] spawnPositions = new Vector2[] { CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition };

            // Add the three barges and spawn them
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Barge, 3);
            SavedSquad barges = CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Barge, 3);

            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() { barges }, new Vector2(300, 300), Vector2.zero, true);
            Squad bargeSquad = State.GetHumanShips().Find((s) => s.ShipType == ConfigData.ShipTypes.Barge).Squad;

            // Give barges command to move to center
            bargeSquad.HasCommandQueue = true;
            bargeSquad.CommandQueueEmptyAction = () =>
            {
                bargeSquad.HasCommandQueue = false;
            };
            MoveToPoint moveToPoint = (MoveToPoint)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToPoint);
            moveToPoint.Setup(bargeSquad, false, null, null, Vector2.zero); // Center of Map
            bargeSquad.CommandQueue.Enqueue(moveToPoint);


            // Add hornets attacking barges
            AddReinforcementSquads(new List<SavedSquad>() {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
            }, new Vector2(250, 250), Vector2.zero);


            if (State.GetSquadsBySide(ConfigData.Configuration.AISide).Count == 0) // If all the bee squads were killed in the last level, add a hornet squad to ensure the player has something to fight
            {
                CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Hornet, 2);

                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Hornet, 2);

                AddReinforcementSquads(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 2, true, true),
                }, new Vector2(250, 250), Vector2.zero);
            }


            //intial dialogue

            Stage.Menus.TogglePausePanel();
            _dialogueTimer.Reuse(1.5f, () =>
            {
                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_ANewThreat.GetRange(1, 5));
            });
            AddTimer(_dialogueTimer);


            // dialogue triggers

            NextTriggers.Add(new Trigger(() =>
                {
                    return Stage.CutsceneManager.HitDialogueBreak;
                },
                () =>
                {
                    SelectedCarrierTrigger();
                },
                "Level 8 Carrier trigger")
            );

            NextTriggers.Add(new Trigger(() =>
            {
                return _hasSeenCarrierIntroIfNeeded;
            },
                () =>
                {
                    Stage.Menus.TogglePausePanel();
                    bargeSquad.RunCommandQueue();


                    // Spawn bee reinforcements
                    // Wave 1 @ 2 Minutes
                    // 1 Squad of 2 Honeybees
                    // 1 Squad of 2 Wasps
                    // 1 Squad of 4 Hornets
                    // 2 Squads of 4 Yellow Jackets

                    ScaledTimer reinforcements = new ScaledTimer(60f, () =>
                    {
                        Debug.Log($"Spawning Bee reinforcements wave 1");

                        AddReinforcementSquads(new List<SavedSquad>() {
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2, true, true),
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true)
                        }, spawnPositions[0], spawnPositions[1]);
                        AddReinforcementsToHivemindCommandQueue();

                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_ANewThreat.GetRange(7, 2));
                    });

                    AddTimer(reinforcements);

                    NextTriggers.Add(new Trigger(() =>
                        {
                            return bargeSquad.GetShips().Count < 3 && !(State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide));
                        },
                       () =>
                       {
                           Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_ANewThreat[6]);
                       },
                       "Level 8 Barge Killed")
                    );

                    NextTriggers.Add(new Trigger(() =>
                        {
                            return bargeSquad.GetShips().Any((s) => ((Barge)s).HasStartedCharging) && !(State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide));
                        },
                       () =>
                       {
                           Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_ANewThreat[9]);
                       },
                       "Level 8 Barge Charge")
                    );

                    // ending condition
                    NextTriggers.Add(new Trigger(() =>
                        {
                            return State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide);
                        },
                       () =>
                       {
                           bool isBargeSquadDead = bargeSquad.IsDead;
                           CloseLevel();
                           if (isBargeSquadDead) // Bees Won
                           {
                               Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_ANewThreat.GetRange(30, 12), true);
                           }
                           else
                           {
                               Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_ANewThreat.GetRange(10, 20), true);
                               CancelTimer(reinforcements);
                           }
                       },
                       "Level 8 Ending")
                   );

                },
                "Level 8 Starting")
            );

            
        }

        public void CloseLevel()
        {
            Pause();
            CancelTimer(_egg);
            CancelTimer(_fishTank);
            Map.FogOfWar.SetActive(true); // Fade to black 
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                Stage.Menus.MissionStatus.SetActive(false);
            }
            //if (Stage.Menus.IsMiniMapOpen)
            //{
            //    Stage.Menus.ToggleMiniMapDisplay();
            //}
            State.GetShips().ToList().ForEach((ship) =>
            {
                if (ship.HasUserFogOfWarVision)
                {
                    ship.FogOfWarVision.Kill(0, true);
                }
                ship.EndKill();
            });
            UnPause();
        }
        public void Level0Ending(SavedSquad gunshipSquad)
        {
            Debug.Log("Level complete!");

            // Add new human ships to the game
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 1); 
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 4); 
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Dreadnought, 2);

            State.PlayerNewShipsReceived += 7;


            // Add new ships to the human's available ships
            FleetShip gunship = CurrentShips.GetFirstAvailableShipOfType(ConfigData.ShipTypes.Gunship);
            //Debug.Log($"Gunship squad {gunshipSquad}, gunship {gunship}");
            gunshipSquad.AddShipToSquad(new SquadShip(gunship.Id, gunship.Type, Vector2.zero));
            gunshipSquad.AutoRepositionSquad();

            // Starting Dreadnought squad #8
            SavedSquad squad = CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Dreadnought, 2);
            //squad.StartingPosition = ConfigData.StartingPositionOffset + new Vector2(0, -10);

            // Starting Frigate squad #9
            squad = CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Frigate, 4);
            //squad.StartingPosition = ConfigData.StartingPositionOffset;


            // Add Honeybee, Frigate, and Dreadnought to the codex and visibility
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Honeybee);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Frigate);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Dreadnought);

            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Hornet);
            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Wasp);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Frigate);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Dreadnought);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Frigate);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Dreadnought);

            ConfigData.UserProgressData.SetShipTypes();

            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();


            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Debug.Log("Did Standard level complete, showing dialogue");
            Stage.Menus.ShowLevelSummary();

        }
        public void Level1Ending()
        {
            Debug.Log("Level 1 complete!");

            // Add new human ships to the game, 10 Scouts, 2 Gunships, 2 Frigates, 1 Dreadnought
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 10);
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 2); 
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 1); 
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Dreadnought, 1);

            State.PlayerNewShipsReceived += 15;


            // Add Hornet and Wasp to codex and add leafcutter and yellow jacket to visibility
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Hornet);
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Wasp);

            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Leafcutter);
            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.YellowJacket);

            ConfigData.UserProgressData.SetShipTypes();


            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Debug.Log("Did Standard level complete, showing dialogue");
            Stage.Menus.ShowLevelSummary();

        }
        public void Level2Ending()
        {
            Debug.Log("Level 2 complete!");

            switch (_questPoints)
            {
                case 60:
                    // Player 10 Scouts, 8 Gunships, 4 Frigates, and 2 Dreadnoughts
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5); 
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 8); 
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 4);
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Dreadnought, 2);
                    State.PlayerNewShipsReceived += 19;
                    break;
                case > 50:
                    // Player gets 5 Scouts, 6 Gunships, 2 Frigates, and 2 Dreadnoughts
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5); 
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 6); 
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 2); 
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Dreadnought, 2);
                    State.PlayerNewShipsReceived += 15;
                    break;
                case > 35:
                    // Player gets 5 Scouts, 4 Gunships, 2 Frigates
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5); 
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 4); 
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 2);
                    State.PlayerNewShipsReceived += 11;
                    break;
                case > 15:
                    // Player gets 5 Scouts, 4 Gunships
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5); 
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 4);
                    State.PlayerNewShipsReceived += 9;
                    break;
                default:
                    // Player Gets 5 Scouts, 1 Gunship
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5); 
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 1);
                    State.PlayerNewShipsReceived += 6;
                    break;
            }
            ConfigData.HasSeenPreLevelIntro = false;
            ConfigData.HasSeenIntermission = false;

            // Unlock Free play mode
            if (!ConfigData.UserProgressData.IsHumanFreePlayUnlocked)
            {
                ConfigData.UserProgressData.IsHumanFreePlayUnlocked = true;
                Dialogue trainingRoomAlert = null;
                trainingRoomAlert = new Dialogue(Stage.DialoguePrefab, "New Game Mode!", "You've unlocked the Training Room!",
                    new List<string>() { "Ok" }, new List<UnityAction>() {  () => {
                    trainingRoomAlert.Hide();
                    Level2EndingDialogue();
                } });
                trainingRoomAlert.Show();
            }
            else
            {
                Level2EndingDialogue();
            }
                //ConfigData.UserProgressData.IsBeeFreePlayUnlocked = true;

                // Add Leafcutter, and Yellow Jacket to codex and add Carpenter Bee to visibility
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Leafcutter);
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.YellowJacket);

            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.CarpenterBee);

            ConfigData.UserProgressData.SetShipTypes();


            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();





        }
        public void Level2EndingDialogue()
        {
            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }
        public void Level3Ending()
        {
            Debug.Log("Level 3 complete");


            if (WinningSide == ConfigData.Configuration.UserSide) // Humans won
            {
                ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Factory, 5); // 5 Factories
                ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Factory);
                ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Factory);
                ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Factory);

                State.PlayerNewShipsReceived += 5;
            }
            else
            {
                // Skip the next level
                ConfigData.UserProgressData.AdvanceToNextLevel();

                // Account for additional Bee minerals mined
                int mineralsMined = 0;
                for (_save_i = 0; _save_i < AllSquads.Count; _save_i++)
                {
                    _save_savedSquad = AllSquads[_save_i];
                    if (_save_savedSquad.Side == ConfigData.Configuration.BeeSide)
                    {
                        _save_savedSquad.GetSquadShips().ForEach((ship) => // Don't need to check if ships are dead because if they are the minerals mined will have been set to zero
                        {
                            _save_fleetship = ship.GetFleetShip();
                            mineralsMined += _save_fleetship.MineralsMinedThisLevel;
                            _save_fleetship.MineralsMinedThisLevel = 0;
                        });
                    }

                }

                Debug.Log($"The Bees won and mined {mineralsMined} minerals");

                int bumblebees = mineralsMined / ConfigData.GetShipInfo(ConfigData.ShipTypes.Bumblebee).Tsv;
                mineralsMined = mineralsMined % ConfigData.GetShipInfo(ConfigData.ShipTypes.Bumblebee).Tsv;
                int leafcutters = mineralsMined / ConfigData.GetShipInfo(ConfigData.ShipTypes.Leafcutter).Tsv;
                mineralsMined = mineralsMined % ConfigData.GetShipInfo(ConfigData.ShipTypes.Leafcutter).Tsv;
                int hornets = mineralsMined / ConfigData.GetShipInfo(ConfigData.ShipTypes.Hornet).Tsv;
                mineralsMined = mineralsMined % ConfigData.GetShipInfo(ConfigData.ShipTypes.Hornet).Tsv;

                ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Hornet, hornets);
                ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Leafcutter, leafcutters);
                ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Bumblebee, bumblebees);

                Debug.Log($"Bees built {bumblebees} bumblebees, {leafcutters} leafcutters, and {hornets} hornets");
            }
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.CarpenterBee);
            ConfigData.UserProgressData.SetShipTypes();

            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }
        public void Level4Ending()
        {
            Debug.Log("Level 4 complete");

            for (_save_i = 0; _save_i < AllSquads.Count; _save_i++)
            {
                _save_savedSquad = AllSquads[_save_i];

                _save_savedSquad.GetSquadShips().ForEach((ship) =>
                {
                    _save_fleetship = ship.GetFleetShip();

                    Debug.Log($"{_save_fleetship.Name} has mined {_save_fleetship.MineralsMined} minerals in its lifetime. It has mined {_save_fleetship.MineralsMinedThisLevel} minerals this level");
                    _save_fleetship.MineralsMined += _save_fleetship.MineralsMinedThisLevel;
                    ConfigData.UserProgressData.MinedTSV += _save_fleetship.MineralsMinedThisLevel;
                    State.PlayerMineralsReceived += _save_fleetship.MineralsMinedThisLevel;
                    _save_fleetship.MineralsMinedThisLevel = 0;

                });

            }

            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }
        public void Level5Ending()
        {
            Debug.Log("Level 5 complete");

            if (WinningSide == ConfigData.Configuration.AISide)
            {
                // If the Bees won and you had to abandon the factories...
                CurrentShips.GetFleetShips().Where((f) => f.Type == ConfigData.ShipTypes.Factory).ToList().ForEach((f) =>
                {
                    f.IsDead = true;
                });
            }






            // Add carriers to the game
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Carrier, 1);
            SavedSquad carrier = CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Carrier, 1);
            State.PlayerNewShipsReceived += 1;

            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Carrier);

            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Bumblebee);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Carrier);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Carrier);
            ConfigData.UserProgressData.SetShipTypes();

            ConfigData.HasSeenPreLevelIntro = false;
            ConfigData.HasSeenIntermission = false;

            ConfigData.UserProgressData.HasMetAlejandraAndEmilia = true;

            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }
        public void Level6Ending()
        {
            Debug.Log("Level 6 complete");
            //return;

            // Skip next level if the player doesn't have factories or if they lost this level
            if (WinningSide == ConfigData.Configuration.AISide || !ConfigData.CurrentShips.HasShipsOfType(ConfigData.ShipTypes.Factory))
            {
                ConfigData.UserProgressData.AdvanceToNextLevel();
            }

            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Cruiser);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Cruiser);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Cruiser);
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Bumblebee);



            ConfigData.UserProgressData.SetShipTypes();

            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }
        public void Level7Ending()
        {
            Debug.Log("Level 7 complete");

            for (_save_i = 0; _save_i < AllSquads.Count; _save_i++)
            {
                _save_savedSquad = AllSquads[_save_i];

                _save_savedSquad.GetSquadShips().ForEach((ship) =>
                {
                    _save_fleetship = ship.GetFleetShip();

                    Debug.Log($"{_save_fleetship.Name} has mined {_save_fleetship.MineralsMined} minerals in its lifetime. It has mined {_save_fleetship.MineralsMinedThisLevel} minerals this level");
                    _save_fleetship.MineralsMined += _save_fleetship.MineralsMinedThisLevel;
                    ConfigData.UserProgressData.MinedTSV += _save_fleetship.MineralsMinedThisLevel;
                    State.PlayerMineralsReceived += _save_fleetship.MineralsMinedThisLevel;
                    _save_fleetship.MineralsMinedThisLevel = 0;

                });

            }

            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }
        public void Level8Ending()
        {
            Debug.Log("Level 8 complete");

            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Barge);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Barge);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Barge);
            ConfigData.UserProgressData.SetShipTypes();

            // Unlock stuff because you finished the campaign
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Flagship);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.FireBarge);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Carrier);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Factory);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.WarpGate);


            //ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Queen);
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Beehive);

            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Flagship);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.FireBarge);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Carrier);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Factory);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.WarpGate);

            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Flagship);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.FireBarge);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Carrier);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.WarpGate);

            //ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Queen);
            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Beehive);

            ConfigData.UserProgressData.SetShipTypes();

            ConfigData.UserProgressData.IsBeeFreePlayUnlocked = true;
            ConfigData.UserProgressData.IsHumanChallengeUnlocked = true;
            ConfigData.UserProgressData.IsFishTankUnlocked = true;

            //return;
            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }

        public void AddReinforcementsToHivemindCommandQueue()
        {
            State.GetSquadsBySide(ConfigData.Configuration.AISide).ForEach(s =>
            {
                if (!s.IsImmobile && !s.HasCommandQueue && !s.HasCommand)
                {
                    s.AddToCommandList();
                }
            });
        }

        /// <summary>
        /// A test function for executing random code after everything has been setup
        /// </summary>
        public void PostSetupTest()
        {
            Debug.Log("POST SETUP TEST HAS BEEN CALLED");
            Debug.LogWarning("POST SETUP TEST HAS BEEN CALLED");



            HumanTarget humanTarget = CreateHumanTarget(Vector2.zero);

            Debug.Log("Placed Human Target");


        }

        public HumanTarget CreateHumanTarget(Vector2 position)
        {
            SavedSquad HTSquad = new SavedSquad(Utilities.GetNegativeSavedSquadId(), ConfigData.Configuration.HumanSide, "Human Target \"Ship\"", Vector2.zero, false, false, DefaultShootingStrategy, UnsetColor, null);

            FleetShip fleetShip = new FleetShip(Utilities.GetNegativeFleetshipId(), ConfigData.ShipTypes.HumanTarget, false, false, 0, 0, 0, 0, 0, 0, 0);
            HTSquad.AddShipToSquad(new SquadShip(fleetShip.Id, fleetShip.Type, Vector2.zero));

            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() { HTSquad }, position, Vector2.zero, true);

            HumanTarget humanTarget = (HumanTarget)State.GetHumanShips().Where((s) => s.ShipType == ConfigData.ShipTypes.HumanTarget).FirstOrDefault();

            humanTarget.Squad.CanAcceptUserInput = false;
            //Debug.Log($"squad tab: {humanTarget.Squad.SquadTab}");
            Destroy(humanTarget.Squad.SquadTab.gameObject);
            humanTarget.Squad.HasSquadTab = false;
            if (humanTarget.HasUserFogOfWarVision)
            {
                Destroy(humanTarget.FogOfWarVision.gameObject);
                humanTarget.HasUserFogOfWarVision = false;
            }

            return humanTarget;
        }

        public void AddReinforcementSquads(List<SavedSquad> squads, Vector2 startingPosition, Vector2 nextPosition)
        {
            squads = squads.Where((squad) => squad != null && squad.GetSquadShips().Count > 0).ToList();
            for (int i = 0; i < squads.Count; i++)
            {
                if (squads[i].IsLoadedIntoLevel)
                {
                    //Debug.Log($"{squads[i]} has already been spawned onto the level, getting a new squad with the same composition");
                    squads[i] = CurrentShips.GetSquadByComposition(this, squads[i].GetSquadShips()[0].ShipType, squads[i].GetSquadShips().Count, true, true);
                }
                if (squads[i] != null)
                {
                    //Debug.Log($"Spawning squad onto level: {squads[i]}");
                    LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() { squads[i] }, startingPosition, nextPosition, true);
                }

            }
        }
    }
}