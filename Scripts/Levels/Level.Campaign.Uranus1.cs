using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.UI_Components;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Assets.Scripts.ConfigData;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        public void Uranus1OnTheOffensive()
        {
            Stage.Menus.SetMissionStatus("Find and destroy all the Bees");
            HasContinuousTriggers = true;
            Stage.ActivateHiveMind = false;
            Stage.CutsceneManager.Setup(Uranus1Ending);

            AddReinforcementSquads(new List<SavedSquad>()
            {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
            }, new Vector2(250, 250), Vector2.zero);
            AddReinforcementSquads(new List<SavedSquad>()
            {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 2, true, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 1, false, true),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 1, false, true),
            }, Vector2.zero, Vector2.zero);
            AddReinforcementSquads(new List<SavedSquad>()
            {
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Bumblebee, 1),
                ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true),
            }, new Vector2(-250, -250), Vector2.zero);

            State.GetHumanShips().ForEach(ship =>
            {
                if (ship.ShipType != ConfigData.ShipTypes.Scout)
                {
                    return;
                }

                ship.ProximityCollider = Instantiate(
                    Stage.Prefabs.HumanProximityColliderPrefab,
                    Vector3.zero,
                    Quaternion.identity,
                    ship.transform).GetComponent<ProximityCollider>();
                ship.HasProximityCollider = true;
                ship.ProximityCollider.Create(ship);
                ship.ProximityCollider.transform.localPosition = Vector3.zero;
                ship.ProximityCollider.Activate();
            });

            Stage.Menus.TogglePausePanel();
            _dialogueTimer.Reuse(1.5f, () =>
                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(2, 4)));
            AddTimer(_dialogueTimer);

            NextTriggers.Add(new Trigger(
                () => Stage.CutsceneManager.HitDialogueBreak,
                SelectedCarrierTrigger,
                "Level 9 Carrier trigger"));

            NextTriggers.Add(new Trigger(
                () => _hasSeenCarrierIntroIfNeeded,
                () =>
                {
                    Stage.Menus.TogglePausePanel();
                    ScaledTimer cruiserTimer = new ScaledTimer(5f, () =>
                    {
                        ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Cruiser, 2);
                        SavedSquad cruiserSquad = CurrentShips.BuildNewSquad(
                            $"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}",
                            ConfigData.Configuration.HumanSide,
                            ShipTypes.Cruiser,
                            2);
                        LevelConstructor.SpawnShipsAndSquads(
                            new List<SavedSquad>() { cruiserSquad },
                            new Vector2(200, 200),
                            Vector2.zero,
                            true);

                        AddReinforcementSquads(new List<SavedSquad>()
                        {
                            ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true),
                        }, new Vector2(280, 200), Vector2.zero);
                        AddReinforcementsToHivemindCommandQueue();
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(6, 6));
                    });
                    AddTimer(cruiserTimer);

                    Ship bumblebee = State.GetBeeShips().First(ship => ship.ShipType == ConfigData.ShipTypes.Bumblebee);
                    NextTriggers.Add(new Trigger(
                        () => State.GetShips(ConfigData.Configuration.UserSide).Any(ship =>
                            ship.ShipsWithinRange.Contains(bumblebee) ||
                            ship.HasProximityCollider && ship.ProximityCollider.NearbyEnemyShips.Contains(bumblebee)),
                        () =>
                        {
                            Stage.CutsceneManager.PlaySingleDialogueLine(Stage.CutsceneManager.Uranus_OnTheOffensive[12]);
                            bumblebee.ShipsHit.Clear();
                            NextTriggers.Add(new Trigger(
                                () => bumblebee.ShipsHit.Count > 0,
                                () => Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(13, 3)),
                                "Level 9 Hit by Bumblebee"));
                            NextTriggers.Add(new Trigger(
                                () => bumblebee.IsDead && !(State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide)),
                                () => Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(16, 2)),
                                "Level 9 Killed Bumblebee"));
                        },
                        "Level 9 Discovering Bumblebee"));

                    NextTriggers.Add(new Trigger(
                        () => State.IsSideKilled(ConfigData.Configuration.UserSide) || State.IsSideKilled(ConfigData.Configuration.AISide),
                        () =>
                        {
                            WinningSide = State.IsSideKilled(ConfigData.Configuration.UserSide)
                                ? ConfigData.Configuration.AISide
                                : ConfigData.Configuration.UserSide;
                            CancelTimer(cruiserTimer);
                            CloseLevel();
                            if (WinningSide == ConfigData.Configuration.UserSide)
                            {
                                Stage.CutsceneManager.PlayDialogueSection(
                                    Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(
                                        18,
                                        ConfigData.CurrentShips.HasShipsOfType(ConfigData.ShipTypes.Factory) ? 20 : 17),
                                    true);
                            }
                            else
                            {
                                // The authored failure route skips On the Defensive gameplay and
                                // immediately uses that mission's post-mission exchange.
                                List<DialogueLine> failureDialogue = new List<DialogueLine>();
                                failureDialogue.AddRange(Stage.CutsceneManager.Uranus_OnTheOffensive.GetRange(38, 3));
                                failureDialogue.AddRange(Stage.CutsceneManager.Uranus_OnTheDefensive.GetRange(14, 3));
                                Stage.CutsceneManager.PlayDialogueSection(failureDialogue, true);
                            }
                        },
                        "Level 9 Ending dialogue"));
                },
                "Level 9 Starting"));
        }

        public void SelectedCarrierTrigger()
        {
            if (!ConfigData.UserProgressData.HasSeenCarrierIntro &&
                State.GetHumanShipTypes().Contains(ConfigData.ShipTypes.Carrier))
            {
                State.SelectSquads(State.GetSquadsBySide(ConfigData.Configuration.UserSide)
                    .Where(squad => squad.IsCarrierSquad)
                    .ToList());
                Stage.IsPlayerControlling = false;
                Tooltip basicTooltip = null;
                Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.SelectedCarrierSquad);

                NextTriggers.Add(new Trigger(
                    () => Stage.CutsceneManager.HitDialogueBreak,
                    () =>
                    {
                        basicTooltip = Instantiate(Stage.Menus.TooltipPrefab, Stage.Menus.UIOverlay.transform).GetComponent<Tooltip>();
                        basicTooltip.Show(
                            "To use the Strikers, simply right click on a target squad. The Strikers will fly towards their targets, drop their bombs, and return to the Carrier to reload. Right click anywhere else to move them and cancel the bombing run.",
                            true);
                        basicTooltip.Place(Vector2.zero, new Vector2(150, 300));

                        NextTriggers.Add(new Trigger(
                            () => !basicTooltip.gameObject.activeSelf,
                            () =>
                            {
                                Destroy(basicTooltip.gameObject);
                                ConfigData.UserProgressData.HasSeenCarrierIntro = true;
                                FinishCarrierIntroduction();
                            },
                            "Level 9-11 Carrier Squads Selected End"));
                    },
                    "Level 9-11 Carrier Squad Tooltip"));
            }
            else
            {
                FinishCarrierIntroduction();
            }
        }

        private void FinishCarrierIntroduction()
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
}
