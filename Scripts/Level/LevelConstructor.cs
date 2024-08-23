using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using Assets.Scripts.Server;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace Assets.Scripts.Level

{
    public class LevelConstructor
    {
        public LevelStage Level;
        public int SquadNumber;
        public LevelConstructor(LevelStage level)
        {
            Level = level;
        }
        public void RequestServerSetup()
        {
            Level.IsLevelSetupOnServer = false;
            ConfigData.Socket.SendRequest(new SetupLevelRequest(new SetupLevel(ConfigData.GetLevel(), ConfigData.GetUserId(), ConfigData.Version),
                ConfigData.StandardMaxTimeOnQueue, Level));
        }

        public void AddShipsMidLevel(List<SavedSquad> squads, Vector2 spawnPoint, Vector2 moveToPoint)
        {
            Debug.Log($"Adding ships into the middle of the level at {spawnPoint}");
            SetupShipsAndSquads(squads, spawnPoint, moveToPoint);
            squads.ForEach((squad) =>
            {
                if (squad != null && !ConfigData.SquadsChosenForLevel.Contains(squad))
                {
                    ConfigData.SquadsChosenForLevel.Add(squad);
                }
            });

            
            
        }
        public void SetCarrierShipFleetships()
        {
            Level.GetState().GetSquadsBySide(ConfigData.Configuration.HumanSide).ForEach((squad) =>
            {
                if (squad is CarrierSquad)
                {
                    List<Ship> ships = squad.GetShips();
                    foreach (Ship ship in ships)
                    {
                        CarrierSquad carrierSquad = (CarrierSquad)squad;
                        CarrierShip carrierShip = (CarrierShip)ship;
                        carrierShip.CarrierShipSetup(carrierSquad.Carrier.FleetShip, carrierSquad.SquadType, carrierSquad.Carrier);
                    }
                }
            });
        }

        public void SetupShips()
        {
            GameState state = Level.GetState();
            ConfigData.AllShips = new Ships(ConfigData.GetFleetData(), ConfigData.GetSavedSquadsData());


            if (Level.IsTrainingNueralNetwork || Level.UseSemiRandomSquads || Level.UseFullyRandomSquads)
            {
                ConfigData.SquadsChosenForLevel.Clear();
            }else if (Level.UseFullyRandomEnemySquads)
            {
                ConfigData.SquadsChosenForLevel = ConfigData.SquadsChosenForLevel.Where((squad) => squad.Side == ConfigData.Configuration.UserSide).ToList();
            }
            else
            {
                ConfigData.AllShips.ReplaceDeadSquadShips();
            }
            SquadNumber = Random.Range(1, ConfigData.Configuration.SquadGenerationCount);
            SetShips(ConfigData.Configuration.UserSide);
            SetShips(ConfigData.Configuration.AISide);

            // Setup entities on the level
            SetupShipsAndSquads(ConfigData.SquadsChosenForLevel.Where((s) => s.Side == ConfigData.Configuration.UserSide).OrderByDescending((s) => s.Side).ToList(), 
                Level.StartingPositions[ConfigData.Configuration.UserSide - 1], Vector2.zero);
            SetupShipsAndSquads(ConfigData.SquadsChosenForLevel.Where((s) => s.Side == ConfigData.Configuration.AISide).OrderByDescending((s) => s.Side).ToList(), 
                Level.StartingPositions[ConfigData.Configuration.AISide - 1], Vector2.zero);

            // set the fleetships for the carrier ships
            SetCarrierShipFleetships();


            if (state.GetSquadsBySide(ConfigData.Configuration.UserSide).Count > 0 && state.GetSquadsBySide(ConfigData.Configuration.AISide).Count > 0 && !Level.IsTrainingNueralNetwork && !Level.IsTrainingHiveMind)
            {
                state.SelectSquad(state.GetSquadByNumber(ConfigData.Configuration.UserSide, 1));
            }
            else if (!Level.IsTrainingNueralNetwork && !Level.IsTrainingHiveMind)
            {
                Debug.Log($"User squads: {state.GetSquadsBySide(ConfigData.Configuration.UserSide).Count}, AI squads: {state.GetSquadsBySide(ConfigData.Configuration.AISide).Count}");
                Level.Menus.NoAliveShipsAlert.SetActive(true);
            }
        }
        public void SetShips(int side)
        {
            //GameState state = Level.GetState();
            

            if (ConfigData.SquadsChosenForLevel.Where((squad) => squad.Side == side).ToList().Count == 0)
            {
                List<SavedSquad> preloadSquads = new List<SavedSquad>();
                List<SavedSquad> midLevelSquads = new List<SavedSquad>();
                if (Level.UseSemiRandomSquads && (side != ConfigData.Configuration.AISide || !Level.UseFullyRandomEnemySquads))
                {

                    List<List<int>> indexes = new List<List<int>>();
                    indexes.Add(new List<int> {16, 41, 42, 43, 44, 45}); // bee indexes
                    indexes.Add(new List<int> {34, 35, 36, 37, 38, 39, 40}); // human indexes

                    for (int option = 0; option < 2; option++)
                    {
                        List<SavedSquad> squadsList = option == 0 ? preloadSquads : midLevelSquads;
                        for (int i = 0; i < SquadNumber; i++)
                        {
                            int chosenIndex = indexes[side - 1][Random.Range(0, indexes[side - 1].Count)];

                            indexes[side - 1].Remove(chosenIndex);
                            SavedSquad squad = ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Id == chosenIndex);
                            if (squad != null)
                            {
                                squadsList.Add(squad);
                            }
                        }
                    }
                    
                    
                } else if (Level.UseFullyRandomSquads || (Level.UseFullyRandomEnemySquads && side != ConfigData.Configuration.UserSide)) {

                    for (int option = 0; option < 2; option++)
                    {
                        List<SavedSquad> squadsList = option == 0 ? preloadSquads : midLevelSquads;
                        for (int i = 0; i < SquadNumber; i++)
                        {
                            string type = ConfigData.Configuration.VisibleBeeShipTypes.ElementAt(Random.Range(0, ConfigData.Configuration.VisibleBeeShipTypes.Count));
                            if (side == ConfigData.Configuration.HumanSide)
                            {
                                type = ConfigData.Configuration.VisibleHumanShipTypes.ElementAt(Random.Range(0, ConfigData.Configuration.VisibleHumanShipTypes.Count));
                            }
                            while (Level.HasObstacles && side == ConfigData.Configuration.BeeSide && type == "Queen")
                            {
                                type = ConfigData.Configuration.VisibleBeeShipTypes.ElementAt(Random.Range(0, ConfigData.Configuration.VisibleBeeShipTypes.Count));
                            }
                            int squadId = Utilities.GetNegativeSavedSquadId();
                            SavedSquad savedSquad = new SavedSquad(squadId, side, $"{type}s #{squadId}", Vector2.zero, false, false,
                                ConfigData.StartingSettings.DefaultShootingStrategy, ConfigData.UnsetColor, null);
                            savedSquad.SetupRandomShips(type);
                            squadsList.Add(savedSquad);
                        }
                    }
                    


                }
                else
                {
                    preloadSquads = new List<SavedSquad> {
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 0),  // human squad // 1 Barge, #0
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 1),  // human squad // 1 Carrier, #1
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 2),  // human squad // 1 Cruiser, #2
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 3),  // human squad // 1 Dreadnought, #3
                        ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 4),  // human squad // 1 Factory, #4
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 5),  // human squad // 1 Fire Ship, #5
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 6),  // human squad // 1 Flagship, #6
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 7),  // human squad // 1 Frigate, #7
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 8),  // human squad // 1 Gunship, #8
                        ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 9),  // human squad // 1 Scout, #9
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 10),  // human squad // 1 Warp Gate, #10

                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 34),  // human squad
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 35),  // human squad
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 36),  // human squad
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 37),  // human squad
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 38),  // human squad
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 39),  // human squad
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 40),  // human squad
                        ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 46),  // human squad (Factory ships)



                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 11),  // bee squad // 1 Bumblebee #11
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 12),  // bee squad // 1 Carpenter Bee #12
                        ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 13),  // bee squad // 1 Honeybee #13
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 14),  // bee squad // 1 Hornet #14
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 15),  // bee squad // 1 Leafcutter #15
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 16),  // bee squad // 1 Queen #16
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 17),  // bee squad // 1 Wasp #17 
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 18),  // bee squad // 1 Yellow Jacket #18
                         //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 21),  // bee squad // Line of 10 Hornets #21

                    };

                    midLevelSquads = new List<SavedSquad> {
                        ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 0),  // human squad // 1 Barge, #0
                        ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 1),  // human squad // 1 Carrier, #1
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 2),  // human squad // 1 Cruiser, #2
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 3),  // human squad // 1 Dreadnought, #3
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 4),  // human squad // 1 Factory, #4
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 5),  // human squad // 1 Fire Ship, #5
                        ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 6),  // human squad // 1 Flagship, #6
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 7),  // human squad // 1 Frigate, #7
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 8),  // human squad // 1 Gunship, #8
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 9),  // human squad // 1 Scout, #9
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 10),  // human squad // 1 Warp Gate, #10
                         //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 19),  // human squad // New ships with colors, #19



                        ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 11),  // bee squad // 1 Bumblebee #11
                        ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 12),  // bee squad // 1 Carpenter Bee #12
                        ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 13),  // bee squad // 1 Honeybee #13
                        ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 14),  // bee squad // 1 Hornet #14
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 15),  // bee squad // 1 Leafcutter #15
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 16),  // bee squad // 1 Queen #16
                        ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 17),  // bee squad // 1 Wasp #17 
                        //ConfigData.AllShips.GetSavedSquads().FirstOrDefault((s) => s.Side == side && s.Id == 18),  // bee squad // 1 Yellow Jacket #18

                    };
                }

                

                preloadSquads.ForEach((s) =>
                {
                    if (s != null && !ConfigData.SquadsChosenForLevel.Contains(s))  
                    {
                        ConfigData.SquadsChosenForLevel.Add(s);
                    }
                });
                midLevelSquads.ForEach((s) =>
                {
                    if (s != null && !ConfigData.SquadsChosenForLevel.Contains(s))
                    {
                        Level.MidLevelSquads[side - 1].Add(s);
                    }
                });
            }
        }
        private void SetupShipsAndSquads(List<SavedSquad> squads, Vector2 startingPosition, Vector2 moveToPoint)
        {
            GameState state = Level.GetState();
            List<Squad> setupSquads = new List<Squad>();
            squads.ForEach((savedSquad) =>
            {
                //Debug.Log($"The squad is {savedSquad.Name}");
                Squad squad = savedSquad.ToSquad(Level);
                setupSquads.Add(squad);

                // [note][testing] turn this off to do training without ships dying
                //List<SquadShip> 
                List<SquadShip> ships = new List<SquadShip>();
                ships = savedSquad.GetShips().Where((s) => !s.GetFleetShip().IsDead).ToList();
                if (ships.Count > 0)  
                {
                    //Debug.Log($"There are {ships.Count} ships in {squad.Name}");
                    squad.SquadNumber = state.OriginalSquadCounts[squad.Side - 1] + 1;
                    state.AddSquad(squad);

                    // loop through the squadships in each saved squad 

                    // .Where((s) => !s.GetFleetShip().IsDead).ToList() [alert] use this to remove dead ships
                    ships.ForEach((squadShip) =>
                    {
                        //Debug.Log($"This ship is {squadShip.ShipType}");
                        // log bees in the level
                        if (squad.Side == ConfigData.Configuration.BeeSide)
                        {
                            if (!Level.HasBeeTypes.Contains(squadShip.ShipType))
                            {
                                Level.HasBeeTypes.Add(squadShip.ShipType);
                            }
                        }

                        FleetShip fleetShip = squadShip.GetFleetShip();

                        

                        Ship ship = null;
                        GameObject instance = null;

                        (GameObject, Ship) tuple = InstantiateShip(fleetShip.Type);
                        instance = tuple.Item1;
                        ship = tuple.Item2;
                        ship.Setup(
                                Level,
                                state.AddEntity(),
                                fleetShip,
                                squad,
                                squadShip.Offset
                            );



                        squad.AddShip(ship);
                        ship.SetColor();

                        if (ship.ShipType == "Carrier")
                        {
                            //Debug.Log("Spawned a carrier");

                            // spawn drones
                            for (int i = 0; i < ConfigData.Configuration.CarrierSquadCount; i++)
                            {
                                CarrierSquad droneSquad = Level.gameObject.AddComponent<CarrierSquad>();
                                droneSquad.Setup(
                                    Level,
                                    ship.Squad.SavedSquad,
                                    ship.Squad.GetShootingStrategy(),
                                    ship.Squad.CeaseFire,
                                    ship.Squad.IsMatchingSpeed,
                                    Utilities.GetNegativeSavedSquadId(),
                                    ship.Squad.Side,
                                    state.OriginalSquadCounts[ship.Side - 1] + 1,
                                    $"{ship.Squad.Name} - Drone Force #{i + 1}",
                                    ship.Squad.Color
                                );
                                state.AddSquad(droneSquad);
                                setupSquads.Add(droneSquad);
                                droneSquad.SetupCarrierSquad((Carrier)ship, "Drone");

                                // spawn strikers
                                CarrierSquad strikerSquad = Level.gameObject.AddComponent<CarrierSquad>();
                                strikerSquad.Setup(
                                    Level,
                                    ship.Squad.SavedSquad,
                                    ship.Squad.GetShootingStrategy(),
                                    ship.Squad.CeaseFire,
                                    ship.Squad.IsMatchingSpeed,
                                    Utilities.GetNegativeSavedSquadId(),
                                    ship.Squad.Side,
                                    state.OriginalSquadCounts[ship.Side - 1] + 1,
                                    $"{ship.Squad.Name} - Striker Force #{i + 1}",
                                    ship.Squad.Color
                                );
                                state.AddSquad(strikerSquad);
                                setupSquads.Add(strikerSquad);

                                strikerSquad.SetupCarrierSquad((Carrier)ship, "Striker");

                                if (squad.IsMatchingSpeed)
                                {
                                    strikerSquad.MatchSpeed();
                                    droneSquad.MatchSpeed();
                                }

                                ship.AdditionalTsv += droneSquad.Tsv + strikerSquad.Tsv;

                            }

                        }

                    });
                    if (squad.IsMatchingSpeed)
                    {
                        squad.MatchSpeed();
                    }
                    // set initial tsv
                    state.InitialTsv[squad.Side - 1] += squad.Tsv;
                    //Debug.Log($"Increase side TSV by {squad.Tsv} / {state.InitialTsv[squad.Side - 1]}");
                }
            });

            PositionSquads(setupSquads, startingPosition, moveToPoint);


            //Debug.Log($"Initial TSV is Humans: {Level.GetState().InitialTsv[ConfigData.Configuration.HumanSide-1]}, " +
            //    $"Bees: {Level.GetState().InitialTsv[ConfigData.Configuration.BeeSide-1]}");
            //Debugger.PrintList(HasBeeTypes);
        }
        private void PositionSquads(List<Squad> squads, Vector2 startingPosition, Vector2 moveToPoint)
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
            //Debug.Log($"Placing squads at {startingPosition}");
            squads.ForEach((squad) =>
            {
                squad.SetStartingPosition(startingPosition);
                squad.NameSquadShips();
            });
            if (Level.IsTrainingNueralNetwork)
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

                if (greatestWidth > Level.MaxX || leastWidth < Level.MinX)
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


                    position.x = squad.StartingPosition.x; // center the squad on the origina x point. The y point should be on the same level as the previous squads

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
        public (GameObject, Ship) InstantiateShip(string type)
        {
            Ship ship = null;
            GameObject instance = null;
            switch (type)
            {
                case "Barge":
                    instance = GameObject.Instantiate(Level.BargePrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Barge>();
                    break;
                case "Beehive":
                    instance = GameObject.Instantiate(Level.BeehivePrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Beehive>();
                    break;
                case "Bumblebee":
                    instance = GameObject.Instantiate(Level.BumblebeePrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Bumblebee>();
                    break;
                case "Carpenter Bee":
                    instance = GameObject.Instantiate(Level.CarpenterBeePrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<CarpenterBee>();
                    break;
                case "Carrier":
                    instance = GameObject.Instantiate(Level.CarrierPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Carrier>();
                    break;
                case "Cruiser":
                    instance = GameObject.Instantiate(Level.CruiserPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Cruiser>();
                    break;
                case "Dreadnought":
                    instance = GameObject.Instantiate(Level.DreadnoughtPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Dreadnought>();
                    break;
                case "Drone":
                    instance = GameObject.Instantiate(Level.DronePrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Drone>();
                    break;
                case "Factory":
                    instance = GameObject.Instantiate(Level.FactoryPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Factory>();
                    break;
                case "Fire Ship":
                    instance = GameObject.Instantiate(Level.FireShipPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<FireShip>();
                    break;
                case "Flagship":
                    instance = GameObject.Instantiate(Level.FlagshipPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Flagship>();
                    break;
                case "Frigate":
                    instance = GameObject.Instantiate(Level.FrigatePrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Frigate>();
                    break;
                case "Gunship":
                    instance = GameObject.Instantiate(Level.GunshipPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Gunship>();
                    break;
                case "Honeybee":
                    instance = GameObject.Instantiate(Level.HoneybeePrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Honeybee>();
                    break;
                case "Hornet":
                    instance = GameObject.Instantiate(Level.HornetPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Hornet>();
                    break;
                case "Leafcutter":
                    instance = GameObject.Instantiate(Level.LeafcutterPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Leafcutter>();
                    break;
                case "Queen":
                    instance = GameObject.Instantiate(Level.QueenPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Queen>();
                    break;
                case "Scout":
                    instance = GameObject.Instantiate(Level.ScoutPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Scout>();
                    break;
                case "Striker":
                    instance = GameObject.Instantiate(Level.StrikerPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Striker>();
                    break;
                case "Warp Gate":
                    instance = GameObject.Instantiate(Level.WarpGatePrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<WarpGate>();
                    break;
                case "Wasp":
                    instance = GameObject.Instantiate(Level.WaspPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<Wasp>();
                    break;
                case "Yellow Jacket":
                    instance = GameObject.Instantiate(Level.YellowJacketPrefab, Vector2.zero, Quaternion.identity);
                    ship = instance.GetComponent<YellowJacket>();
                    break;
                default:
                    Debugger.Exception($"Tried to instanstiate a ship type ({type}) that doesn't exist");
                    break;
            }
            instance.transform.SetParent(Level.Map.transform);
            return (instance, ship);
        }
    }
}