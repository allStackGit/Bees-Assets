using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.UI_Components;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        /// <summary>
        /// Resets the level for Hivemind training
        /// </summary>
        private void LevelTimeOut()
        {
            Debug.Log("Level timed out!");
            Stage.DebugLogger.__LevelTimeouts++;
            IsRestarting = true;
            if (ActivateCollisionAsteroids) CancelTimer(_asteroidSpawnTimer);
            SaveAndEnd();
        }

        private int _save_i;
        private SavedSquad _save_savedSquad;
        private FleetShip _save_fleetship;
        private readonly List<Ship> _save_ships = new List<Ship>();
        private readonly List<FogOfWarVision> _save_fogOfWarVisions = new List<FogOfWarVision>();
        private readonly List<TargetingSquadMarker> _save_targetingSquadMarkers = new List<TargetingSquadMarker>();
        private readonly List<Obstacle> _save_obstacles = new List<Obstacle>();
        private readonly List<Projectile> _save_projectiles = new List<Projectile>();
        private ScaledTimer _levelEndedDialogueTimer = new ScaledTimer();

        public void SaveAndEnd()
        {
            Debug.Log("Saving and ending");
            if (Stage.RecordStats && !Stage.IsTraining)
            {
                for (_save_i = 0; _save_i < AllSquads.Count; _save_i++)
                {
                    _save_savedSquad = AllSquads[_save_i];
                    if (_save_savedSquad.HasBeenSavedToStorage)
                        _save_savedSquad = ConfigData.CurrentShips.GetSavedSquad(_save_savedSquad.Id);
                    else
                        continue;

                    _save_savedSquad.Stats.BattlesFought++;
                    if (_save_savedSquad.Side == WinningSide) _save_savedSquad.Stats.BattlesWon++;
                    _save_savedSquad.GetSquadShips().ForEach(ship =>
                    {
                        _save_fleetship = ship.GetFleetShip();
                        _save_fleetship.BattlesFought++;
                        if (_save_fleetship.Side == WinningSide) _save_fleetship.BattlesWon++;
                        _save_fleetship.MineralsMined += _save_fleetship.MineralsMinedThisLevel;
                        _save_fleetship.MineralsMinedThisLevel = 0;
                    });
                }
                ConfigData.CurrentShips.ReplaceDeadSquadShips(ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign);
                ConfigData.CurrentShips.SaveFleetData();
                ConfigData.CurrentShips.SaveSquadData();
            }

            _save_ships.Clear();
            _save_ships.AddRange(State.GetShips());
            for (_save_i = 0; _save_i < _save_ships.Count; _save_i++) _save_ships[_save_i].EndKill();

            _save_fogOfWarVisions.Clear();
            _save_fogOfWarVisions.AddRange(State.FogOfWarVisions);
            for (_save_i = 0; _save_i < _save_fogOfWarVisions.Count; _save_i++) _save_fogOfWarVisions[_save_i].Kill(0, true);

            _save_targetingSquadMarkers.Clear();
            _save_targetingSquadMarkers.AddRange(State.TargetingSquadMarkers);
            for (_save_i = 0; _save_i < _save_targetingSquadMarkers.Count; _save_i++) _save_targetingSquadMarkers[_save_i].Kill();

            if (HasObstacles)
            {
                for (_save_i = 0; _save_i < ObstacleMap.Obstacles.Count; _save_i++)
                {
                    Destroy(ObstacleMap.Obstacles[_save_i].gameObject);
                }
                Destroy(ObstacleMap.ObstacleBackground);
            }
            if (State.Obstacles.Count > 0)
            {
                _save_obstacles.Clear();
                _save_obstacles.AddRange(State.Obstacles);
                for (_save_i = 0; _save_i < _save_obstacles.Count; _save_i++)
                {
                    if (_save_obstacles[_save_i].ObstacleType == ConfigData.ObstacleTypes.CollisionAsteroid)
                        ((CollisionAsteroid)_save_obstacles[_save_i]).Kill(true);
                    else if (_save_obstacles[_save_i].ObstacleType == ConfigData.ObstacleTypes.MiningAsteroid)
                        ((MiningAsteroid)_save_obstacles[_save_i]).Kill(true);
                    else if (_save_obstacles[_save_i].ObstacleType == ConfigData.ObstacleTypes.AsteroidPiece)
                        ((AsteroidPiece)_save_obstacles[_save_i]).Kill();
                    else
                        Debug.LogError($"{_save_obstacles[_save_i].Name} does not have valid obstacle type: {_save_obstacles[_save_i].ObstacleType}");
                }
            }
            if (State.Projectiles.Count > 0)
            {
                _save_projectiles.Clear();
                _save_projectiles.AddRange(State.Projectiles);
                for (_save_i = 0; _save_i < _save_projectiles.Count; _save_i++) _save_projectiles[_save_i].Kill();
            }
            while (State.Deadbodies.Count > 0)
            {
                State.Deadbodies[0].Kill();
                State.Deadbodies.Remove(State.Deadbodies[0]);
            }

            State.StoreCommands();
            State.Release();
            if (!Stage.IsTraining && !Stage.Menus.IsMiniMapOpen) Stage.Menus.ToggleMiniMapDisplay();

            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign || ConfigData.IsTestingLevel)
            {
                if (Stage.DoesUserHaveController && !IsRestarting)
                {
                    if (ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge)
                    {
                        ConfigData.UserProgressData.ChallengeScore += State.PlayerScore;
                        Stage.Menus.ShowLevelSummary(() =>
                        {
                            Debug.Log("Showing level ended dialogue after challenge level summary");
                            _levelEndedDialogueTimer.Reuse(1, LevelEndedDialogue);
                            AddTimer(_levelEndedDialogueTimer);
                        });
                        if (WinningSide == ConfigData.Configuration.UserSide)
                        {
                            ConfigData.UserProgressData.AdvanceToNextLevel();
                            int challengeLevels = ConfigData.GetChallengeLevelData().GetLevels().Count;
                            if (ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.UserSide) >= challengeLevels)
                            {
                                ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Queen);
                                ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Queen);
                                ConfigData.UserProgressData.SetShipTypes();
                                Stage.Menus.CampaignCompletedDialogue = new Dialogue(Stage.DialoguePrefab, "Challenge Mode Completed!", "Congratulations! You've finished the Challenge Mode!", new List<string>() { "Main Menu" }, new List<UnityAction>() { Stage.Menus.ExitToMainMenu });
                                Stage.Menus.CampaignCompletedDialogue.SetTextBoxHeight(120);
                                Stage.Menus.CampaignCompletedDialogue.SetButtonWidth(0, 80);
                                ConfigData.UserProgressData.Save();
                                Stage.Menus.CampaignCompletedDialogue.Show();
                                return;
                            }
                        }
                        ConfigData.UserProgressData.Save();
                    }
                    else
                    {
                        _levelEndedDialogueTimer.Reuse(1, LevelEndedDialogue);
                        AddTimer(_levelEndedDialogueTimer);
                    }
                }
                else
                {
                    IsRestarting = false;
                    SetupLevel();
                }
            }
        }

        private void LevelEndedDialogue()
        {
            Stage.Menus.OpenLevelEndedDialogue();
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                Stage.Menus.TryNewSquadsButtonText.text = ConfigData.IsTestingLevel ? "Go Back" : "Play Next Level";
                Stage.Menus.KeepGoingButton.SetActive(false);
            }
            else if (ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge)
            {
                Stage.Menus.TryNewSquadsButtonText.text = WinningSide == ConfigData.Configuration.UserSide ? "Play Next Level" : "Try Again";
                Stage.Menus.KeepGoingButton.SetActive(false);
            }
        }

        public void Pause()
        {
            if (Stage.ActivateAudio) Stage.Audio.Pause();
            State.IsPaused = true;
            Time.timeScale = 0;
        }

        public void UnPause()
        {
            State.IsPaused = false;
            if (Stage.ActivateAudio && Stage.PlayMusic) Stage.Audio.Play();
            Time.timeScale = Stage.TimeScale;
        }

        private Projectile _f_projectile;
        private int _projectile_power;
        public Projectile AddProjectile(ConfigData.ProjectileTypes type, Weapon weapon, Vector2 startingPosition, float angle)
        {
            _f_projectile = Stage.Pool.GetProjectileFromPool(type);
            _f_projectile.transform.parent = Map.transform;
            _projectile_power = weapon.Power;
            if (weapon.Type == ConfigData.WeaponTypes.DualCannon) _projectile_power /= 2;
            _f_projectile.Setup(this, weapon, weapon.Ship, weapon.TargetShip, startingPosition, angle, weapon.Range, _projectile_power);
            weapon.Ship.ProjectilesInFlight.Add(_f_projectile);
            return _f_projectile;
        }

        private Queue<Squad> _hive_squads;
        private List<Squad> outOfBoundsHiveSquads = new List<Squad>();
        private Squad _hive_squad;
        private void GetHiveMindCommands()
        {
            if (!State.IsPaused && Stage.ActivateHiveMind && IsLevelSetupOnServer)
            {
                _hive_squads = State.GetSquadsAwaitingHiveMindCommands();
                while (_hive_squads.Count > 0)
                {
                    _hive_squad = _hive_squads.Dequeue();
                    if (!_hive_squad.IsDead)
                    {
                        if (_hive_squad.IsInBounds()) _hive_squad.MakeMatchupStrat();
                        else
                        {
                            if (!_hive_squad.HasDestination) _hive_squad.Move(StartingPositions[_hive_squad.Side - 1]);
                            outOfBoundsHiveSquads.Add(_hive_squad);
                        }
                    }
                }
                for (int i = 0; i < outOfBoundsHiveSquads.Count; i++)
                {
                    State.AddToSquadsAwaitingHiveMindCommands(outOfBoundsHiveSquads[i]);
                }
                outOfBoundsHiveSquads.Clear();
            }
        }

        public Vector2 GetPosition() { return transform.localPosition; }
        public Vector3 Get3DPosition() { return transform.localPosition; }
        public Vector2 ForceBounds(Vector2 point) { return ForceBounds(point.x, point.y); }
        public Vector2 ForceBounds(float x, float y) { return Utilities.ForceBounds(x, y, MaxX, MaxY, MinX, MinY); }
        public bool IsPointInBounds(Vector2 point) { return ForceBounds(point) == point; }
        public float DistanceOutOfBounds(Vector2 point) { return Vector2.Distance(point, ForceBounds(point)); }
    }
}
