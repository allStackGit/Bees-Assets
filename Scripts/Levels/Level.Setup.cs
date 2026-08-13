using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        private ScaledTimer _hivemindTimer = new ScaledTimer();
        private ScaledTimer _checkTriggersTimer = new ScaledTimer();
        private ScaledTimer _initialCommandDelayTimer = new ScaledTimer();
        public void SetupHivemind()
        {
            CancelTimer(_hivemindTimer);
            //CancelInvoke(nameof(GetHiveMindCommands));
            if (Stage.ActivateHiveMind)
            {
                List<Squad> squads = State.GetAllSquads();
                for (int i = 0; i < squads.Count; i++)
                {
                    Squad squad = squads[i];
                    if ((HasPlayer && squad.Side != ConfigData.Configuration.AISide) || squad.IsImmobile || squad.HasCommandQueue)
                    {
                        continue;
                    }
                    squad.AddToCommandList();
                }

                //Invoke(nameof(GetHiveMindCommands), Stage.InitialCommandDelay);
                _hivemindTimer.Reuse(.25f, GetHiveMindCommands, true);
                _initialCommandDelayTimer.Reuse(Stage.InitialCommandDelay - .25f, () =>
                {
                    AddTimer(_hivemindTimer);
                });
                AddTimer(_initialCommandDelayTimer);
            }
        }
        public void MakeSaveLevel()
        {
            SaveLevelOptions = new LevelOptions(ConfigData.GetLevelData().GetNewId(), ConfigData.Configuration.AISide, $"Random Level #{ConfigData.GetLevelData().GetNewId()}", CurrentLevelOptions.MapIndex,
                CurrentLevelOptions.Obstacles, CurrentLevelOptions.ObstacleList, CurrentLevelOptions.AsteroidOption == 2 ? 2 : (ActivateCollisionAsteroids ? 1 : 0),
                ActivateFogOfWar ? 1 : 0, ActivateMining ? 1 : 0, false, true, -1, ActivateLoadingShipsMidLevel ? 1 : 0, CurrentLevelOptions.EnemyReinforcementDelay, CurrentLevelOptions.EnemyShipTypeOption, 0,
                CurrentLevelOptions.EnemyReinforcements.ToList(), CurrentLevelOptions.EnemySquads.ToList(), new List<int>(), "", new List<SavedSquad>(), Vector2.zero, Vector2.zero);
        }
        public void SetupShips()
        {
            //if (ConfigData.ChooseRandomLevel)
            //{
            //    ConfigData.SquadsChosenForLevel = ConfigData.SquadsChosenForLevel.Where((chosenSquad) => !MidLevelSquads[chosenSquad.Side - 1].Contains(chosenSquad) && 
            //    chosenSquad.Side != CurrentLevelOptions.Side).ToList();
            //    CurrentLevelOptions.EnemySquads.ForEach((enemySquad) =>
            //    {
            //        Debug.Log($"Chose {enemySquad.Name} for level");
            //        ConfigData.SquadsChosenForLevel.Add((SavedSquad)enemySquad.Clone());
            //    });
            //}
            //else
            //{
            //    ConfigData.SquadsChosenForLevel = ConfigData.SquadsChosenForLevel.Where((chosenSquad) => !MidLevelSquads[chosenSquad.Side - 1].Contains(chosenSquad)).ToList();
            //}
             //Debug.Log(Utilities.ListToString(CurrentLevelOptions.ChosenSquads));
            LevelConstructor.SetupShips(ConfigData.Configuration.AISide);
             //Debug.Log(Utilities.ListToString(CurrentLevelOptions.ChosenSquads));
            LevelConstructor.SetupShips(ConfigData.Configuration.UserSide);
            AssignShipClearancesForSetup();
            if (CurrentLevelOptions.EnemyReinforcementDelay == 0)
            {
                CurrentLevelOptions.EnemyReinforcementDelay = ConfigData.StandardReinforcementsDelay;
            }
        }

        private void AssignShipClearancesForSetup()
        {
            List<Ship> ships = State.Ships;
            for (int i = 0; i < ships.Count; i++)
            {
                Ship ship = ships[i];
                if (!Stage.ShipClearances.TryGetValue(ship.ShipType, out int clearance))
                {
                    float halfWidth = ship.GetHalfWidth();
                    float halfHeight = ship.GetHalfHeight();
                    clearance = Mathf.CeilToInt(halfWidth > halfHeight ? halfWidth : halfHeight);
                    while (clearance % Pathfinder.Scale > 0)
                    {
                        clearance++;
                    }
                    clearance /= Pathfinder.Scale;
                    clearance = Mathf.Max(clearance, ConfigData.MinimumClearance);
                    Stage.ShipClearances.Add(ship.ShipType, clearance);
                }

                ship.Clearance = clearance;
                if (clearance > MaximumClearance)
                {
                    MaximumClearance = clearance;
                }
            }
        }

        private ScaledTimer _timeoutTimer = new ScaledTimer();
        public void SetupMapAndCamera()
        {
            Map.Setup(this);
            
            if (CurrentLevelOptions.UserStartingPosition != Vector2.zero)
            {
                StartingPositions[ConfigData.Configuration.UserSide - 1] = CurrentLevelOptions.UserStartingPosition;
                Stage.DefaultCameraPosition = CurrentLevelOptions.UserStartingPosition;

            }
            else
            {
                StartingPositions[ConfigData.Configuration.UserSide - 1] = Map.UserStartingPosition;
            }
            if (CurrentLevelOptions.AIStartingPosition != Vector2.zero)
            {
                StartingPositions[ConfigData.Configuration.AISide - 1] = CurrentLevelOptions.AIStartingPosition;
            }
            else
            {
                StartingPositions[ConfigData.Configuration.AISide - 1] = Map.AIStartingPosition;
            }


            // Setup map bounds
            MapWidth = (int)(Mathf.Abs(Map.SpriteRenderer.localBounds.min.x) + Map.SpriteRenderer.localBounds.max.x);
            MapHeight = (int)(Mathf.Abs(Map.SpriteRenderer.localBounds.min.y) + Map.SpriteRenderer.localBounds.max.y);
            HalfMapWidth = MapWidth / 2;
            HalfMapHeight = MapHeight / 2;

            MinX = Map.SpriteRenderer.localBounds.min.x + ConfigData.MapEdgePadding.x;
            MinY = Map.SpriteRenderer.localBounds.min.y + ConfigData.MapEdgePadding.y;
            MaxX = Map.SpriteRenderer.localBounds.max.x - ConfigData.MapEdgePadding.x;
            MaxY = Map.SpriteRenderer.localBounds.max.y - ConfigData.MapEdgePadding.y;
            MapX = Map.SpriteRenderer.localBounds.max.x * 2;
            MapY = Map.SpriteRenderer.localBounds.max.y * 2;
            MaxDistance = Mathf.Sqrt(MapX * MapX + MapY * MapY);
            HalfX = MapX / 2;
            HalfY = MapY / 2;


            if (!Stage.IsTraining && !Stage.UnlockCamera && Stage.PrimaryLevel == this)
            {
                Stage.SetupCamera();

                Stage.SquadTabs.ForEach((tab) =>
                {
                    tab.Background.GetComponent<UnityEngine.UI.Image>().color = ConfigData.GetUIColor("ui-green-screen");
                    tab.HideTab();
                });

            }



            if (HasObstacles) 
            {
                //CancelInvoke(nameof(SpawnAsteroid));
                //CancelInvoke(nameof(SetLocationHistory));
                //InvokeRepeating(nameof(SetLocationHistory), .5f, .5f);

                //Debug.Log($"");
                SpawnObstacles();
                if (Pathfinder != null)
                {
                    Pathfinder.Setup();
                }
                else
                {
                    Pathfinder = new Pathfinder(this);
                }

            }

            if (ConfigData.CurrentGameMode == ConfigData.GameModes.FishTank)
            {
                Stage.Camera.orthographicSize = Map.MaxZoom;
            }

        }
    }
}
