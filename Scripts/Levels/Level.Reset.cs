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
        private Ship[] _reset_ships;
        private float _reset_remainingHumanTsv, _reset_remainingHumanTSVPercentage, _reset_remainingBeeTsv, _reset_remainingBeeTSVPercentage;
        private Vector2 _reset_swap;
        readonly List<SpottedShip>[] _reset_spottedShips = new List<SpottedShip>[] { new List<SpottedShip>(), new List<SpottedShip>() };
        private int _reset_i;
        /// <summary>
        /// Used for Nueral Network training. Resets the level.
        /// </summary>
        /// <param name="isStepTimeout"></param>
        public void ResetLevel(bool isStepTimeout)
        {

            //Academy.Instance.StatsRecorder.Add("Episode Time", Seconds);

            //Debug.Log($"Reset level ({Seconds}), Unclamped Bee reward: {BeeCumaltiveReward}, Unclamped Human reward: {HumanCumulativeReward}");
            _reset_ships = State.GetShips().ToArray();

            State.GameOver = false;
            State.LevelEnded = false;
            _reset_remainingHumanTsv = State.GetTsvBySide(ConfigData.Configuration.HumanSide);
            _reset_remainingHumanTSVPercentage = _reset_remainingHumanTsv / State.InitialTsv[ConfigData.Configuration.HumanSide - 1];

            _reset_remainingBeeTsv = State.GetTsvBySide(ConfigData.Configuration.BeeSide);
            _reset_remainingBeeTSVPercentage = _reset_remainingBeeTsv / State.InitialTsv[ConfigData.Configuration.BeeSide - 1];

            //if (Utilities.RandomInt(10) > 7)
            //{
            //    UserStartingPosition = new Vector2(Utilities.RandomInt((int)MaxX * 2), Utilities.RandomInt((int)MaxY * 2)) - new Vector2(MaxX, MaxY);
            //    //UserStartingPosition = new Vector2(Utilities.RandomInt((int)MaxX), Utilities.RandomInt((int)MaxY)) - new Vector2(MaxX, 0);
            //}

            //UserStartingPosition = new Vector2(Utilities.RandomInt((int)MaxX * 2), UserStartingPosition.y*2) - new Vector2(MaxX, UserStartingPosition.y);

            //AIStartingPosition = new Vector2(Utilities.RandomInt((int)MaxX * 2), AIStartingPosition.y*2) - new Vector2(MaxX, AIStartingPosition.y);

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
                    //WinningSide = ConfigData.Configuration.BeeSide;
                    //Debug.Log($"Bees won! They had {remainingBeeTsv} / {state.InitialTsv[ConfigData.Configuration.BeeSide - 1]} remaining TSV or {remainingBeeTSVPercentage} x of the original.");

                    //AgentGroup.SetGroupReward(_reset_remainingBeeTSVPercentage);
                    //HumanAgentGroup.SetGroupReward(-_reset_remainingBeeTSVPercentage);
                    //BeeCumaltiveReward += 1f;
                    //HumanCumulativeReward = -1f;
                    //Debug.Log($"Bees won! Lost {LostBeeShips} bees, reward: {BeeCumaltiveReward}, {HumanCumulativeReward}");

                }
                else if (State.IsSideKilled(ConfigData.Configuration.BeeSide) && !State.IsSideKilled(ConfigData.Configuration.HumanSide))
                {
                    //Debug.Log($"Humans won! They had {remainingHumanTsv} / {state.InitialTsv[ConfigData.Configuration.HumanSide - 1]} remaining TSV or {remainingHumanTSVPercentage} x of the original.");

                    //AgentGroup.SetGroupReward(-_reset_remainingHumanTSVPercentage);
                    //HumanAgentGroup.SetGroupReward(_reset_remainingHumanTSVPercentage);
                    //BeeCumaltiveReward = -1f;
                    //HumanCumulativeReward += 1f;
                    //Debug.Log($"Humans won! Lost {LostBeeShips} bees, reward: {BeeCumaltiveReward}, {HumanCumulativeReward}");

                }
                else
                {
                    Debug.Log($"Both sides died! no on won!");
                    //AgentGroup.SetGroupReward(0);
                    //HumanAgentGroup.SetGroupReward(0);

                }
                //AgentGroup.EndGroupEpisode();
                //HumanAgentGroup.EndGroupEpisode();
            }
            Array.Clear(_reset_spottedShips, 0, 2);
            State.SpottedShips = _reset_spottedShips;


            for (_reset_i = 0; _reset_i < _reset_ships.Length; _reset_i++)
            {
                _reset_ships[_reset_i].EndKill();
            }
            SetupLevel();
            //Invoke(nameof(StartNew), .1f);
            //WinningSide = 0;
        }
        private List<LevelOptions> _setup_possibleLevels;
        private bool _hasSetTimeoutTimer;
        /// <summary>
        /// Called by both ResetLevel(), FinalizeSceneWithUserData(), and SaveAndEnd(). Prepares the LevelStage for a new level
        /// </summary>
        public void SetupLevel()
        {
            //StartTime = Time.realtimeSinceStartup;

            StartTime = Time.realtimeSinceStartup;
            if (ConfigData.ChooseRandomLevel)
            {
                _setup_possibleLevels = ConfigData.GetLevelData().GetLevels().Where((level) => level.Side == ConfigData.Configuration.AISide).ToList();
                CurrentLevelOptions = (LevelOptions)_setup_possibleLevels[Utilities.RandomInt(_setup_possibleLevels.Count)].Clone();
            }
            else if (ConfigData.LevelOptions == null)
            {
                CurrentLevelOptions = new LevelOptions(ConfigData.GetLevelData().GetNewId(), ConfigData.Configuration.AISide, $"Random Level #{ConfigData.GetLevelData().GetNewId()}");
                //Debug.Log($"Generated random level options with obstacle map index: {CurrentLevelOptions.ObstacleMapIndex}");
            }
            else
            {
                CurrentLevelOptions = (LevelOptions)ConfigData.LevelOptions.Clone();
            }
            //Debug.Log("CurrentLevelOptions.HasSquadActionBox " + CurrentLevelOptions.HasSquadActionBox);
            

            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                Destroy(Stage.UIElements[2]); // Scoreboard
                Stage.UIElements[3].GetComponent<HorizontalLayoutGroup>().padding.left = 0; // Move the squad tabs to the left since the scoreboard is gone
            }

            if (Stage.GeneratedSquadCountOverride > 0)
            {
                CurrentLevelOptions.EnemySquadGenerationCount = Stage.GeneratedSquadCountOverride;
            }
            //if (CurrentLevelOptions.EnemySquadGenerationCount > 0)
            //{
            //    Debug.Log($"Generating {CurrentLevelOptions.EnemySquadGenerationCount} enemy squads for this level before randomization");
            //    CurrentLevelOptions.EnemySquadGenerationCount = Utilities.RandomInt(CurrentLevelOptions.EnemySquadGenerationCount - Stage.GeneratedSquadCountMinimum) + 1 + Stage.GeneratedSquadCountMinimum;
            //    Debug.Log($"Generating {CurrentLevelOptions.EnemySquadGenerationCount} enemy squads for this level");
            //}

            // Reset any data that might have changed from a previous level
            ResetGameData();
            if (ConfigData.LevelOptions != null)
            {
                //Debug.Log(Utilities.ListToString(ConfigData.LevelOptions.ChosenSquads));
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
                Debug.Log(Utilities.ListToString(CurrentLevelOptions.ChosenSquads));
            }

            Debug.Log($"Game mode: {ConfigData.CurrentGameMode}");

            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
            {
                CurrentLevelOptions.HasSquadActionBox = true;
                Stage.Menus.ActionBox.Setup(Stage, this, Stage.EventSystem, ConfigData.Configuration.UserSide);

            }
            else if (CurrentLevelOptions.HasSquadActionBox)
            {
                Stage.Menus.ActionBox.Setup(Stage, this, Stage.EventSystem, ConfigData.Configuration.UserSide);
            }

            //Debug.Log($"Playing level: {CurrentLevelOptions.Name} with squads: {Utilities.ListToString(CurrentLevelOptions.ChosenSquads)}");
            // Check settings and config variables
            Stage.SetConfigOptionsAndOverrides(this);
            Debug.Log($"Generating {CurrentLevelOptions.EnemySquadGenerationCount} enemy squads for this level");

            //Debug.Log($"The human side is {ConfigData.Configuration.HumanSide}, the Bee side is {ConfigData.Configuration.BeeSide}, the AI side is {ConfigData.Configuration.AISide}, the user side is {ConfigData.Configuration.UserSide}");
            //Debug.Log($"The AI Starting position is {AIStartingPosition}, the user starting position is {UserStartingPosition}");

            //Debug.Log($"Chosen squads: {Utilities.ListToString(ConfigData.LevelOptions.ChosenSquads)}");
            if (Stage.HasRandomizedOptions)
            {
                RandomizeOptions();
            }
            else
            {
                Debug.Log($"The map does not have randomized options");
                CurrentLevelOptions.MapIndex = Stage.OverrideMapIndex;
                MapData = ConfigData.Maps[CurrentLevelOptions.MapIndex];
                Map = Stage.Pool.GetPooledMap(CurrentLevelOptions.MapIndex);


                //CurrentLevelOptions.Obstacles = Stage.OverrideObstacleMapIndex;
                //ObstacleMap = Stage.Pool.GetObstacleMapFromPool(CurrentLevelOptions.Obstacles);

                //if (CurrentLevelOptions.Obstacles > 0)
                //{
                //    HasObstacles = true;
                //}
                //else
                //{
                //    HasObstacles = false;
                //}
                HasObstacles = false;
            }
            SetupMapAndCamera();
            //Debug.Log(Utilities.ListToString(CurrentLevelOptions.ChosenSquads));
            SetupShips();
            if (!Stage.IsTraining)
            {
                MakeSaveLevel();
            }

            //AllSquads.AddRange(CurrentLevelOptions.EnemySquads);
            //AllSquads.AddRange(CurrentLevelOptions.ChosenSquads);
            //AllSquads.AddRange(CurrentLevelOptions.EnemyReinforcements);
            //AllSquads.AddRange(CurrentLevelOptions.FriendlyReinforcements);

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


            //CancelTimer(_checkTriggersTimer);
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign && !ConfigData.IsTestingLevel)
            {
                Stage.Menus.MissionStatus.SetActive(true);
                SetTriggers();
                _checkTriggersTimer.Reuse(.5f, CheckTriggers, true);
                AddTimer(_checkTriggersTimer);
            }
            else if(ConfigData.IsTestingLevel){
                Stage.RecordStats = false;
                Stage.IsPlayerControlling = true;
                Stage.Menus.SurrenderButtonLabel.text = "Leave";
                SelectFirstSquad();
                EasterEggTriggers();
            }
            else
            {
                //PostSetupTest();
                Stage.IsPlayerControlling = true;
                SelectFirstSquad();
                EasterEggTriggers();
            }



            if (Stage.ActivateAudio && Stage.PlayMusic)
            {
                Stage.Audio.SetupMusic();
            }

            SetupHivemind();



            //float end = (Time.realtimeSinceStartup - StartTime) * 1000; // seconds to milliseconds
            //Debug.Log($"It took {Math.Round(end, 2)} ms to set up the level and {Math.Round(Time.realtimeSinceStartup, 2)}s total time.");
        }
        /// <summary>
        /// When everything is ready for the first level, select the user's first squad
        /// </summary>
        public void SelectFirstSquad()
        {
            if (State.GetSquadsBySide(ConfigData.Configuration.UserSide).Count > 0 && State.GetSquadsBySide(ConfigData.Configuration.AISide).Count > 0 && !Stage.IsTraining)
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
            else if (!Stage.IsTraining && ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
            {
                Debug.Log($"User squads: {State.GetSquadsBySide(ConfigData.Configuration.UserSide).Count}, AI squads: {State.GetSquadsBySide(ConfigData.Configuration.AISide).Count}");
                Pause();
                Stage.Menus.NoAliveShipsAlert.SetActive(true);
            }
        }
        /// <summary>
        /// Cleans up the game state and requests and deletes the previous map
        /// </summary>
        public void ResetGameData()
        {
            //int count = 0;
            //GameObject.FindGameObjectsWithTag("Projectile").ToList().ForEach((projectileObject) =>
            //{
            //    Projectile projectile = projectileObject.GetComponent<Projectile>();
            //    try
            //    {
            //        if (!projectile.IsDead)
            //        {
            //            if (projectile.Type == ConfigData.ProjectileTypes.FireBargeExplosion)
            //            {
            //                projectile.Deactivate();
            //            }
            //            else
            //            {
            //                count++;
            //                Debug.Log($"{Name} ended with {projectile.Name} still alive");
            //                Debug.Log(projectile);
            //            }

            //        }
            //    }
            //    catch (Exception e)
            //    {
            //        Debug.Log(projectileObject.name);
            //        throw e;
            //    }

            //});
            //if (count > 0)
            //{
            //    Debug.LogError($"Found alive projectiles at end of level");
            //}
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
            HashSet<ServerRequest> staleResponseHistory = ConfigData.__PastServerRequests
                .Where(request => request is CommandRequest || request is MatchupStrategyRequest)
                .OrderByDescending(request => request.StartTime)
                .Take(StaleSquadRequestHistoryLimit)
                .ToHashSet();
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
