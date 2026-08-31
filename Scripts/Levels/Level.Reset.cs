using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        private const int StaleSquadRequestHistoryLimit = 4096;
        private readonly List<Ship> _resetShips = new List<Ship>();
        private float _reset_remainingHumanTsv, _reset_remainingHumanTSVPercentage, _reset_remainingBeeTsv, _reset_remainingBeeTSVPercentage;
        private Vector2 _reset_swap;
        private int _reset_i;
        /// <summary>
        /// Used for Nueral Network training. Resets the level.
        /// </summary>
        /// <param name="isStepTimeout"></param>
        public void ResetLevel(bool isStepTimeout)
        {
            _resetShips.Clear();
            _resetShips.AddRange(State.GetShips());

            State.GameOver = false;
            State.LevelEnded = false;
            _reset_remainingHumanTsv = State.GetTsvBySide(ConfigData.Configuration.HumanSide);
            _reset_remainingHumanTSVPercentage = _reset_remainingHumanTsv / State.InitialTsv[ConfigData.Configuration.HumanSide - 1];

            _reset_remainingBeeTsv = State.GetTsvBySide(ConfigData.Configuration.BeeSide);
            _reset_remainingBeeTSVPercentage = _reset_remainingBeeTsv / State.InitialTsv[ConfigData.Configuration.BeeSide - 1];

            Map.AIStartingPosition = new Vector2(UnityEngine.Random.Range(MinX, MaxX), UnityEngine.Random.Range(0, MaxY));
            Map.UserStartingPosition = new Vector2(UnityEngine.Random.Range(MinX, MaxX), UnityEngine.Random.Range(MinY, 0));

            if (Utilities.CoinToss())
            {
                _reset_swap = Map.UserStartingPosition;
                Map.UserStartingPosition = Map.AIStartingPosition;
                Map.AIStartingPosition = _reset_swap;
            }

            StartingPositions[ConfigData.Configuration.AISide - 1] = Map.AIStartingPosition;
            StartingPositions[ConfigData.Configuration.UserSide - 1] = Map.UserStartingPosition;

            if (!isStepTimeout)
            {
                if (State.IsSideKilled(ConfigData.Configuration.HumanSide) && !State.IsSideKilled(ConfigData.Configuration.BeeSide))
                {
                }
                else if (State.IsSideKilled(ConfigData.Configuration.BeeSide) && !State.IsSideKilled(ConfigData.Configuration.HumanSide))
                {
                }
                else if (!Stage.IsTraining)
                {
                    Debug.Log("Both sides died! no on won!");
                }
            }

            // RemoveShip prunes spotting entries during ordinary lifecycle teardown. Reset already
            // owns the entire old episode, so clear the existing containers once before killing
            // the snapshot instead of nulling/recreating them or repeatedly scanning old sightings.
            for (_reset_i = 0; _reset_i < State.SpottedShips.Length; _reset_i++)
            {
                State.SpottedShips[_reset_i]?.Clear();
            }

            for (_reset_i = 0; _reset_i < _resetShips.Count; _reset_i++)
            {
                _resetShips[_reset_i].EndKill();
            }
            SetupLevel();
        }

        private readonly List<LevelOptions> _setup_possibleLevels = new List<LevelOptions>();
        private bool _hasSetTimeoutTimer;
        /// <summary>
        /// Called by both ResetLevel(), FinalizeSceneWithUserData(), and SaveAndEnd(). Prepares the LevelStage for a new level
        /// </summary>
        public void SetupLevel()
        {
            StartTime = Time.realtimeSinceStartup;
            if (ConfigData.ChooseRandomLevel)
            {
                _setup_possibleLevels.Clear();
                List<LevelOptions> levels = ConfigData.GetLevelData().GetLevels();
                for (int i = 0; i < levels.Count; i++)
                {
                    LevelOptions level = levels[i];
                    if (level.Side == ConfigData.Configuration.AISide)
                    {
                        _setup_possibleLevels.Add(level);
                    }
                }
                CurrentLevelOptions = (LevelOptions)_setup_possibleLevels[Utilities.RandomInt(_setup_possibleLevels.Count)].Clone();
            }
            else if (ConfigData.LevelOptions == null)
            {
                CurrentLevelOptions = new LevelOptions(ConfigData.GetLevelData().GetNewId(), ConfigData.Configuration.AISide, $"Random Level #{ConfigData.GetLevelData().GetNewId()}");
            }
            else
            {
                CurrentLevelOptions = (LevelOptions)ConfigData.LevelOptions.Clone();
            }

            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                Destroy(Stage.UIElements[2]);
                Stage.UIElements[3].GetComponent<HorizontalLayoutGroup>().padding.left = 0;
            }

            if (Stage.GeneratedSquadCountOverride > 0)
            {
                CurrentLevelOptions.EnemySquadGenerationCount = Stage.GeneratedSquadCountOverride;
            }

            ResetGameData();
            if (ConfigData.LevelOptions != null)
            {
                ConfigData.LevelOptions.ChosenSquads.ForEach((savedSquad) =>
                {
                    if (savedSquad.HasBeenSavedToStorage)
                    {
                        CurrentLevelOptions.ChosenSquads.Add(ConfigData.CurrentShips.GetSavedSquad(savedSquad.Id));
                    }
                    else
                    {
                        CurrentLevelOptions.ChosenSquads.Add(savedSquad);
                    }
                });
                if (!Stage.IsTraining)
                {
                    Debug.Log(Utilities.ListToString(CurrentLevelOptions.ChosenSquads));
                }
            }

            if (!Stage.IsTraining)
            {
                Debug.Log($"Game mode: {ConfigData.CurrentGameMode}");

                // The action box is player UI. Automated training destroys/omits that hierarchy,
                // so neither initial setup nor episode resets may touch its serialized references.
                if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
                {
                    CurrentLevelOptions.HasSquadActionBox = true;
                    Stage.Menus.ActionBox.Setup(Stage, this, Stage.EventSystem, ConfigData.Configuration.UserSide);
                }
                else if (CurrentLevelOptions.HasSquadActionBox)
                {
                    Stage.Menus.ActionBox.Setup(Stage, this, Stage.EventSystem, ConfigData.Configuration.UserSide);
                }
            }

            StageConfigOptions.Apply(Stage, this);
            if (!Stage.IsTraining)
            {
                Debug.Log($"Generating {CurrentLevelOptions.EnemySquadGenerationCount} enemy squads for this level");
            }

            if (Stage.HasRandomizedOptions)
            {
                RandomizeOptions();
            }
            else
            {
                if (!Stage.IsTraining)
                {
                    Debug.Log("The map does not have randomized options");
                }
                CurrentLevelOptions.MapIndex = Stage.OverrideMapIndex;
                MapData = ConfigData.Maps[CurrentLevelOptions.MapIndex];
                Map = Stage.Pool.GetPooledMap(CurrentLevelOptions.MapIndex);
                HasObstacles = false;
            }
            SetupMapAndCamera();
            SetupShips();
            if (!Stage.IsTraining)
            {
                MakeSaveLevel();
            }

            if (ActivateMining && ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
            {
                SpawnMiningAsteroids();
            }
            if (ActivateFogOfWar && HasPlayer)
            {
                Map.FogOfWar.SetActive(true);
            }
            else
            {
                Map.FogOfWar.SetActive(false);
            }

            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign && !ConfigData.IsTestingLevel)
            {
                Stage.Menus.MissionStatus.SetActive(true);
                SetTriggers();
                _checkTriggersTimer.Reuse(.5f, CheckTriggers, true);
                AddTimer(_checkTriggersTimer);
            }
            else if(ConfigData.IsTestingLevel)
            {
                Stage.RecordStats = false;
                Stage.IsPlayerControlling = true;
                Stage.Menus.SurrenderButtonLabel.text = "Leave";
                SelectFirstSquad();
                EasterEggTriggers();
            }
            else
            {
                Stage.IsPlayerControlling = true;
                SelectFirstSquad();
                EasterEggTriggers();
            }

            if (Stage.ActivateAudio && Stage.PlayMusic)
            {
                Stage.Audio.SetupMusic();
            }

            SetupHivemind();
        }

        /// <summary>
        /// When everything is ready for the first level, select the user's first squad
        /// </summary>
        public void SelectFirstSquad()
        {
            if (Stage.IsTraining)
            {
                return;
            }

            int userSquadCount = 0;
            int aiSquadCount = 0;
            List<Squad> squads = State.Squads;
            for (int i = 0; i < squads.Count; i++)
            {
                Squad candidate = squads[i];
                if (candidate.IsDead)
                {
                    continue;
                }
                if (candidate.Side == ConfigData.Configuration.UserSide)
                {
                    userSquadCount++;
                }
                else if (candidate.Side == ConfigData.Configuration.AISide)
                {
                    aiSquadCount++;
                }
            }

            if (userSquadCount > 0 && aiSquadCount > 0)
            {
                Squad squad = State.GetSquadByNumber(ConfigData.Configuration.UserSide, 1);
                State.SelectSquad(squad);
                if (!Stage.UnlockCamera)
                {
                    Vector2 squadPosition = squad.GetPosition();
                    Stage.Camera.transform.position = new Vector3(squadPosition.x, squadPosition.y, -10) + Get3DPosition();
                    Stage.InputManager.MaintainScrollBoundary();
                }
            }
            else if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
            {
                Debug.Log($"User squads: {userSquadCount}, AI squads: {aiSquadCount}");
                Pause();
                Stage.Menus.NoAliveShipsAlert.SetActive(true);
            }
        }

        /// <summary>
        /// Cleans up the game state and requests and deletes the previous map
        /// </summary>
        public void ResetGameData()
        {
            ReconcilePersistedFleetForSetup();
            ResetRuntimeState(ConfigData.Socket.HandledRequests);
            PruneServerRequestHistoryForReset();

            if (Map != null)
            {
                Stage.Pool.ReturnMapToPool(Map);
            }
        }

        private void PruneServerRequestHistoryForReset()
        {
            if (Stage.WatchServerRequests)
            {
                return;
            }

            // Late strategy responses can still be queued on the process-wide socket after the
            // owning Level has ended. SocketResponseLifecycleGuard uses the original request to
            // prove that such a response belonged to a retired Squad lifecycle. Preserve only the
            // newest bounded set of request types needed for that ownership check; discard all
            // unrelated debug history as before so normal play does not accumulate it indefinitely.
            List<ServerRequest> staleResponseHistory = ConfigData.__PastServerRequests
                .Where(request => request is CommandRequest || request is MatchupStrategyRequest)
                .OrderByDescending(request => request.StartTime)
                .Take(StaleSquadRequestHistoryLimit)
                .ToList();
            ConfigData.__PastServerRequests.IntersectWith(staleResponseHistory);
        }

        private void ReconcilePersistedFleetForSetup()
        {
            // Fully random training squads are transient and do not own the player's saved fleet.
            // Never repair/save persisted squad membership as a side effect of an automated episode.
            if (Stage.IsTraining)
            {
                return;
            }

            ConfigData.CurrentShips.ReplaceDeadSquadShips(ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign);
        }

        private void ResetRuntimeState(HashSet<long> allHandledRequests)
        {
            Timers.Clear();
            _hasSetTimeoutTimer = false;
            State.ResetState();
            Seconds = 0;
            RemoveHandledRequests(allHandledRequests, HandledRequests);
            AllSquads.Clear();
            CurrentLevelOptions.ChosenSquads.Clear();
        }

        private static void RemoveHandledRequests(
            HashSet<long> allHandledRequests,
            HashSet<long> levelHandledRequests)
        {
            allHandledRequests.ExceptWith(levelHandledRequests);
            levelHandledRequests.Clear();
        }
    }
}
