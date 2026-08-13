using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        /// <summary>
        /// Checks if any of the trigger conditions to load new ships for a level have been satisfied or not. For actual levels, this should probably be defined in some external file on a per level basis
        /// </summary>
        private void CheckTriggers()
        {
            EvaluateCampaignTriggers();
        }

        /// <summary>
        /// Advances the mission trigger graph once. Runtime uses the timer-backed
        /// CheckTriggers wrapper; deterministic scenario tools use this method so
        /// they do not need to wait for wall-clock trigger intervals.
        /// </summary>
        internal int EvaluateCampaignTriggers()
        {
            //Debug.Log($"Checking triggers");
            Triggers.AddRange(NextTriggers);
            NextTriggers.Clear();

            int triggeredCount = 0;
            Triggers.ForEach((trigger) =>
            {
                if (trigger.Conditional())
                {
                    trigger.Action();
                    triggeredCount++;
                }
            });
            for (int i = Triggers.Count - 1; i >= 0; i--)
            {
                if (Triggers[i].HasBeenTriggered)
                {
                    Triggers.RemoveAt(i);
                }
            }
            if (!HasContinuousTriggers && Triggers.Count == 0) {
                CancelTimer(_checkTriggersTimer);
                //CancelInvoke(nameof(CheckTriggers));
            }
            return triggeredCount;
        }
        private int _updateIndex;
        //public HashSet<long> _currentTimerIDs = new HashSet<long>(); // [debug]
        //private int _removeIndex;
        public void CancelTimer(ScaledTimer scaledTimer)
        {
            //_removeIndex = Timers.IndexOf(scaledTimer);
            //if (_removeIndex < 0)
            //{
            //    Debug.LogWarning($"Could not find {scaledTimer} in Timers and couldn't remove it");
            //}
            //else
            //{
            //    Timers.RemoveAt(_removeIndex);
            //}
            Timers.Remove(scaledTimer);
            //_currentTimerIDs.Remove(scaledTimer.Id);
            scaledTimer.IsCanceled = true;
            //Debug.Log($"Canceled {scaledTimer}");
        }
        public void AddTimer(ScaledTimer scaledTimer)
        {
            //Debug.Log($"Adding {scaledTimer}");
            //if (_currentTimerIDs.Contains(scaledTimer.Id)) // [debug]
            //{
            //    Debug.LogWarning($"Tried to add {scaledTimer} but it already exists in Timers. Adding anyways");
            //}
            //else
            //{
            //    Debug.Log($"Adding fresh {scaledTimer} to timers");
            //}
            Timers.Add(scaledTimer);
            //_currentTimerIDs.Add(scaledTimer.Id); // [debug]
        }
        private readonly List<ScaledTimer> _loopTimers = new List<ScaledTimer>();
        void Update()
        {
            //GameObject.Find("Rotated Point").transform.position = Utilities.RotatePointAroundPoint(GameObject.Find("Pivot").transform.position, __OriginalPosition, __RotationTest);
            //if (UseRLServer)
            //{
            //    RLSocket.Update();
            //}

            if (State.GameOver && !State.LevelEnded /*&& !State.CanShipsKeepMining()*/) // Turn this back on when the hivemind is better trained at mining
            {
                LevelOver();
                return;

            }
            if ((State.IsPaused || ConfigData.SocketManager.NetworkDisconnection.IsOpen || !IsLevelConnectedToServer) && !Stage.IsTraining)
            {
                if (IsPausedByTester && Stage.InputManager.HasPauseInput() && Time.realtimeSinceStartup - TimePaused > 1)
                {
                    IsPausedByTester = false;
                    TimePaused = Time.realtimeSinceStartup;
                    UnPause();
                }
            }
            else
            {
                if (!_hasSetTimeoutTimer)
                {
                    _hasSetTimeoutTimer = true;
                    if (Stage.TimeoutTime > 0)
                    {
                        //CancelTimer(_timeoutTimer);
                        _timeoutTimer.Reuse(Stage.TimeoutTime, LevelTimeOut);
                        AddTimer(_timeoutTimer);
                        //Debug.Log($"Added timeout timer:{_timeoutTimer}");
                        //Debug.Log(Utilities.ListToString(Timers));
                    }
                }
                if (HasObstacles)
                {
                    //Debug.Log($"Calling path finder update again");
                    Pathfinder.Update();
                }
                UpdateTimers();

            }



        }
        public void UpdateTimers()
        {
            if (Timers.Count > 0)
            {
                _loopTimers.Clear();
                _loopTimers.AddRange(Timers);

                for (_updateIndex = 0; _updateIndex < _loopTimers.Count; _updateIndex++)
                {
                    if (_loopTimers[_updateIndex].Update() && !_loopTimers[_updateIndex].IsRecurring && !_loopTimers[_updateIndex].IsCanceled)
                    {
                        CancelTimer(_loopTimers[_updateIndex]);
                    }
                }


            }
        }
        private double _timeDouble, _levelOver_fps, _levelOver_fups;
        private ScaledTimer _saveAndEndHalfSecond = new ScaledTimer();
        private ScaledTimer _saveAndEndFiveSecond = new ScaledTimer();

        private void RecordPlayerLevelResult()
        {
            // Automated training episodes are simulation state. They still need WinningSide for
            // command/outcome accounting, but they must never mutate or persist player progression.
            if (Stage.IsTraining)
            {
                DidUserWin = false;
                return;
            }

            if (WinningSide == ConfigData.Configuration.HumanSide)
            {
                if (ConfigData.CurrentGameMode == ConfigData.GameModes.FreePlay)
                {
                    ConfigData.UserProgressData.HumanFreePlayWins++;
                }
                else if (ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge)
                {
                    ConfigData.UserProgressData.HumanChallengeWins++;
                }
                else if (ConfigData.CurrentGameMode == ConfigData.GameModes.FishTank)
                {
                    ConfigData.UserProgressData.HumanFishTankWins++;
                }
            }
            else if (WinningSide == ConfigData.Configuration.BeeSide)
            {
                if (ConfigData.CurrentGameMode == ConfigData.GameModes.FreePlay)
                {
                    ConfigData.UserProgressData.BeeFreePlayWins++;
                }
                else if (ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge)
                {
                    ConfigData.UserProgressData.BeeChallengeWins++;
                }
                else if (ConfigData.CurrentGameMode == ConfigData.GameModes.FishTank)
                {
                    ConfigData.UserProgressData.BeeFishTankWins++;
                }
            }

            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge)
            {
                Stage.Menus.UpdateScore(ConfigData.UserProgressData.HumanChallengeWins, ConfigData.UserProgressData.BeeChallengeWins);
            }
            else if (ConfigData.CurrentGameMode == ConfigData.GameModes.FreePlay)
            {
                Stage.Menus.UpdateScore(ConfigData.UserProgressData.HumanFreePlayWins, ConfigData.UserProgressData.BeeFreePlayWins);
            }
            else if (ConfigData.CurrentGameMode == ConfigData.GameModes.FishTank)
            {
                Stage.Menus.UpdateScore(ConfigData.UserProgressData.HumanFishTankWins, ConfigData.UserProgressData.BeeFishTankWins);
            }

            DidUserWin = WinningSide == ConfigData.Configuration.UserSide;
            ConfigData.UserProgressData.Save();
        }

        /// <summary>
        /// Ends the level and marks the winner
        /// </summary>
        public void LevelOver() // [stats-method] [note]
        {
            if (!Stage.IsTrainingNueralNetwork)
            {
                Stage.DebugLogger.__LevelCompletes++;
                State.LevelEnded = true;
                Pause();
                //Debug.Log("LEVEL OVER!");

                State.GetAllSquads().ForEach((squad) =>
                {
                    if (squad.HasCommand)
                    {
                        squad.GetCommand().SetFinalize("Level ended");
                    }
                });

                _timeDouble = ConfigData.Stopwatch.Elapsed.TotalSeconds;
                _levelOver_fps = Time.frameCount / (_timeDouble > 0 ? _timeDouble : 0.0000000000000000001);
                _levelOver_fups = Stage.FixedUpdates / (_timeDouble > 0 ? _timeDouble : 0.0000000000000000001);
                ConfigData.__TotalLength += Time.realtimeSinceStartup - Stage.StartTime;
                //ConfigData.__AverageTimeOnQueue = ConfigData.__TotalTimeOnQueue / ConfigData.__TotalRequests;
                ConfigData.__AverageC2C = ConfigData.__TotalC2C / ConfigData.__TotalRequests;
                ConfigData.__AverageWireTime = ConfigData.__TotalWireTime / ConfigData.__TotalRequests;
                ConfigData.__AverageProcessingTime = ConfigData.__TotalProcessingTime / ConfigData.__TotalRequests;

                Debug.Log($"{$"fps: {_levelOver_fps}".PadRight(10).Substring(0, 10)}  {$"fups: {_levelOver_fups}".PadRight(10).Substring(0, 10)}     " +
                      $"{$"CPS: {Stage.DebugLogger.__HivemindCommands / ConfigData.Stopwatch.Elapsed.TotalSeconds}".PadRight(9).Substring(0, 9)}   " +
                      $"LTO: {Stage.DebugLogger.__LevelTimeouts} LC: {Stage.DebugLogger.__LevelCompletes} AveLT: {(int)ConfigData.__AverageLength}s || Hashes: {ConfigData.UsedHashes.Count}"
                );

                Debug.Log($"{$"C2C: {ConfigData.__AverageC2C}".PadRight(10).Substring(0, 10)}ms  {$"WT: {ConfigData.__AverageWireTime}".PadRight(10).Substring(0, 10)}ms     " +
                      $"{$"APT: {ConfigData.__AverageProcessingTime}".PadRight(9).Substring(0, 9)}ms " +
                      $"Resend%: {Math.Round((double)ConfigData.__TotalResends / ConfigData.__TotalRequests, 4) * 100}%"
                );

                if (State.IsSideKilled(ConfigData.Configuration.BeeSide) && !State.IsSideKilled(ConfigData.Configuration.HumanSide))
                {
                    //Debug.Log($"Humans won!");
                    WinningSide = ConfigData.Configuration.HumanSide;
                }
                else if (State.IsSideKilled(ConfigData.Configuration.HumanSide) && !State.IsSideKilled(ConfigData.Configuration.BeeSide))
                {
                    //Debug.Log($"Bees won!");
                    WinningSide = ConfigData.Configuration.BeeSide;
                }
                else if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
                {
                    if (State.IsSideKilled(ConfigData.Configuration.HumanSide) && State.IsSideKilled(ConfigData.Configuration.BeeSide))
                    {
                        Debug.LogError("Both sides are dead!");
                    }
                    else
                    {
                        Debug.LogError("Neither side is dead!");
                    }
                }

                RecordPlayerLevelResult();
                UnPause();
            }

            if (ActivateCollisionAsteroids)
            {
                CancelTimer(_asteroidSpawnTimer);
            }

            if (Stage.IsTrainingNueralNetwork)
            {
                
                ResetLevel(false);
            }
            else
            {
                if (Stage.IsTrainingHiveMind)
                {
                    SaveAndEnd(); // invoke immediately because training is happening

                }
                else
                {
                    if (State.FireBargeExplosions.Count > 0)
                    {
                        //Invoke(nameof(SaveAndEnd), 5f); // invoke after 5 seconds because the explosion should be fully seen
                        _saveAndEndFiveSecond.Reuse(5f, SaveAndEnd);
                        AddTimer(_saveAndEndFiveSecond);

                    }
                    else
                    {
                        _saveAndEndHalfSecond.Reuse(.5f, SaveAndEnd);
                        AddTimer(_saveAndEndHalfSecond);
                        //Invoke(nameof(SaveAndEnd), .5f); // inoke after half a second 
                    }

                }
            }
        }
    }
}
