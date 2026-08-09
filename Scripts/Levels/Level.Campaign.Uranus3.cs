using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Assets.Scripts.ConfigData;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        public void Uranus3ANewThreat()
        {
            Stage.Menus.SetMissionStatus("Rescue the Barges and destroy all the Bees!");
            Stage.ActivateHiveMind = false;
            HasContinuousTriggers = true;
            Stage.CutsceneManager.Setup(Uranus3Ending);

            Vector2[] spawnPositions =
            {
                CurrentLevelOptions.AIStartingPosition - new Vector2(-100, 0),
                CurrentLevelOptions.AIStartingPosition
            };

            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Barge, 3);
            SavedSquad barges = CurrentShips.BuildNewSquad(
                $"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}",
                ConfigData.Configuration.HumanSide,
                ShipTypes.Barge,
                3);
            LevelConstructor.SpawnShipsAndSquads(
                new List<SavedSquad>() { barges },
                new Vector2(300, 300),
                Vector2.zero,
                true);
            Squad bargeSquad = State.GetHumanShips().Find(ship => ship.ShipType == ConfigData.ShipTypes.Barge).Squad;

            bargeSquad.HasCommandQueue = true;
            bargeSquad.CommandQueueEmptyAction = () => bargeSquad.HasCommandQueue = false;
            MoveToPoint moveToPoint = (MoveToPoint)Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToPoint);
            moveToPoint.Setup(bargeSquad, false, null, null, Vector2.zero);
            bargeSquad.CommandQueue.Enqueue(moveToPoint);

            AddReinforcementSquads(new List<SavedSquad>()
            {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
            }, new Vector2(250, 250), Vector2.zero);

            if (State.GetSquadsBySide(ConfigData.Configuration.AISide).Count == 0)
            {
                CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Hornet, 2);
                CurrentShips.BuildNewSquad(
                    $"Squad #{ConfigData.UserProgressData.BeeCampaignSavedSquadNumber++}",
                    ConfigData.Configuration.BeeSide,
                    ShipTypes.Hornet,
                    2);
                AddReinforcementSquads(new List<SavedSquad>()
                {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 2, true, true),
                }, new Vector2(250, 250), Vector2.zero);
            }

            Stage.Menus.TogglePausePanel();
            _dialogueTimer.Reuse(1.5f, () =>
                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_ANewThreat.GetRange(1, 5)));
            AddTimer(_dialogueTimer);

            NextTriggers.Add(new Trigger(
                () => Stage.CutsceneManager.HitDialogueBreak,
                SelectedCarrierTrigger,
                "Level 11 Carrier trigger"));

            NextTriggers.Add(new Trigger(
                () => _hasSeenCarrierIntroIfNeeded,
                () =>
                {
                    Stage.Menus.TogglePausePanel();
                    bargeSquad.RunCommandQueue();

                    ScaledTimer reinforcements = new ScaledTimer(60f, () =>
                    {
                        AddReinforcementSquads(new List<SavedSquad>()
                        {
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2, true, true),
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
                        }, spawnPositions[0], spawnPositions[1]);
                        AddReinforcementsToHivemindCommandQueue();
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_ANewThreat.GetRange(7, 2));
                    });
                    AddTimer(reinforcements);

                    NextTriggers.Add(new Trigger(
                        () => bargeSquad.GetShips().Count < 3 &&
                            !(State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide)),
                        () => Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_ANewThreat[6]),
                        "Level 11 Barge Killed"));

                    NextTriggers.Add(new Trigger(
                        () => bargeSquad.GetShips().Any(ship => ((Barge)ship).HasStartedCharging) &&
                            !(State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide)),
                        () => Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_ANewThreat[9]),
                        "Level 11 Barge Charge"));

                    NextTriggers.Add(new Trigger(
                        () => State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide),
                        () =>
                        {
                            bool isBargeSquadDead = bargeSquad.IsDead;
                            CloseLevel();
                            if (isBargeSquadDead)
                            {
                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_ANewThreat.GetRange(30, 12), true);
                            }
                            else
                            {
                                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_ANewThreat.GetRange(10, 20), true);
                                CancelTimer(reinforcements);
                            }
                        },
                        "Level 11 Ending"));
                },
                "Level 11 Starting"));
        }
    }
}
