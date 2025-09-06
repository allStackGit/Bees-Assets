using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using Assets.Scripts.Server;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace Assets.Scripts.Levels

{
    public class LevelConstructor
    {
        public Level Level;
        public Stage Stage;
        public LevelConstructor(Level level)
        {
            Level = level;
            Stage = Level.Stage;
        }
        public void RequestServerSetup()
        {
            Level.IsLevelSetupOnServer = false;
            ConfigData.Socket.SendRequest(new SetupLevelRequest(new SetupLevel(ConfigData.CurrentGameMode == ConfigData.GameModes.FreePlay ? -1 : ConfigData.UserProgressData.GetCurrentLevel(), ConfigData.GetUserId()),
                ConfigData.StandardMaxTimeOnQueue, Level));
        }
        public void SetCarrierShipFleetships(List<Squad> squads)
        {
            squads.ForEach((squad) =>
            {
                if (squad is CarrierSquad)
                {
                    List<Ship> ships = squad.GetShips();
                    foreach (Ship ship in ships)
                    {
                        CarrierSquad carrierSquad = (CarrierSquad)squad;
                        CarrierShip carrierShip = (CarrierShip)ship;
                        carrierShip.CarrierShipSetup(carrierSquad.Carrier.FleetShip, carrierSquad.CarrierSquadType, carrierSquad.Carrier);
                    }
                }
            });
        }
        private int _queenCount;
        /// <summary>
        /// Generates random ships for the side and if the side is also the enemy side and there should be reinforcements it generates random reinforcements as well
        /// </summary>
        /// <param name="side"></param>
        public void AddRandomSquads(int side)
        {
            _queenCount = 0;
            //Debug.Log($"Adding random squads for side {side}");
            for (int option = 0; option < (Level.ActivateLoadingShipsMidLevel ? 2 : 1); option++)
            {
                bool hasArmedSquads = false;
                List<SavedSquad> squadsList = new List<SavedSquad>();
                for (int i = 0; i < Level.CurrentLevelOptions.EnemySquadGenerationCount; i++)
                {
                    ConfigData.ShipTypes type = Stage.BeeShipTypes.ElementAt(Random.Range(0, Stage.BeeShipTypes.Count));
                    if (side == ConfigData.Configuration.HumanSide)
                    {
                        type = Stage.HumanShipTypes.ElementAt(Random.Range(0, Stage.HumanShipTypes.Count));
                    }
                    while (side == ConfigData.Configuration.BeeSide && type == ConfigData.ShipTypes.Queen && Stage.BeeShipTypes.Count > 1 && (Level.HasObstacles || _queenCount == 2 || Utilities.RandomInt(4) != 3))
                    {
                        type = Stage.BeeShipTypes.ElementAt(Random.Range(0, Stage.BeeShipTypes.Count));
                    }
                    long squadId = Utilities.GetNegativeSavedSquadId();
                    SavedSquad savedSquad = new SavedSquad(squadId, side, $"{type}s #{squadId}", Vector2.zero, false, false,
                        ConfigData.DefaultShootingStrategy, ConfigData.UnsetColor, null);
                    savedSquad.SetupRandomShips(type);
                    squadsList.Add(savedSquad);
                    if (type == ConfigData.ShipTypes.Queen)
                    {
                        _queenCount++;
                    }

                    if (ConfigData.ArmedShipTypes.Contains(type) || (side == ConfigData.Configuration.BeeSide && Stage.OverrideBeeShipTypes.Count > 0)
                        || (side == ConfigData.Configuration.HumanSide && Stage.OverrideHumanShipTypes.Count > 0) || Level.CurrentLevelOptions.EnemyShipTypeOption != 0
                        || (side == ConfigData.Configuration.BeeSide && ConfigData.UserProgressData.VisibleBeeShipTypes.Intersect(ConfigData.ArmedShipTypes).Count() == 0) 
                        || (side == ConfigData.Configuration.HumanSide && ConfigData.UserProgressData.VisibleHumanShipTypes.Intersect(ConfigData.ArmedShipTypes).Count() == 0))
                    {
                        hasArmedSquads = true;
                    }
                    if (i == Level.CurrentLevelOptions.EnemySquadGenerationCount - 1 && !hasArmedSquads)
                    {
                        i--;
                        squadsList.Remove(savedSquad);
                    }
                }

                if (option == 0)
                {
                    if (side == ConfigData.Configuration.AISide)
                    {
                        //Debug.Log($"Adding randomly generated enemy squads");
                        Level.CurrentLevelOptions.EnemySquads.AddRange(squadsList);
                    }
                    else
                    {
                        //Debug.Log($"Adding randomly generated chosen squads");
                        Level.CurrentLevelOptions.ChosenSquads.AddRange(squadsList);
                    }

                }
                else
                {
                    if (side == ConfigData.Configuration.AISide)
                    {
                        //Debug.Log($"Adding randomly generated enemy reinforcement squads");
                        Level.CurrentLevelOptions.EnemyReinforcements.AddRange(squadsList);
                    }
                    else
                    {
                        //Level.CurrentLevelOptions.FriendlyReinforcements.AddRange(squadsList);
                    }
                }
            }
        }
        /// <summary>
        /// Adds the override squads for each level
        /// </summary>
        /// <param name="side"></param>
        public void AddOverrideSquads(int side)
        {
            List<SavedSquad> preloadSquads = new List<SavedSquad> {
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 7),  // human squad // 1 Barge, #0
                ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 8),  // human squad // 1 Carrier, #1
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 9),  // human squad // 1 Cruiser, #2s
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 10),  // human squad // 1 Dreadnought, #3
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 11),  // human squad // 1 Factory, #4
                ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 20),  // human squad // 1 Fire Barge, #5
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 13),  // human squad // 1 Flagship, #6
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 14),  // human squad // 1 Frigate, #7
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 15),  // human squad // 1 Gunship, #8
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 16),  // human squad // 1 Scout, #9
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 17),  // human squad // 1 Warp Gate, #10

                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 34),  // human squad
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 35),  // human squad
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 36),  // human squad
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 37),  // human squad
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 38),  // human squad
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 39),  // human squad
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 40),  // human squad
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 46),  // human squad (Warp gate ships)
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 47),  // human squad (3 red carriers)
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 52),  // human squad (Warp gate ship and others)



                ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 2),  // bee squad // 1 Bumblebee #11
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 12),  // bee squad // 1 Carpenter Bee #12
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 13),  // bee squad // 1 Honeybee #13
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 14),  // bee squad // 1 Hornet #14
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 15),  // bee squad // 1 Leafcutter #15
                ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 19),  // bee squad // 1 Queen #16
                //ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 17),  // bee squad // 1 Wasp #17 
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 18),  // bee squad // 1 Yellow Jacket #18
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 48),  // bee squad  // 1 Beehive #48
                    //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 21),  // bee squad // Line of 10 Hornets #21

                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 62),  // bee squad  // 10 Yellow Jackets #62
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 63),  // bee squad  // 1 Yellow Jacket #63


            };

            List<SavedSquad> midLevelSquads = new List<SavedSquad> {
                ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 0),  // human squad // 1 Barge, #0
                ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 1),  // human squad // 1 Carrier, #1
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 2),  // human squad // 1 Cruiser, #2
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 3),  // human squad // 1 Dreadnought, #3
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 4),  // human squad // 1 Factory, #4
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 5),  // human squad // 1 Fire Barge, #5
                ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 6),  // human squad // 1 Flagship, #6
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 7),  // human squad // 1 Frigate, #7
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 8),  // human squad // 1 Gunship, #8
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 9),  // human squad // 1 Scout, #9
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 10),  // human squad // 1 Warp Gate, #10
                    //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 19),  // human squad // New ships with colors, #19



                ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 11),  // bee squad // 1 Bumblebee #11
                ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 12),  // bee squad // 1 Carpenter Bee #12
                ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 13),  // bee squad // 1 Honeybee #13
                ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 14),  // bee squad // 1 Hornet #14
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 15),  // bee squad // 1 Leafcutter #15
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 16),  // bee squad // 1 Queen #16
                ConfigData.CurrentShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 17),  // bee squad // 1 Wasp #17 
                //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 18),  // bee squad // 1 Yellow Jacket #18

            };

            if (side == ConfigData.Configuration.AISide)
            {
                preloadSquads.ForEach((squad) =>
                {
                    if (squad != null && !Level.CurrentLevelOptions.EnemySquads.Contains(squad))
                    {
                        Level.CurrentLevelOptions.EnemySquads.Add(squad);
                    }
                });

                if (Level.CurrentLevelOptions.EnemyReinforcementsOption == 1)
                {
                    preloadSquads.ForEach((squad) =>
                    {
                        if (squad != null && !Level.CurrentLevelOptions.EnemyReinforcements.Contains(squad))
                        {
                            Level.CurrentLevelOptions.EnemyReinforcements.Add(squad);
                        }
                    });
                }
            }
            else
            {
                preloadSquads.ForEach((squad) =>
                {
                    if (squad != null && !Level.CurrentLevelOptions.ChosenSquads.Contains(squad))
                    {
                        Level.CurrentLevelOptions.ChosenSquads.Add(squad);
                    }
                });
            }
            
        }
        public void SetupShips(int side)
        {
            //ConfigData.CurrentShips = new Ships(ConfigData.GetFleetData(), ConfigData.GetSavedSquadsData());

            //Debug.Log($"Stage: {Stage}, Level: {Level.Name}, CurrentLevelOptions: {Level.CurrentLevelOptions.GetAllLevelDetails()}");
            if (Stage.IsTrainingNueralNetwork || Stage.UseFullyRandomSquads || ((Stage.UseFullyRandomEnemySquads || Level.CurrentLevelOptions.EnemySquadGenerationCount > 0) && side == ConfigData.Configuration.AISide))
            {
                AddRandomSquads(side);
            }
            else if ((Stage.UseOverrideSquads && side == ConfigData.Configuration.UserSide) || (Stage.UseOverrideEnemySquads && side == ConfigData.Configuration.AISide))
            {
                AddOverrideSquads(side);
            }

            // Setup entities on the level
            //Debug.Log($"Setting up ships for {side} at {Level.StartingPositions[side - 1]}");
            //Debug.Log($"Chosen squads: {Utilities.ListToString(Level.CurrentLevelOptions.ChosenSquads)}");
            if (side == ConfigData.Configuration.AISide)
            {
                //Debug.Log($"Squads to spawn: {Utilities.ListToString(Level.CurrentLevelOptions.EnemySquads)}");
                Level.CurrentLevelOptions.EnemyExistingSquads.ForEach((squadId) =>
                {
                    SavedSquad existingSquad = ConfigData.CurrentShips.GetSavedSquad(squadId);
                    if (existingSquad != null)
                    {
                        Level.CurrentLevelOptions.EnemySquads.Add(existingSquad);
                    }
                });
                Debug.Log($"Squads to spawn: {Utilities.ListToString(Level.CurrentLevelOptions.EnemySquads)}");
                SpawnShipsAndSquads(Level.CurrentLevelOptions.EnemySquads, Level.StartingPositions[side - 1], Vector2.zero, false);
            }
            else
            {
                Debug.Log($"Chosen squads to spawn: {Utilities.ListToString(Level.CurrentLevelOptions.ChosenSquads)}");
                SpawnShipsAndSquads(Level.CurrentLevelOptions.ChosenSquads, Level.StartingPositions[side - 1], Vector2.zero, false);
            }

        }
        public void SpawnShipsAndSquads(List<SavedSquad> squads, Vector2 startingPosition, Vector2 moveToPoint, bool squadsAreReinforcements)
        {
            List<Squad> setupSquads = new List<Squad>();
            List<Ship> carriers = new List<Ship>();
            squads.ForEach((savedSquad) =>
            {
                //Debug.Log($"The squad is {savedSquad.Name}");
                Squad squad = savedSquad.ToSquad(Level);
                setupSquads.Add(squad);

                // [note][testing] turn this off to do training without ships dying
                //List<SquadShip> 
                List<SquadShip> ships = new List<SquadShip>();
                ships = savedSquad.GetSquadShips().Where((s) => !s.GetFleetShip().IsDead).ToList();
                if (ships.Count > 0)  
                {
                    //Debug.Log($"There are {ships.Count} ships in {squad.Name}");
                    squad.SquadNumber = Level.State.OriginalSquadCounts[squad.Side - 1] + 1;
                    squad.SetSquadTab();
                    Level.State.AddSquad(squad);

                    // loop through the squadships in each saved squad 

                    // .Where((s) => !s.GetFleetShip().IsDead).ToList() [alert] use this to remove dead ships
                    ships.ForEach((squadShip) =>
                    {
                        //Debug.Log($"This ship is {squadShip.ShipType}");

                        FleetShip fleetShip = squadShip.GetFleetShip();

                        

                        Ship ship = InstantiateShip(fleetShip.Type);
                        ship.Setup(
                                Level,
                                fleetShip,
                                squad,
                                squadShip.Offset
                            );



                        squad.AddShip(ship);
                        ship.SetColor();

                        if (ship.ShipType == ConfigData.ShipTypes.Carrier)
                        {
                            carriers.Add(ship);
                        }

                    });
                    if (squad.IsMatchingSpeed)
                    {
                        squad.MatchSpeed();
                    }
                    //else
                    //{
                    //    Debug.Log($"{squad} is not matching speed");
                    //}
                    // set initial tsv
                    Level.State.InitialTsv[squad.Side - 1] += squad.Tsv;
                    //Debug.Log($"Increase side TSV by {squad.Tsv} / {state.InitialTsv[squad.Side - 1]}");
                }
            });

            carriers.ForEach((carrier) =>
            {
                Squad squad = carrier.Squad;
                // spawn drones
                for (int i = 0; i < ConfigData.Configuration.CarrierSquadCount; i++)
                {
                    CarrierSquad droneSquad = Stage.Pool.GetCarrierSquadFromPool();
                    droneSquad.Setup(
                        Level,
                        carrier.Squad.SavedSquad,
                        carrier.Squad.GetShootingStrategy(),
                        carrier.Squad.CeaseFire,
                        carrier.Squad.IsMatchingSpeed,
                        carrier.Squad.ShouldChase(),
                        false,
                        Utilities.GetNegativeSavedSquadId(),
                        carrier.Squad.Side,
                        Level.State.OriginalSquadCounts[carrier.Side - 1] + 1,
                        $"{carrier.Squad.Name} - Drone Force #{i + 1}",
                        carrier.Squad.Color
                    );
                    Level.State.AddSquad(droneSquad);
                    setupSquads.Add(droneSquad);
                    droneSquad.SetupCarrierSquad((Carrier)carrier, ConfigData.ShipTypes.Drone);

                    // spawn strikers
                    CarrierSquad strikerSquad = Stage.Pool.GetCarrierSquadFromPool();
                    strikerSquad.Setup(
                        Level,
                        carrier.Squad.SavedSquad,
                        carrier.Squad.GetShootingStrategy(),
                        carrier.Squad.CeaseFire,
                        carrier.Squad.IsMatchingSpeed,
                        carrier.Squad.ShouldChase(),
                        false,
                        Utilities.GetNegativeSavedSquadId(),
                        carrier.Squad.Side,
                        Level.State.OriginalSquadCounts[carrier.Side - 1] + 1,
                        $"{carrier.Squad.Name} - Striker Force #{i + 1}",
                        carrier.Squad.Color
                    );
                    Level.State.AddSquad(strikerSquad);
                    setupSquads.Add(strikerSquad);

                    strikerSquad.SetupCarrierSquad((Carrier)carrier, ConfigData.ShipTypes.Striker);

                    if (squad.IsMatchingSpeed)
                    {
                        strikerSquad.MatchSpeed();
                        droneSquad.MatchSpeed();
                    }

                }
            });

            PositionSquads(setupSquads, startingPosition, moveToPoint, squadsAreReinforcements);

            if (carriers.Count > 0)
            {
                // set the fleetships for the carrier ships
                SetCarrierShipFleetships(setupSquads);
            }


            //Debug.Log($"Initial TSV is Humans: {Level.State.InitialTsv[ConfigData.Configuration.HumanSide-1]}, " +
            //    $"Bees: {Level.State.InitialTsv[ConfigData.Configuration.BeeSide-1]}");
            //Debugger.PrintList(HasBeeTypes);
        }
        private void PositionSquads(List<Squad> squads, Vector2 startingPosition, Vector2 moveToPoint, bool squadsAreReinforcements)
        {
            Squad firstLevelSquad = null;
            Squad previousLeftSquad = null;
            Squad previousRightSquad = null;
            bool firstSquad = true;
            int horizontalSteps = 0;
            float rightWidth = 0;
            float leftWidth = 0;
            float bottomHeight = 0;
            float greatestWidth = 0;
            float leastWidth = 0;
            float halfWidth = 0;
            float halfHeight = 0;

            // setup preliminary position so that the squad height and width can be calculated
            Debug.Log($"Placing {squads.Count} squads at {startingPosition}");
            squads.ForEach((squad) =>
            {
                squad.SetStartingPosition(startingPosition);
                squad.NameSquadShips();
            });
            if (Stage.IsTrainingNueralNetwork)
            {
                return; // [alert] [rl-training] only do this for rl learning
            }
            squads.ForEach((squad) =>
            {
                if (squad.GetShips().Count == 0)
                {
                    return;

                }
                //Debug.Log("----------------------------------------------------------------------------------------------------------------------------\n");
                Vector2 position = squad.GetPosition();
                halfWidth = squad.GetWidth() / 2;
                halfHeight = squad.GetHeight() / 2;

                if (firstSquad)
                {
                    // increase the margins for the next squad
                    //rightWidth += halfWidth;
                    //leftWidth += halfWidth;
                    //bottomHeight += halfHeight;
                    firstSquad = false;
                    firstLevelSquad = squad;
                    previousLeftSquad = squad;
                    previousRightSquad = squad;

                    //Debug.Log($"Positioned first squad: {squad.Name}");
                }
                else
                {
                //    Debug.Log($"{squad.Name} is potentially located at {position} before changing the y level. It's left side is at {leastWidth} and it's right side is at {greatestWidth}. It's " +
                //$"{squad.GetWidth()} wide and {squad.GetHeight()} tall");
                    position.y = firstLevelSquad.GetPosition().y; // set the squad on the same y level as the last squad

                    // adjust the position left or right from center based on how many squads have already been placed on this level
                    if (horizontalSteps % 2 == 0)
                    {
                        rightWidth += halfWidth + 10;
                        rightWidth += previousRightSquad.GetWidth() / 2;
                        position.x += rightWidth;
                        previousRightSquad = squad;
                        //Debug.Log($"Moving {squad.Name} {rightWidth} points to the right. {halfWidth} points contributed from its own half width.");
                    }
                    else
                    {
                        leftWidth += halfWidth + 10;
                        leftWidth += previousLeftSquad.GetWidth() / 2;
                        position.x += -1 * leftWidth;
                        previousLeftSquad = squad;
                        //Debug.Log($"Moving {squad.Name} {leftWidth} points to the left. {halfWidth} points contributed from its own half width.");

                    }


                }
                // calculate the furthest extents of the squad and add 10 for good margin. Use it to determine if the squad is over the horizontal edges
                greatestWidth = position.x + halfWidth + 0;
                leastWidth = position.x - (halfWidth + 0);

                //Debug.Log($"{squad.Name} is potentially located at {position}. It's left side is at {leastWidth} and it's right side is at {greatestWidth}. It's " +
                //$"{squad.GetWidth()} wide and {squad.GetHeight()} tall");

                if (!squadsAreReinforcements && (greatestWidth > Level.MaxX || leastWidth < Level.MinX))
                {
                    // Debug Log how much the squad is out of bounds horizontally
                    if (greatestWidth > Level.MaxX)
                    {
                        //Debug.Log($"{squad.Name} is over the margin of MaxX by {Mathf.Abs(greatestWidth - MaxX)}");
                    }
                    else
                    {
                        //Debug.Log($"{squad.Name} is over the margin of MinX by {Mathf.Abs(leastWidth - MinX)}");
                    }


                    position.x = squad.StartingPosition.x; // center the squad on the original x point. The y point should be on the same level as the previous squads

                    // increase the vertical margin for this squad from the starting position of the first squad. The half height of this squad and the half height
                    // of the first squad on the previous level.
                    bottomHeight = halfHeight;
                    bottomHeight += firstLevelSquad.GetHeight() / 2;
                    position.y += bottomHeight + 20; // add the half height and 10 for additional margin off the squad below

                    // reset the left and right width and steps
                    rightWidth = halfWidth;
                    leftWidth = halfWidth;
                    horizontalSteps = 0;

                    // calculate the furthest extents of the squad and add 10 for good margin. Use it to determine if the squad is over the horizontal edges
                    greatestWidth = position.x + halfWidth + 0;
                    leastWidth = position.x - (halfWidth + 0);

                    //Debug.Log(
                    //$"Because {squad.Name} is over the margin it is being relocated to {position}. It's left side is at {leastWidth} and it's right side is at {greatestWidth}.\n " +
                    //$"It's topSide is at {(position.y + halfHeight)} and it's bottom side is at {(position.y - halfHeight)}. it has a half height of {halfHeight} and a halfWidth of {halfWidth}. \n" +
                    //$" It has been moved up from the original position by {bottomHeight}"
                    //);

                    firstLevelSquad = squad; // set this squad as the first level squad of this level
                    //Debug.Log($"Positioned first squad on this level: {squad.Name}");
                }

                //Debug.Log($"The previous squad {previousSquad.Name} was centered on {previousSquad.GetPosition()} and has a half width of {previousSquad.GetWidth() / 2}. " +
                //   $"The current squad {squad.Name} will be centered on {position} and have a half width of {halfWidth}. ");



                // set the position
                squad.SetStartingPosition(position);
                horizontalSteps++;
                squad.SetOffsets();
                if (moveToPoint != Vector2.zero)
                {
                    squad.Move(moveToPoint);
                }
            });
        }
        public Ship InstantiateShip(ConfigData.ShipTypes type)
        {
            Ship ship = null;
            switch (type)
            {
                case ConfigData.ShipTypes.Barge:
                    ship = Stage.Pool.BargePool.Get();
                    break;
                case ConfigData.ShipTypes.Beacon:
                    ship = Stage.Pool.BeaconPool.Get();
                    break;
                case ConfigData.ShipTypes.Beehive:
                    ship = Stage.Pool.BeehivePool.Get();
                    break;
                case ConfigData.ShipTypes.Bumblebee:
                    ship = Stage.Pool.BumblebeePool.Get();
                    break;
                case ConfigData.ShipTypes.CarpenterBee:
                    ship = Stage.Pool.CarpenterBeePool.Get();
                    break;
                case ConfigData.ShipTypes.Carrier:
                    ship = Stage.Pool.CarrierPool.Get();
                    break;
                case ConfigData.ShipTypes.Cruiser:
                    ship = Stage.Pool.CruiserPool.Get();
                    break;
                case ConfigData.ShipTypes.Dreadnought:
                    ship = Stage.Pool.DreadnoughtPool.Get();
                    break;
                case ConfigData.ShipTypes.Drone:
                    ship = Stage.Pool.DronePool.Get();
                    break;
                case ConfigData.ShipTypes.Factory:
                    ship = Stage.Pool.FactoryPool.Get();
                    break;
                case ConfigData.ShipTypes.FireBarge:
                    ship = Stage.Pool.FireBargePool.Get();
                    break;
                case ConfigData.ShipTypes.Flagship:
                    ship = Stage.Pool.FlagshipPool.Get();
                    break;
                case ConfigData.ShipTypes.Frigate:
                    ship = Stage.Pool.FrigatePool.Get();
                    break;
                case ConfigData.ShipTypes.Gunship:
                    ship = Stage.Pool.GunshipPool.Get();
                    break;
                case ConfigData.ShipTypes.Honeybee:
                    ship = Stage.Pool.HoneybeePool.Get();
                    break;
                case ConfigData.ShipTypes.Hornet:
                    ship = Stage.Pool.HornetPool.Get();
                    break;
                case ConfigData.ShipTypes.Leafcutter:
                    ship = Stage.Pool.LeafcutterPool.Get();
                    break;
                case ConfigData.ShipTypes.Queen:
                    ship = Stage.Pool.QueenPool.Get();
                    break;
                case ConfigData.ShipTypes.Scout:
                    ship = Stage.Pool.ScoutPool.Get();
                    break;
                case ConfigData.ShipTypes.Striker:
                    ship = Stage.Pool.StrikerPool.Get();
                    break;
                case ConfigData.ShipTypes.WarpGate:
                    ship = Stage.Pool.WarpGatePool.Get();
                    break;
                case ConfigData.ShipTypes.Wasp:
                    ship = Stage.Pool.WaspPool.Get();
                    break;
                case ConfigData.ShipTypes.YellowJacket:
                    ship = Stage.Pool.YellowJacketPool.Get();
                    break;
                default:
                    Debug.LogError($"Tried to instanstiate a ship type ({type}) that doesn't exist");
                    break;
            }
            ship.transform.SetParent(Level.Map.Transform);
            return ship;
        }
    }
}