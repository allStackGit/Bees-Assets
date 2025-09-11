using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
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

        /// <summary>
        /// This represents an amount of value that the player has accomplished for a given level like rescuing personnel that in turn translates to reinforcements or some other bonus
        /// </summary>
        private int _questPoints;
        private bool _someShipsHaveRetreated;
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
            Zone exitZone = null;
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
            Stage.CutsceneManager.Setup(() =>
            {
                Level0Ending();
            });
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
                    LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() {ConfigData.CurrentShips.GetSavedSquad(1) }, StartingPositions[ConfigData.Configuration.UserSide - 1] - new Vector2(0, 100), StartingPositions[ConfigData.Configuration.UserSide - 1] - new Vector2(0, 100), true);

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
                                            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() {ConfigData.CurrentShips.GetSavedSquad(3), ConfigData.CurrentShips.GetSavedSquad(4), ConfigData.CurrentShips.GetSavedSquad(5), ConfigData.CurrentShips.GetSavedSquad(6), ConfigData.CurrentShips.GetSavedSquad(7)}, StartingPositions[ConfigData.Configuration.AISide - 1] + new Vector2(0, 50), StartingPositions[ConfigData.Configuration.AISide - 1] + new Vector2(0, 50), true);


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
                                                            exitZone = exitBox.GetComponent<Zone>();

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
        public void Level1Triggers()
        {
            Debug.Log("Setting triggers for level 1");
            Stage.EnablePlayerControl();
            HasContinuousTriggers = true;
            GameObject basicTooltip = null;
            GameObject highlightTooltip = null;
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

            _dialogueTimer.Reuse(3, () =>
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
                    basicTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform);
                    basicTooltip.SetActive(true);
                    TMP_Text tooltipText = basicTooltip.transform.Find("Vertical/Message").GetComponent<TMP_Text>();
                    RectTransform tooltipRectTransformPosition = basicTooltip.GetComponent<RectTransform>();
                    RectTransform tooltipRectTransformSize = basicTooltip.transform.GetChild(0).GetComponent<RectTransform>();

                    tooltipText.text = "Select a squad with the left mouse button.";
                    tooltipRectTransformPosition.localPosition = new Vector2(200, 0);

                    highlightTooltip = Instantiate(Stage.Menus.HighlightTooltipPrefab, Map.transform);
                    highlightTooltip.SetActive(true);
                    highlightTooltip.transform.position = scoutSquad.GetPosition();
                    highlightTooltip.transform.localScale = new Vector2(scoutSquad.GetWidth() + 2, scoutSquad.GetHeight() + 2);

                    NextTriggers.Add(new Trigger(() =>
                        {
                            return scoutSquad.IsSelected;
                        },
                        () =>
                        {
                            highlightTooltip.SetActive(false);
                            basicTooltip.SetActive(false);
                            Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Reinforcements.GetRange(2, 4));

                            NextTriggers.Add(new Trigger(() =>
                                {
                                    return Stage.CutsceneManager.HitDialogueBreak;
                                },
                                () =>
                                {
                                    tooltipText.text = "Here are different settings for your ship.You can determine your squad’s flight pattern and shooting strategies here. Take some time to familiarize yourself with these options.";
                                    tooltipRectTransformSize.sizeDelta = new Vector2(150, 225);
                                    tooltipRectTransformPosition.localPosition = new Vector2(-225, -150);
                                    basicTooltip.SetActive(true);


                                    GameObject pointerA = Instantiate(Stage.Menus.PointerArrow, Stage.Menus.UIOverlay.transform);
                                    rectTransform = pointerA.GetComponent<RectTransform>();
                                    rectTransform.localPosition = new Vector2(-482, -255);
                                    rectTransform.eulerAngles = new Vector3(0, 0, 170);
                                    pointerA.SetActive(true);

                                    GameObject pointerB = Instantiate(Stage.Menus.PointerArrow, Stage.Menus.UIOverlay.transform);
                                    rectTransform = pointerB.GetComponent<RectTransform>();
                                    rectTransform.localPosition = new Vector2(-380, -340);
                                    rectTransform.eulerAngles = new Vector3(0, 0, 90);
                                    rectTransform.localScale = new Vector2(0.25f, 0.5f);
                                    pointerB.SetActive(true);

                                    GameObject spaceBarMessage = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform);
                                    spaceBarMessage.SetActive(true);
                                    spaceBarMessage.transform.Find("Vertical/Message").GetComponent<TMP_Text>().text = "Press and hold the space bar to continue";

                                    NextTriggers.Add(new Trigger(() =>
                                        {
                                            return Input.GetKey(KeyCode.Space);
                                        },
                                        () =>
                                        {
                                            Destroy(pointerA);
                                            Destroy(pointerB);
                                            Destroy(spaceBarMessage);

                                            GameObject squadNumberHighlight = Instantiate(Stage.Menus.UIHighlightTooltipPrefab, Stage.Menus.UIOverlay.transform);
                                            squadNumberHighlight.SetActive(true);
                                            squadNumberHighlight.transform.localPosition = new Vector2(-610, 370);
                                            squadNumberHighlight.transform.localScale = new Vector2(150, 30);
                                            squadNumberHighlight.transform.SetAsFirstSibling();

                                            tooltipText.text = "You can also select squads with the number hotkeys on your keyboard. These are displayed at the top of the screen.";
                                            tooltipRectTransformSize.sizeDelta = new Vector2(150, 150);
                                            tooltipRectTransformPosition.localPosition = new Vector2(-550, 300);

                                            GameObject multiSelectMessage = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform);
                                            multiSelectMessage.SetActive(true);
                                            multiSelectMessage.transform.Find("Vertical/Message").GetComponent<TMP_Text>().text = "If you need to select multiple squads, click and drag the mouse over the squads.";
                                            multiSelectMessage.transform.GetChild(0).GetComponent<RectTransform>().sizeDelta = new Vector2(150, 100);
                                            multiSelectMessage.transform.GetComponent<RectTransform>().localPosition = new Vector2(-200, 0);

                                            GameObject rangeMessage = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform);
                                            rangeMessage.SetActive(true);
                                            rangeMessage.transform.Find("Vertical/Message").GetComponent<TMP_Text>().text = "Your ships with weapons will automatically shoot at any enemies in range. You can view your selected ships’ range at any time by holding <b>R</b>.";
                                            rangeMessage.transform.GetChild(0).GetComponent<RectTransform>().sizeDelta = new Vector2(150, 150);
                                            rangeMessage.transform.GetComponent<RectTransform>().localPosition = new Vector2(200, 0);

                                            frigateSquad.CanAcceptUserInput = true;
                                            gunshipSquad.CanAcceptUserInput = true;
                                            dreadnoughtSquad.CanAcceptUserInput = true;

                                            NextTriggers.Add(new Trigger(() =>
                                                {
                                                    return frigateSquad.IsSelected || gunshipSquad.IsSelected || dreadnoughtSquad.IsSelected;
                                                },
                                                () =>
                                                {
                                                    basicTooltip.SetActive(false);
                                                    Destroy(squadNumberHighlight);
                                                    Destroy(multiSelectMessage);
                                                    Destroy(rangeMessage);

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

                                                                    // Create bee reinforcement squads. One Squad of 3 hornets, one squad of 2 wasps
                                                                    LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() { ConfigData.CurrentShips.GetSavedSquad(4), ConfigData.CurrentShips.GetSavedSquad(5), ConfigData.CurrentShips.GetSavedSquad(7) }, new Vector2(-295, -195), new Vector2(-225, -155), true);

                                                                    AddReinforcementsToHivemindCommandQueue();

                                                                    NextTriggers.Add(new Trigger(() =>
                                                                        {
                                                                            return State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide);
                                                                        },
                                                                        () =>
                                                                        {
                                                                            if (State.IsSideKilled(ConfigData.Configuration.AISide))
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
            Debug.Log("Setting triggers for level 2");
            Stage.EnablePlayerControl();
            HasContinuousTriggers = true;

            int personnelLost = 0;
            int personnelEvacuated = 0;

            ScaledTimer clock = new ScaledTimer();


            // Every time the trigger is checked, 1 person is evacuated. If 15 people are lost then the level ends. If 15 people aren't lost then the level ends after 5 minutes and roughly 60 people are evacuated. The ship will have a ton of health and no weapons but in theory should still be physical so that projectiles can hit it and explode and when it loses health, personnel are lost. It should have sufficient TSV so that it's a very valuable target for the Bees even if they only do a tiny bit of damage relative to its significant health.

            // Prevent the Hivemind from giving commands
            Stage.ActivateHiveMind = false;

            // Start the dialogue
            Stage.CutsceneManager.Setup(() =>
            {
                Level2Ending();
            });

            _dialogueTimer.Reuse(3, () =>
            {
                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(1, 4));
            });
            AddTimer(_dialogueTimer);

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
                    plutoLines.Add(Stage.CutsceneManager.PlutoLines_BluerPastures[10]);
                    Stage.CutsceneManager.PlayDialogueSection(plutoLines);

                    NextTriggers.Add(new Trigger(() =>
                        {
                            return Stage.CutsceneManager.HitDialogueBreak;
                        },
                        () =>
                        {
                            Destroy(plutoCircle);
                            float endTime = Time.time + 300; // 5 minutes to complete the level
                            float timeLeft = endTime - Time.time;

                            int minutesLeft;
                            int secondsLeft;

                            TMP_Text clockText = Stage.Menus.Clock.transform.GetChild(0).GetComponent<TMP_Text>();
                            TMP_Text counterText = Stage.Menus.Counter.transform.GetChild(0).GetComponent<TMP_Text>();
                            Stage.Menus.Counter.transform.GetChild(1).GetComponent<TMP_Text>().text = "Evacuated";


                            Stage.Menus.Clock.SetActive(true);
                            Stage.Menus.Counter.SetActive(true);

                            // Create target on pluto
                            HumanTarget humanTarget = CreateHumanTarget(new Vector2(68, -28));
                            //humanTarget.transform.localScale = new Vector2(80, 80);
                            Destroy(humanTarget.FogOfWarVision.gameObject);

                            clock.Reuse(1f, () =>
                            {
                                timeLeft = endTime - Time.time;
                                minutesLeft = Mathf.FloorToInt(timeLeft / 60f);
                                secondsLeft = Mathf.FloorToInt(timeLeft % 60f);

                                personnelLost = (humanTarget.MaxHealth - humanTarget.Health) / 200;

                                if (timeLeft <= 0 || personnelLost >= 15)
                                {
                                    _questPoints = personnelEvacuated;
                                    CancelTimer(clock);
                                    if (personnelLost >= 15)
                                    {
                                        Debug.Log($"Level over, too many personnel lost: {personnelLost}");

                                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(16, 4), true);
                                    }
                                    else
                                    {
                                        if (personnelLost == 0)
                                        {
                                            Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(20, 6), true);
                                        }
                                        else
                                        {
                                            Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(20, 4), true);
                                        }

                                    }
                                }
                                else
                                {
                                    // Every 5 seconds, evacuate 1 person
                                    if (Mathf.FloorToInt(timeLeft) % 5 == 0)
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
                            firstSquads.AddRange(new List<SavedSquad>() { ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4) });


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
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4) 
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
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4) 
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
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2) 
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
                                    Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.PlutoLines_BluerPastures[12]);
                                },
                                "Level 2 Leafcutter splitter shot")
                            );

                            NextTriggers.Add(new Trigger(() =>
                                {
                                    return State.GetAllEnemyShips(ConfigData.Configuration.HumanSide).Any((s) => s.ShipType == ConfigData.ShipTypes.YellowJacket && s.IsDead && ((YellowJacket)s).ContactedShip != null);
                                },
                                () =>
                                {
                                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.PlutoLines_BluerPastures.GetRange(13, 3));
                                },
                                "Level 2 Yellow Jacket Hitting ship")
                            );


                        },
                        "Level 2 Starting combat")
                    );
                },
                "Level 2 Showing Pluto outline and dialogue")
            );
        }
        public void Level3Triggers()
        {

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



            // Start the dialogue
            _dialogueTimer.Reuse(3, () =>
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
                    // Spawn the Bees
                    AddReinforcementSquads(new List<SavedSquad>() { ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 2), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 2) }, miningAsteroids[0].transform.localPosition, Vector2.zero);

                    AddReinforcementSquads(new List<SavedSquad>() { ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 1), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 1) }, miningAsteroids[1].transform.localPosition, Vector2.zero);

                    AddReinforcementSquads(new List<SavedSquad>() { ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 1), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 1) }, miningAsteroids[2].transform.localPosition, Vector2.zero);

                    AddReinforcementSquads(new List<SavedSquad>() { ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 3), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 1), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 2) }, miningAsteroids[3].transform.localPosition, Vector2.zero);

                    AddReinforcementSquads(new List<SavedSquad>() { ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.CarpenterBee, 1), ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 2) }, miningAsteroids[4].transform.localPosition, Vector2.zero);


                    // Enable player control and hive mind
                    Stage.EnablePlayerControl();
                    Stage.ActivateHiveMind = true;
                    SetupHivemind();

                    // Set the dialogue trigger
                    NextTriggers.Add(new Trigger(() =>
                        {
                            return State.GetBeeShips().Any((s) => s.ShipType == ConfigData.ShipTypes.CarpenterBee && s.Health < s.MaxHealth && ((CarpenterBee)s).ShipAnimation.activeSelf);
                        },
                        () =>
                        {
                            Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_SeizeTheMeans.GetRange(8, 4));
                        },
                        "Level 3 Carpenter Bee Dialogue")
                    );

                    // Set level end trigger
                    NextTriggers.Add(new Trigger(() =>
                        {
                            return State.IsSideKilled(ConfigData.Configuration.HumanSide) || State.IsSideKilled(ConfigData.Configuration.BeeSide);
                        },
                        () =>
                        {
                            if (State.IsSideKilled(ConfigData.Configuration.BeeSide))
                            {
                                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_SeizeTheMeans[12]);

                                WinningSide = ConfigData.Configuration.HumanSide;

                                // End kill all the human ships
                                State.GetShips(ConfigData.Configuration.HumanSide).ForEach((ship) =>
                                {
                                    ship.EndKill();
                                    Stage.IsPlayerControlling = false;
                                });


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
            _dialogueTimer.Reuse(3, () =>
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

                    Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_OfProduction.GetRange(3, 3));

                    NextTriggers.Add(new Trigger(() =>
                        {
                            return Stage.CutsceneManager.HitDialogueBreak;
                        },
                        () =>
                        {
                            Stage.EnablePlayerControl();
                            // Hide the mining locations on the mini map
                            for (int i = 0; i < minimapIcons.Length; i++)
                            {
                                Destroy(minimapIcons[i]);
                            }

                            // Show the mining tooltips
                            GameObject basicTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform);
                            basicTooltip.SetActive(true);
                             basicTooltip.transform.Find("Vertical/Message").GetComponent<TMP_Text>().text = "To mine, first select your factory ships, then right click on ore-rich asteroids. Once the factory ship arrives, it will automatically begin collecting materials."; ;
                            basicTooltip.GetComponent<RectTransform>().localPosition = new Vector2(0, 0);
                            basicTooltip.transform.GetChild(0).GetComponent<RectTransform>().sizeDelta = new Vector2(150, 200);


                            GameObject endMissionTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform);
                            endMissionTooltip.SetActive(true);
                            endMissionTooltip.transform.Find("Vertical/Message").GetComponent<TMP_Text>().text = "You can retreat from the mission at any time. Click on the Retreat button in the menu and an area will appear on the left side of the map where your ships can retreat to safety.";
                            endMissionTooltip.GetComponent<RectTransform>().localPosition = new Vector2(-400, 0);
                            endMissionTooltip.transform.GetChild(0).GetComponent<RectTransform>().sizeDelta = new Vector2(150, 200);


                            // Show button to end mission
                            Stage.Menus.RetreatButton.SetActive(true);
                            Stage.Menus.RetreatButton.GetComponent<Button>().onClick.AddListener(SetRetreatForLevel4);


                            ScaledTimer hideTooltips = new ScaledTimer(30f, () =>
                            {
                                Destroy(basicTooltip);
                                Destroy(endMissionTooltip);
                            });
                            AddTimer(hideTooltips);

                            // Set timers for Bee waves

                            // Wave 1 @ 2 Minutes
                            // 2 Squads of 2 Honeybees
                            // 2 Squads of 4 Hornets

                            ScaledTimer wave1 = new ScaledTimer(120f, () =>
                            {
                                Debug.Log($"Spawning Bee reinforcements wave 1");

                                AddReinforcementSquads(new List<SavedSquad>() {
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4)
                                }, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);

                                AddReinforcementsToHivemindCommandQueue();
                                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[6]);
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
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4)
                                }, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);

                                AddReinforcementsToHivemindCommandQueue();
                                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[Utilities.RandomInt(5) + 8]);
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
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4),
                                }, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);

                                AddReinforcementsToHivemindCommandQueue();
                                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[7]);
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
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4),
                                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4),
                                });

                                AddReinforcementSquads(reinforcementSquads, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);

                                AddReinforcementsToHivemindCommandQueue();
                                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[Utilities.RandomInt(5) + 8]);

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
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2),
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6),
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6),
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8),
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8),
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4),
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4),
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4),
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 6),
                                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 6),
                                    };

                                    for (int i = 0; i < waveCount - 5; i++)
                                    {
                                        reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6));
                                        reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6));

                                        reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8));

                                        reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4));
                                        reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4));

                                        reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 6));
                                        reinforcementSquads.Add(ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 6));
                                    }

                                    AddReinforcementSquads(reinforcementSquads, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);

                                    AddReinforcementsToHivemindCommandQueue();
                                    Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[Utilities.RandomInt(5) + 8]);
                                }, true);

                                AddTimer(wave5);
                            });

                            AddTimer(wave4);




                            // Set dialogue triggers
                            NextTriggers.Add(new Trigger(() =>
                                {
                                    return State.IsSideKilled(ConfigData.Configuration.UserSide) && !_someShipsHaveRetreated;
                                },
                                () =>
                                {
                                    CancelTimer(wave5);
                                    Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[14], true);
                                },
                                "Level 4 Player losing dialogue")
                            );

                            NextTriggers.Add(new Trigger(() =>
                                {
                                    return State.IsSideKilled(ConfigData.Configuration.UserSide) && _someShipsHaveRetreated;
                                },
                               () =>
                               {
                                   CancelTimer(wave5);
                                   Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[15], true);
                               },
                               "Level 4 Player retreating dialogue")
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
                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Neptune_OfProduction[13]);
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
                    _someShipsHaveRetreated = true;
                    ship.EndKill();
                }
            };
        }
        public void Level5Triggers()
        {
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
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4),
            }, CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition);
            AddReinforcementsToHivemindCommandQueue();

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
                    if (State.IsSideKilled(ConfigData.Configuration.AISide)) // Player won
                    {
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_PressingForward.GetRange(1, 4), true);

                    }
                    else
                    {
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Neptune_PressingForward.GetRange(5, 2), true);
                    }
                },
                "Level 5 Ending dialogue")
            );

        }
        public void Level6Triggers()
        {
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
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4),
            }, new Vector2(250, 250), Vector2.zero);

            AddReinforcementSquads(new List<SavedSquad>() {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 1),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 1),
            }, Vector2.zero, Vector2.zero);

            AddReinforcementSquads(new List<SavedSquad>() {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Bumblebee, 1),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4),
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
            _dialogueTimer.Reuse(3, () =>
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
                    Stage.ActivateHiveMind = true;
                    SetupHivemind();
                    Stage.EnablePlayerControl();

                    // Spawn the cruiser after a little bit and show dialogue. Have hornets or something attacking it
                    ScaledTimer cruiserTimer = new ScaledTimer(5f, () =>
                    {
                        ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Cruiser, 3);
                        SavedSquad cruiserSquad = CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Cruiser, 3);
                        cruiserSquad.StartingPosition = new Vector2(-33, -2); // Reposition it so that it works with the squad maker

                        LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() { cruiserSquad }, new Vector2(200, 200), Vector2.zero, true);

                        // Add hornets attacking cruisers
                        AddReinforcementSquads(new List<SavedSquad>() {
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8),
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
                                    return bumblebee.IsDead;
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
                            if (State.IsSideKilled(ConfigData.Configuration.AISide)) // Player won
                            {
                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(18, ConfigData.CurrentShips.GetFirstAvailableShipOfType(ConfigData.ShipTypes.Factory) != null ? 20 : 17), true);
                                WinningSide = ConfigData.Configuration.UserSide;
                            }
                            else
                            {
                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(38, 3), true);
                                WinningSide = ConfigData.Configuration.AISide;
                            }

                        },
                        "Level 6 Ending dialogue")
                    );
                },
                "Level 6 Starting")
            );


            
        }
        public void Level7Triggers()
        {

            // Add 3 squads of 2 Bumblebees
            for (int i = 0; i < 3; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Bumblebee, 2);
            }

            Stage.EnablePlayerControl();
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

            if (ConfigData.CurrentShips.GetFirstAvailableShipOfType(ConfigData.ShipTypes.Cruiser) != null)
            {
                dialogueLines.Add(Stage.CutsceneManager.Uranus_OnTheDefensive[9]);
            }
            if (ConfigData.UserProgressData.HasMetAlejandraAndEmilia)
            {
                dialogueLines.AddRange(Stage.CutsceneManager.Uranus_OnTheDefensive.GetRange(10, 2));
            }

            // Show the mining tooltips
            GameObject endMissionTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform);
            endMissionTooltip.SetActive(true);
            endMissionTooltip.transform.Find("Vertical/Message").GetComponent<TMP_Text>().text = "You can retreat from the mission at any time. Click on the Retreat button in the menu and an area will appear on the left side of the map where your ships can retreat to safety.";
            endMissionTooltip.GetComponent<RectTransform>().localPosition = new Vector2(-400, 0);
            endMissionTooltip.transform.GetChild(0).GetComponent<RectTransform>().sizeDelta = new Vector2(150, 200);


            // Show button to end mission
            Stage.Menus.RetreatButton.SetActive(true);
            Stage.Menus.RetreatButton.GetComponent<Button>().onClick.AddListener(SetRetreatForLevel7);


            ScaledTimer hideTooltips = new ScaledTimer(20f, () =>
            {
                Destroy(endMissionTooltip);
            });
            AddTimer(hideTooltips);

            // Set timers for Bee waves

            // Wave 1 @ 3 Minutes
            // 1 Squad of 1 Leafcutter
            // 1 Squad of 2 Honeybees
            // 1 Squad of 2 Wasps
            // 2 Squads of 4 Hornets

            ScaledTimer wave1 = new ScaledTimer(180f, () =>
            {
                Debug.Log($"Spawning Bee reinforcements wave 1");

                AddReinforcementSquads(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 1),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true),
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true)
                }, upperPositions[0], upperPositions[1]);
                AddReinforcementsToHivemindCommandQueue();

                Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheDefensive[1]);
            });

            AddTimer(wave1);

            // Wave 2 @ 5 Minutes
            // 1 Squad of 1 Leafcutter
            // 1 Squad of 2 Honeybees
            // 1 Squad of 2 Wasps
            // 2 Squads of 4 Hornets

            ScaledTimer wave2 = new ScaledTimer(300f, () =>
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

            // Wave 3 @ 7 Minutes
            // 2 Squads of 4 Yellow Jackets
            // 1 Squad of 2 Honeybees
            // 1 Squad of 4 Wasps
            // 1 Squad of 4 Hornets

            ScaledTimer wave3 = new ScaledTimer(420, () =>
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

            // Wave 4 @ 9 Minutes
            // 2 Squads of 6 Yellow Jackets
            // 2 Squads of 2 Honeybees
            // 2 Squads of 4 Wasps
            // 1 Squad of 4 Leafcutters

            ScaledTimer wave4 = new ScaledTimer(540, () =>
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


            // Wave 5 @ 10 Minutes
            // 2 Squads of 2 Honeybees
            // 2 Squads of 6 Wasps
            // 2 Squads of 8 Hornets
            // 1 Squad of 4 Yellow Jackets
            // 2 Squads of 4 Leafcutters
            // 1 Squad of 2 Bumblebees
            // All squads not previously killed
            ScaledTimer wave5 = new ScaledTimer(600, () =>
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

            // Wave 6 @ 11 Minutes
            // 2 Squads of 2 Honeybees
            // 2 Squads of 6 Wasps
            // 2 Squads of 8 Hornets
            // 1 Squad of 4 Yellow Jackets
            // 2 Squads of 4 Leafcutters
            // 2 Squads of 2 Bumblebees
            ScaledTimer wave6 = new ScaledTimer(660, () =>
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
            });

            AddTimer(wave6);

            // Level ending
            NextTriggers.Add(new Trigger(() =>
                {
                    return State.IsSideKilled(ConfigData.Configuration.UserSide);
                },
                () =>
                {
                    if (!_someShipsHaveRetreated)
                    {
                        Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheDefensive[13]);
                    }
                    else
                    {
                        Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheDefensive[17]);
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
                    _someShipsHaveRetreated = true;
                    ship.EndKill();
                }
            };
        }
        public void Level8Triggers()
        {

            Stage.EnablePlayerControl();
            HasContinuousTriggers = true;

            Stage.CutsceneManager.Setup(() =>
            {
                Level8Ending();
            });

            Vector2[] spawnPositions = new Vector2[] { CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0), CurrentLevelOptions.AIStartingPosition };

            // Add the three barges and spawn them
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Barge, 3);
            SavedSquad barges = CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Barge, 3);
            barges.StartingPosition = new Vector2(-33, -2); // Reposition it so that it works with the squad maker

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

            bargeSquad.RunCommandQueue();


            // Add hornets attacking barges
            AddReinforcementSquads(new List<SavedSquad>() {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
            }, new Vector2(250, 250), Vector2.zero);
            AddReinforcementsToHivemindCommandQueue();


            //intial dialogue
            Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_ANewThreat.GetRange(1, 5));

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

            // dialogue triggers
            NextTriggers.Add(new Trigger(() =>
                {
                    return bargeSquad.GetShips().Count < 3;
                },
               () =>
               {
                   Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_ANewThreat[6]);
               },
               "Level 8 Barge Killed")
            );
            NextTriggers.Add(new Trigger(() =>
                {
                    return bargeSquad.GetShips().Any((s) => ((Barge)s).HasStartedCharging);
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
                   if (bargeSquad.IsDead) // Bees Won
                   {
                       Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_ANewThreat.GetRange(21, 12));
                   }
                   else
                   {
                       Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_ANewThreat.GetRange(10, 20), true);
                       CancelTimer(reinforcements);
                   }
               },
               "Level 8 Ending")
           );
        }

        public void Level0Ending()
        {
            Debug.Log("Level complete!");

            // Add new human ships to the game
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 1); // 1 Gunship
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 2); // 2 Frigates
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Dreadnought, 1); // 2 Dreadnoughts

            // Add new bee ships to the game
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Honeybee, 1); // 1 Honeybee

            // Add new ships to the human's available ships
            SavedSquad gunshipSquad = ConfigData.CurrentShips.GetSavedSquad(1);
            FleetShip gunship = CurrentShips.GetFirstAvailableShipOfType(ConfigData.ShipTypes.Gunship);
            gunshipSquad.AddShipToSquad(new SquadShip(gunship.Id, gunship.Type, Vector2.zero));
            gunshipSquad.AutoRepositionSquad();

            // Starting Dreadnought squad #8
            SavedSquad squad = CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Dreadnought, 2);
            squad.StartingPosition = new Vector2(-33, -2); // Reposition it so that it works with the squad maker

            // Starting Frigate squad #9
            squad = CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Frigate, 3);
            squad.StartingPosition = new Vector2(-33, -2); // Reposition it so that it works with the squad maker

            // Starting Honeybee squad #10
            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Honeybee, 1);


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
        public void Level1Ending()
        {
            Debug.Log("Level 1 complete!");

            // Add new human ships to the game, 10 Scouts, 2 Gunships, 2 Frigates, 1 Dreadnought
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 10);
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 2); 
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 2); 
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Dreadnought, 1); // 1 Dreadnought

            // Create the rest of the Bee fleet
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Honeybee, 28);
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Hornet, 124);
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Wasp, 99);
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.YellowJacket, 76);
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Leafcutter, 72);
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.CarpenterBee, 6);
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Bumblebee, 7);

            //ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Bumblebee, 15); // 15 Bumblebees
            //ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Queen, 2); // 2 Queens
            //ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Beehive, 5); // 5 Bee hives

            // Create the new Bee squads

            // 5 Honeybee squads of 2
            for (int i = 0; i < 5; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Honeybee, 2);
            }

            // 5 Wasp sqauds of 4
            for (int i = 0; i < 5; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Wasp, 4);
            }

            // 6 Hornet squads of 4
            for (int i = 0; i < 6; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Hornet, 4);
            }

            // 4 Yellow Jacket squads of 4
            for (int i = 0; i < 4; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.YellowJacket, 4);
            }

            // 2 Leafcutter squads of 2
            for (int i = 0; i < 2; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Leafcutter, 2);
            }



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
            Stage.Menus.ShowLevelContinueDialogue();

        }
        public void Level2Ending()
        {
            Debug.Log("Level 2 complete!");

            // Add 4 squads of 1 carpenter bees
            for (int i = 0; i < 5; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.CarpenterBee, 1);
            }

            // Add one squad of two carpenter bees
            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.CarpenterBee, 2);

            // Add 2 squads of 2 wasps and 1 squad of 3 wasps
            for (int i = 0; i < 2; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Wasp, 2);
            }

            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Wasp, 3);

            // Add one squad of 2 leafcutters
            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Leafcutter, 2);

            // Add three squads of 2 hornets and 2 squads of 1 hornet
            for (int i = 0; i < 3; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Hornet, 2);
            }
            for (int i = 0; i < 2; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Hornet, 1);
            }

            switch (_questPoints)
            {
                case 60:
                    // Player 10 Scouts, 8 Gunships, 4 Frigates, and 4 Dreadnoughts
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5); // 5 Scouts
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 8); // 8 Gunships
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 4); // 4 Frigates
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Dreadnought, 4); // 4 Dreadnoughts
                    break;
                case > 50:
                    // Player gets 5 Scouts, 6 Gunships, 2 Frigates, and 2 Dreadnoughts
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5); // 5 Scouts
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 6); // 6 Gunships
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 2); // 2 Frigates
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Dreadnought, 2); // 2 Dreadnoughts
                    break;
                case > 35:
                    // Player gets 5 Scouts, 4 Gunships, 2 Frigates
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5); // 5 Scouts
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 4); // 4 Gunships
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 2); // 2 Frigates
                    break;
                case > 15:
                    // Player gets 5 Scouts, 4 Gunships
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5); // 5 Scouts
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 4); // 4 Gunships
                    break;
                default:
                    // Player Gets 5 Scouts, 1 Gunship
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5); // 5 Scouts
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 1); // 1 Gunships
                    break;
            }

            // Unlock Free play mode
            ConfigData.UserProgressData.IsHumanFreePlayUnlocked = true;
            ConfigData.UserProgressData.IsBeeFreePlayUnlocked = true;

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


            State.GameOver = true;
            Stage.Menus.ShowLevelContinueDialogue();
        }
        public void Level3Ending()
        {
            Debug.Log("Level 3 complete");


            if (WinningSide == ConfigData.Configuration.HumanSide) // Humans won
            {
                ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Factory, 5); // 5 Factories
                ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Factory);
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

            // 9 Honeybee squads of 2
            for (int i = 0; i < 9; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Honeybee, 2);
            }

            // 3 Wasp sqauds of 4
            for (int i = 0; i < 5; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Wasp, 4);
            }

            // 10 Wasp sqauds of 6
            for (int i = 0; i < 10; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Wasp, 6);
            }

            // 4 Hornet squads of 4
            for (int i = 0; i < 4; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Hornet, 4);
            }

            // 2 Hornet squads of 6
            for (int i = 0; i < 2; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Hornet, 6);
            }

            // 8 Hornet squads of 8
            for (int i = 0; i < 7; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Hornet, 8);
            }

            // 15 Yellow Jacket squads of 4
            for (int i = 0; i < 15; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.YellowJacket, 4);
            }

            // 2 Leafcutter squads of 4
            for (int i = 0; i < 2; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Leafcutter, 4);
            }

            // 8 Leafcutter squads of 6
            for (int i = 0; i < 8; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Leafcutter, 6);
            }


            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Stage.Menus.ShowLevelContinueDialogue();
        }
        public void Level4Ending()
        {
            Debug.Log("Level 4 complete");

            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Stage.Menus.ShowLevelContinueDialogue();
        }
        public void Level5Ending()
        {
            Debug.Log("Level 5 complete");

            // Add new Bee squads
            // Add 2 squads of 2 Wasps
            for (int i = 0; i < 2; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Wasp, 2);
            }

            // Add 3 squads of 4 Hornets
            for (int i = 0; i < 3; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Hornet, 4);
            }

            // Add 2 squads of 1 Leafcutter
            for (int i = 0; i < 2; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Leafcutter, 1);
            }

            // Add one squad of one Bumblebee
            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Bumblebee, 1);

            // Add 1 squad of 4 Yellow Jackets
            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.YellowJacket, 4);

            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Bumblebee);
            ConfigData.UserProgressData.SetShipTypes();

            ConfigData.UserProgressData.HasMetAlejandraAndEmilia = true;

            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Stage.Menus.ShowLevelContinueDialogue();
        }
        public void Level6Ending()
        {
            Debug.Log("Level 6 complete");
            //return;

            // Skip next level if the player doesn't have factories or if they lost this level
            if (WinningSide == ConfigData.Configuration.AISide || ConfigData.CurrentShips.GetFirstAvailableShipOfType(ConfigData.ShipTypes.Factory) == null)
            {
                ConfigData.UserProgressData.AdvanceToNextLevel();
            }

            // Add 3 squads of 2 Bumblebees
            for (int i = 0; i < 3; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}", ConfigData.Configuration.BeeSide, ShipTypes.Bumblebee, 2);
            }

            // Add Honeybee, Frigate, and Dreadnought to the codex and visibility
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Bumblebee);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Cruiser);


            ConfigData.UserProgressData.SetShipTypes();

            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Stage.Menus.ShowLevelContinueDialogue();
        }
        public void Level7Ending()
        {
            Debug.Log("Level 7 complete");

            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Stage.Menus.ShowLevelContinueDialogue();
        }
        public void Level8Ending()
        {
            Debug.Log("Level 8 complete");

            //return;
            // Advance to next level in campaign
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();


            State.GameOver = true;
            Stage.Menus.ShowLevelContinueDialogue();
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

            FleetShip fleetShip = new FleetShip(Utilities.GetNegativeFleetshipId(), ConfigData.ShipTypes.HumanTarget, false, true, false, 0, 0, 0, 0, 0, 0, 0);
            HTSquad.AddShipToSquad(new SquadShip(fleetShip.Id, fleetShip.Type, Vector2.zero));

            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() { HTSquad }, position, Vector2.zero, true);

            HumanTarget humanTarget = (HumanTarget)State.GetHumanShips().Where((s) => s.ShipType == ConfigData.ShipTypes.HumanTarget).FirstOrDefault();

            humanTarget.Squad.CanAcceptUserInput = false;
            Destroy(humanTarget.Squad.SquadTab);

            return humanTarget;
        }

        public void AddReinforcementSquads(List<SavedSquad> squads, Vector2 startingPosition, Vector2 nextPosition)
        {
            for (int i = 0; i < squads.Count; i++)
            {
                if (squads[i].IsLoadedIntoLevel)
                {
                    //Debug.Log($"{squads[i]} has already been spawned onto the level, getting a new squad with the same composition");
                    squads[i] = CurrentShips.GetSquadByComposition(this, squads[i].GetSquadShips()[0].ShipType, squads[i].GetSquadShips().Count);
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