using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.UI_Components;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using static Assets.Scripts.ConfigData;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        private readonly ScaledTimer _dialogueTimer = new ScaledTimer();
        private readonly ScaledTimer _egg = new ScaledTimer();
        private readonly ScaledTimer _fishTank = new ScaledTimer();

        /// <summary>
        /// Mission-specific value accumulated by campaign objectives and converted into rewards.
        /// </summary>
        private int _questPoints;
        private LevelOptions _lastShipRetreatedLevelOptions;
        private bool _lastShipRetreated
        {
            get => CurrentLevelOptions != null && object.ReferenceEquals(_lastShipRetreatedLevelOptions, CurrentLevelOptions);
            set => _lastShipRetreatedLevelOptions = value ? CurrentLevelOptions : null;
        }
        private LevelOptions _carrierIntroCompletedLevelOptions;
        private bool _hasSeenCarrierIntroIfNeeded
        {
            get => CurrentLevelOptions != null && object.ReferenceEquals(_carrierIntroCompletedLevelOptions, CurrentLevelOptions);
            set => _carrierIntroCompletedLevelOptions = value ? CurrentLevelOptions : null;
        }

        private void SetTriggers()
        {
            // Level is reused between campaign missions/retries. A prior mission can end with
            // deferred NextTriggers still waiting on dialogue/UI state; never let those callbacks
            // enter the next mission's graph. Let the new mission opt back into continuous checks.
            Triggers.Clear();
            NextTriggers.Clear();
            HasContinuousTriggers = false;
            CampaignMissionCatalog.Configure(this, CurrentLevelOptions.Id);
        }

        public void EasterEggTriggers()
        {
            Stage.CutsceneManager.Setup(() => { });
            _egg.Reuse(10f, () =>
            {
                if (Utilities.RandomInt(100) == 36)
                {
                    Stage.CutsceneManager.PlaySingleDialogueLine(
                        Stage.CutsceneManager.EasterEggLines[Utilities.RandomInt(Stage.CutsceneManager.EasterEggLines.Count)]);
                }
            }, true);
            AddTimer(_egg);
        }

        public void FishTankTrigger()
        {
            if (ConfigData.UserProgressData.IsFishTankUnlocked)
            {
                return;
            }

            _fishTank.Reuse(60 * 30f, () =>
            {
                ConfigData.UserProgressData.IsFishTankUnlocked = true;
                ConfigData.UserProgressData.Save();
                Pause();

                Dialogue fishTankAlert = new Dialogue(
                    Stage.DialoguePrefab,
                    "Are you sure this is for you?",
                    "Perhaps you'd like to look at the fish tank instead?",
                    new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No },
                    new List<UnityAction>() { Stage.Menus.GoToFishTank });
                fishTankAlert.Show();
            });
            AddTimer(_fishTank);
        }

        public void CloseLevel()
        {
            // Capture the combat result before cleanup removes surviving ships. Campaign
            // dialogue triggers may continue running after CloseLevel; their outcome queries
            // must see the result that caused closure, not deaths caused by teardown itself.
            State.CaptureEliminationState();
            Pause();
            CancelTimer(_egg);
            CancelTimer(_fishTank);
            if (IsLevelSetupOnServer)
            {
                ConfigData.Socket.OpenLevels.Remove(this);
                IsLevelConnectedToServer = false;
            }
            Map.FogOfWar.SetActive(true);
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                Stage.Menus.MissionStatus.SetActive(false);
            }

            foreach (Ship ship in State.GetShips().ToList())
            {
                if (ship.HasUserFogOfWarVision)
                {
                    ship.FogOfWarVision.Kill(0, true);
                }
                ship.EndKill();
            }
            UnPause();
        }

        public void AddReinforcementsToHivemindCommandQueue()
        {
            State.GetSquadsBySide(ConfigData.Configuration.AISide).ForEach(squad =>
            {
                if (!squad.IsImmobile && !squad.HasCommandQueue && !squad.HasCommand)
                {
                    Debug.Log($"Adding squad {squad} to hivemind command list");
                    squad.AddToCommandList();
                }
            });
        }

        /// <summary>
        /// Test hook for executing code after the level is fully set up.
        /// </summary>
        public void PostSetupTest()
        {
            Debug.Log("POST SETUP TEST HAS BEEN CALLED");
            Debug.LogWarning("POST SETUP TEST HAS BEEN CALLED");
            CreateHumanTarget(Vector2.zero);
            Debug.Log("Placed Human Target");
        }

        public HumanTarget CreateHumanTarget(Vector2 position)
        {
            SavedSquad targetSquad = new SavedSquad(
                Utilities.GetNegativeSavedSquadId(),
                ConfigData.Configuration.HumanSide,
                "Human Target \"Ship\"",
                Vector2.zero,
                false,
                false,
                DefaultShootingStrategy,
                UnsetColor,
                null);

            FleetShip fleetShip = new FleetShip(
                Utilities.GetNegativeFleetshipId(),
                ConfigData.ShipTypes.HumanTarget,
                false,
                false,
                0, 0, 0, 0, 0, 0, 0);
            targetSquad.AddShipToSquad(new SquadShip(fleetShip.Id, fleetShip.Type, Vector2.zero));

            LevelConstructor.SpawnShipsAndSquads(new List<SavedSquad>() { targetSquad }, position, Vector2.zero, true);
            HumanTarget humanTarget = (HumanTarget)State.GetHumanShips()
                .FirstOrDefault(ship => ship.ShipType == ConfigData.ShipTypes.HumanTarget);

            humanTarget.Squad.CanAcceptUserInput = false;
            if (humanTarget.Squad.HasSquadTab && humanTarget.Squad.SquadTab != null)
            {
                Destroy(humanTarget.Squad.SquadTab.gameObject);
                humanTarget.Squad.HasSquadTab = false;
            }
            if (humanTarget.HasUserFogOfWarVision)
            {
                Destroy(humanTarget.FogOfWarVision.gameObject);
                humanTarget.HasUserFogOfWarVision = false;
            }
            return humanTarget;
        }

        public void AddReinforcementSquads(List<SavedSquad> squads, Vector2 startingPosition, Vector2 nextPosition)
        {
            squads = squads
                .Where(squad => squad != null && squad.GetAliveSquadShips().Count > 0)
                .ToList();
            for (int i = 0; i < squads.Count; i++)
            {
                List<SquadShip> aliveShips = squads[i].GetAliveSquadShips();
                if (squads[i].IsLoadedIntoLevel)
                {
                    squads[i] = CurrentShips.GetSquadByComposition(
                        this,
                        aliveShips[0].ShipType,
                        aliveShips.Count,
                        true,
                        true);
                }

                if (squads[i] != null)
                {
                    LevelConstructor.SpawnShipsAndSquads(
                        new List<SavedSquad>() { squads[i] },
                        startingPosition,
                        nextPosition,
                        true);
                }
            }
        }
    }
}
