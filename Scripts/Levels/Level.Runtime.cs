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
            Triggers.AddRange(NextTriggers);
            NextTriggers.Clear();

            int triggeredCount = 0;
            for (int i = 0; i < Triggers.Count; i++)
            {
                LevelTrigger trigger = Triggers[i];
                if (trigger.Conditional())
                {
                    trigger.Action();
                    triggeredCount++;
                }
            }
            for (int i = Triggers.Count - 1; i >= 0; i--)
            {
                if (Triggers[i].HasBeenTriggered)
                {
                    Triggers.RemoveAt(i);
                }
            }
            if (!HasContinuousTriggers && Triggers.Count == 0)
            {
                CancelTimer(_checkTriggersTimer);
            }
            return triggeredCount;
        }

        private int _updateIndex;
        private int _timerCollectionVersion;
        private int _loopTimerVersion = -1;

        public void CancelTimer(ScaledTimer scaledTimer)
        {
            if (Timers.Remove(scaledTimer))
            {
                _timerCollectionVersion++;
            }
            scaledTimer.IsCanceled = true;
        }

        public void AddTimer(ScaledTimer scaledTimer)
        {
            Timers.Add(scaledTimer);
            _timerCollectionVersion++;
        }

        private readonly List<ScaledTimer> _loopTimers = new List<ScaledTimer>();
        void Update()
        {
            if (State.GameOver && !State.LevelEnded)
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
                        _timeoutTimer.Reuse(Stage.TimeoutTime, LevelTimeOut);
                        AddTimer(_timeoutTimer);
                    }
                }
                if (HasObstacles)
                {
                    Pathfinder.Update();
                }
                UpdateTimers();
            }
        }

        public void UpdateTimers()
        {
            if (Timers.Count == 0)
            {
                return;
            }

            if (_loopTimerVersion != _timerCollectionVersion)
            {
                _loopTimers.Clear();
                _loopTimers.AddRange(Timers);
                _loopTimerVersion = _timerCollectionVersion;
            }

            for (_updateIndex = 0; _updateIndex < _loopTimers.Count; _updateIndex++)
            {
                ScaledTimer timer = _loopTimers[_updateIndex];
                if (timer.Update() && !timer.IsRecurring && !timer.IsCanceled)
                {
                    CancelTimer(timer);
                }
            }
        }

        private double _timeDouble, _levelOver_fps, _levelOver_fups;
        private ScaledTimer _saveAndEndHalfSecond = new ScaledTimer();
        private ScaledTimer _saveAndEndFiveSecond = new ScaledTimer();

        private void RecordPlayerLevelResult()
        {
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
        public void LevelOver()
        {
            if (!Stage.IsTrainingNueralNetwork)
            {
                Stage.DebugLogger.__LevelCompletes++;
                State.LevelEnded = true;
                Pause();

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
                    WinningSide = ConfigData.Configuration.HumanSide;
                }
                else if (State.IsSideKilled(ConfigData.Configuration.HumanSide) && !State.IsSideKilled(ConfigData.Configuration.BeeSide))
                {
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
                    SaveAndEnd();
                }
                else
                {
                    if (State.FireBargeExplosions.Count > 0)
                    {
                        _saveAndEndFiveSecond.Reuse(5f, SaveAndEnd);
                        AddTimer(_saveAndEndFiveSecond);
                    }
                    else
                    {
                        _saveAndEndHalfSecond.Reuse(.5f, SaveAndEnd);
                        AddTimer(_saveAndEndHalfSecond);
                    }
                }
            }
        }
    }
}
