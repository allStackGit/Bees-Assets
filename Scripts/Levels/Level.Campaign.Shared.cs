using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Server;
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
        private bool _lastShipRetreated;
        private bool _hasSeenCarrierIntroIfNeeded;

        private void SetTriggers()
        {
            Triggers.Clear();

            // Campaign progress is the authoritative mission identity. LevelOptions is mutable UI
            // handoff state (chosen squads, etc.) and must not be able to select a different
            // campaign script if it becomes stale or mismatched between scenes.
            int missionId = ConfigData.UserProgressData.GetCurrentLevel(
                ConfigData.Configuration.UserSide,
                ConfigData.GameModes.Campaign);

            if (CurrentLevelOptions != null && CurrentLevelOptions.Id != missionId)
            {
                Debug.LogError($"Campaign level options are #{CurrentLevelOptions.Id} ({CurrentLevelOptions.Name}) while progress is mission #{missionId}. Using campaign progress for mission setup.");
            }

            CampaignMissionCatalog.Configure(this, missionId);
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

            // A level can be closed before its initial SetupLevel response arrives. Retire every
            // level-owned setup/reconnect request before the scene disappears so a late response
            // cannot re-register this old Level in Socket.OpenLevels or reconnect a dead scene.
            ConfigData.Socket.StandingRequests.RemoveWhere(request =>
                (request is SetupLevelRequest setupRequest && ReferenceEquals(setupRequest.Level, this)) ||
                (request is ReconnectLevelRequest reconnectRequest && ReferenceEquals(reconnectRequest.Level, this)));
            ConfigData.Socket.OpenLevels.Remove(this);
            IsLevelConnectedToServer = false;

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
            squads = squads.Where(squad => squad != null && squad.GetSquadShips().Count > 0).ToList();
            for (int i = 0; i < squads.Count; i++)
            {
                if (squads[i].IsLoadedIntoLevel)
                {
                    squads[i] = CurrentShips.GetSquadByComposition(
                        this,
                        squads[i].GetSquadShips()[0].ShipType,
                        squads[i].GetSquadShips().Count,
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
