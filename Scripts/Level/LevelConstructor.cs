using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using Assets.Scripts.Server;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Level

{
    public class LevelConstructor
    {
        public LevelStage Level;
        public LevelConstructor(LevelStage level)
        {
            Level = level;
        }
        public void RequestServerSetup()
        {
            Level.IsLevelSetupOnServer = false;
            Level.Socket.SendRequest(new SetupLevelRequest(new SetupLevel(ConfigData.GetLevel(), ConfigData.GetUserId(), ConfigData.Version),
                ConfigData.StandardMaxTimeOnQueue));
        }

        public void SetShips()
        {
            GameState state = Level.GetState();
            ConfigData.Ships = new Ships(ConfigData.GetFleetData(), ConfigData.GetSavedSquadsData());


            if (Level.IsTrainingNueralNetwork || Level.UseSemiRandomSquads || Level.UseFullyRandomSquads)
            {
                ConfigData.SquadsChosenForLevel.Clear();
            }
            else if (Level.ReplaceDeadShips)
            {
                // [note][testing] turn this off to do training without ships dying
                ConfigData.Ships.ReplaceDeadSquadShips();
            }

            if (ConfigData.SquadsChosenForLevel.Count == 0)
            {
                List<SavedSquad> preloadSquads = new List<SavedSquad>();
                if (Level.UseSemiRandomSquads)
                {

                    //List<int> humanIndexes = new List<int> {1, 2, 3, 4, 5, 6, 7, 16, 22 };
                    List<int> humanIndexes = new List<int> { 2, 50, 36 };
                    List<int> beeIndexes = new List<int> { 8, 11, 13, 15, 17, 21, 25, 46, 57, 71 };

                    int squadNumber = Level.SquadCount;
                    for (int i = 0; i < squadNumber; i++)
                    {
                        int chosenIndex = humanIndexes[Random.Range(0, humanIndexes.Count)];
                        SavedSquad squad = ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == chosenIndex);
                        if (squad != null)
                        {
                            preloadSquads.Add(squad);

                        }
                        humanIndexes.Remove(chosenIndex);
                    }
                    for (int i = 0; i < squadNumber; i++)
                    {
                        int chosenIndex = beeIndexes[Random.Range(0, beeIndexes.Count)];
                        SavedSquad squad = ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == chosenIndex);
                        if (squad != null)
                        {
                            preloadSquads.Add(squad);

                        }
                        beeIndexes.Remove(chosenIndex);
                    }
                } else if (Level.UseFullyRandomSquads) {

                    int squadNumber =  Random.Range(1, Level.SquadCount);

                    for (int side = 1; side < 3; side++)
                    {
                        for (int i = 0; i < squadNumber; i++)
                        {
                            string type = ConfigData.Configuration.VisibleBeeShipTypes.ElementAt(Random.Range(0, ConfigData.Configuration.VisibleBeeShipTypes.Count));
                            if (side == 2)
                            {
                                type = ConfigData.Configuration.VisibleHumanShipTypes.ElementAt(Random.Range(0, ConfigData.Configuration.VisibleHumanShipTypes.Count));
                            }
                            int squadId = -1 * Utilities.RandomInt(1000000);
                            SavedSquad savedSquad = new SavedSquad(squadId, side, $"{type}s #{squadId}", Vector2.zero, false, false,
                                ConfigData.StartingSettings.DefaultShootingStrategy, ConfigData.UnsetColor, null);
                            savedSquad.SetupRandomShips(type);
                            preloadSquads.Add(savedSquad);
                            //Squad randomSquad = Level.gameObject.AddComponent<Squad>();
                            //randomSquad.Setup(
                            //    Level,
                            //    savedSquad,
                            //    savedSquad.ChosenShootingStrategy,
                            //    savedSquad.CeaseFire,
                            //    savedSquad.IsMatchingSpeed,
                            //    (int)Utilities.Hash() + ConfigData.Ships.GetSavedSquads().Count,
                            //    savedSquad.Side,
                            //    state.GetSquadsBySide(side).Count + 1,
                            //    savedSquad.Name,
                            //    savedSquad.Color
                            //);
                            //state.AddSquad(randomSquad);
                            ////Debugger.Log($"Making a random squad for Side #{side} of type {type}");
                            //randomSquad.SetupRandomSquadShips(type);
                        }
                    }


                }
                else
                {
                    preloadSquads = new List<SavedSquad> {
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 2),

                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 39),  // human squad // 2 gunships #39
                        ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 36),  // human squad // 1 scout #36
                        ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 41),  // human squad // 1 gunship #41
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 43),  // human squad // 3 gunships #43
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 45),  // human squad // 2 dreadnoughts #45
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 47),  // human squad // 1 frigate #47
                        ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 49),  // human squad // 1 barge #49
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 50),  // human squad // 1 cruiser #50
                        ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 51),  // human squad // 1 flagship #51
                        ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 52),  // human squad // 1 carrier #52
                        ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 53),  // human squad // 1 fire ship #53
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 58),  // human squad // 1 dreadnought #58
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 59),  // human squad // 4 scouts #59



                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 40), // bee squad // 4 hornets #40
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 37), // bee squad // 1 hornet #37
                        ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 42), // bee squad // 1 wasp #42
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 44), // bee squad // 2 wasps #44
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 46), // bee squad // 2 leafcutters #46
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 48), // bee squad // 1 leafcutters #48
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 54), // bee squad // 1 bumblebee #54
                        ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 55), // bee squad // 1 queen #55
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 56), // bee squad // 1 honeybee #56
                        ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 57), // bee squad // 4 yellow jackets #57
                        //ConfigData.Ships.GetSavedSquads().FirstOrDefault((s) => s.Id == 60), // bee squad // 4 honeybees #60

                    };
                }

                

                preloadSquads.ForEach((s) =>
                {
                    if (s != null && !ConfigData.SquadsChosenForLevel.Contains(s))  
                    {
                        ConfigData.SquadsChosenForLevel.Add(s);
                    }
                });
            }


            // play as Bees
            //ConfigData.SwapSides();

            //Debugger.Log($"There are {ConfigData.SquadsChosenForLevel.Count} squads loaded for this level");

            // Setup entities on the level
            SetupShipsAndSquads();
            if (state.GetSquadsBySide(ConfigData.Configuration.UserSide).Count > 0 && state.GetSquadsBySide(ConfigData.Configuration.AISide).Count > 0 && !Level.IsTrainingNueralNetwork)
            {
                state.SelectSquad(state.GetSquadByNumber(ConfigData.Configuration.UserSide, 1));
            }
            else if (!Level.IsTrainingNueralNetwork)
            {
                Debugger.Log($"User squads: {state.GetSquadsBySide(ConfigData.Configuration.UserSide).Count}, AI squads: {state.GetSquadsBySide(ConfigData.Configuration.AISide).Count}");
                Level.Menus.NoAliveShipsAlert.SetActive(true);
            }
        }

        private void SetupShipsAndSquads()
        {
            GameState state = Level.GetState();
            ConfigData.SquadsChosenForLevel.OrderByDescending((s) => s.Side).ToList().ForEach((savedSquad) =>
            {
                //Debugger.Log($"The squad is {savedSquad.Name}");
                Squad squad = savedSquad.ToSquad(Level);

                // [note][testing] turn this off to do training without ships dying
                //List<SquadShip> 
                List<SquadShip> ships = new List<SquadShip>();
                if (Level.ReplaceDeadShips)
                {
                    ships = savedSquad.GetShips().Where((s) => !s.GetFleetShip().IsDead).ToList();
                }
                else
                {
                    ships = savedSquad.GetShips();
                }
                if (ships.Count > 0)  
                {
                    //Debugger.Log($"There are {ships.Count} ships in {squad.Name}");
                    state.AddSquad(squad);
                    squad.SquadNumber = state.GetSquadsBySide(squad.Side).Count;

                    // loop through the squadships in each saved squad 

                    // .Where((s) => !s.GetFleetShip().IsDead).ToList() [alert] use this to remove dead ships
                    ships.ForEach((squadShip) =>
                    {
                        //Debugger.Log($"This ship is {squadShip.ShipType}");
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
                        if (ship != null && instance != null)
                        {
                            ship.Setup(
                                Level,
                                state.EntityCount++,
                                fleetShip,
                                squad,
                                squadShip.Offset
                            );



                            squad.AddShip(ship);
                            ship.SetColor();

                            if (ship.ShipType == "Carrier")
                            {
                                //Debugger.Log("Spawned a carrier");

                                // spawn drones
                                CarrierSquad droneSquad = Level.gameObject.AddComponent<CarrierSquad>();
                                droneSquad.Setup(
                                    Level,
                                    null,
                                    ship.Squad.GetShootingStrategy(),
                                    ship.Squad.CeaseFire,
                                    ship.Squad.IsMatchingSpeed,
                                    (int)Utilities.Hash() + ConfigData.Ships.GetSavedSquads().Count,
                                    ship.Squad.Side,
                                    state.GetSquadsBySide(ship.Side).Count + 1,
                                    $"{ship.Squad.Name} - Drone Force",
                                    ship.Squad.Color
                                );
                                state.AddSquad(droneSquad);
                                droneSquad.SetupCarrierSquad((Carrier)ship, "Drone");


                                // spawn strikers
                                CarrierSquad strikerSquad = Level.gameObject.AddComponent<CarrierSquad>();
                                strikerSquad.Setup(
                                    Level,
                                    null,
                                    ship.Squad.GetShootingStrategy(),
                                    ship.Squad.CeaseFire,
                                    ship.Squad.IsMatchingSpeed,
                                    (int)Utilities.Hash() + ConfigData.Ships.GetSavedSquads().Count,
                                    ship.Squad.Side,
                                    state.GetSquadsBySide(ship.Side).Count + 1,
                                    $"{ship.Squad.Name} - Striker Force",
                                    ship.Squad.Color
                                );
                                state.AddSquad(strikerSquad);
                                strikerSquad.SetupCarrierSquad((Carrier)ship, "Striker");


                                if (squad.IsMatchingSpeed)
                                {
                                    strikerSquad.MatchSpeed();
                                    droneSquad.MatchSpeed();
                                }
                                ship.AdditionalTsv = droneSquad.Tsv + strikerSquad.Tsv;
                            }
                        }
                        else
                        {
                            Debugger.Exception($"The instantiated ship ({fleetShip.Type}) was null. Ship: {ship}, Instance: {instance}");
                        }
                        
                    });
                    if (squad.IsMatchingSpeed)
                    {
                        squad.MatchSpeed();
                    }
                    // set initial tsv
                    state.InitialTsv[squad.Side - 1] += squad.Tsv;
                    //Debugger.Log($"Increase side TSV by {squad.Tsv} / {state.InitialTsv[squad.Side - 1]}");
                }

            });
            PositionSquads(state.GetSquadsBySide(ConfigData.Configuration.UserSide));
            PositionSquads(state.GetSquadsBySide(ConfigData.Configuration.AISide));

            // set the fleetships for the carrier ships
            state.GetSquadsBySide(ConfigData.Configuration.HumanSide).ForEach((squad) =>
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

            //Debugger.Log($"Initial TSV is Humans: {Level.GetState().InitialTsv[ConfigData.Configuration.HumanSide-1]}, " +
            //    $"Bees: {Level.GetState().InitialTsv[ConfigData.Configuration.BeeSide-1]}");
            //Debugger.PrintList(HasBeeTypes);
        }
        private void PositionSquads(List<Squad> squads)
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
            squads.ForEach((squad) =>
            {
                if (squad.GetShips().Any())
                {
                    squad.SetStartingPosition(squad.StartingPosition);

                }
            });
            if (Level.IsTrainingNueralNetwork)
            {
                //return; // [alert] [rl-training] only do this for rl learning
            }

            squads.ForEach((squad) =>
            {
                if (!squad.GetShips().Any())
                {
                    return;

                }
                //Debugger.Log("----------------------------------------------------------------------------------------------------------------------------\n");
                Vector2 position = squad.StartingPosition;
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

                    //Debugger.Log($"Positioned first squad: {squad.Name}");
                }
                else
                {
                //    Debugger.Log($"{squad.Name} is potentially located at {position} before changing the y level. It's left side is at {leastWidth} and it's right side is at {greatestWidth}. It's " +
                //$"{squad.GetWidth()} wide and {squad.GetHeight()} tall");
                    position.y = firstLevelSquad.GetPosition().y; // set the squad on the same y level as the last squad

                    // adjust the position left or right from center based on how many squads have already been placed on this level
                    if (horizontalSteps % 2 == 0)
                    {
                        rightWidth += halfWidth + 10;
                        rightWidth += previousRightSquad.GetWidth() / 2;
                        position.x += rightWidth;
                        previousRightSquad = squad;
                        //Debugger.Log($"Moving {squad.Name} {rightWidth} points to the right. {halfWidth} points contributed from its own half width.");
                    }
                    else
                    {
                        leftWidth += halfWidth + 10;
                        leftWidth += previousLeftSquad.GetWidth() / 2;
                        position.x += -1 * leftWidth;
                        previousLeftSquad = squad;
                        //Debugger.Log($"Moving {squad.Name} {leftWidth} points to the left. {halfWidth} points contributed from its own half width.");

                    }


                }
                // calculate the furthest extents of the squad and add 10 for good margin. Use it to determine if the squad is over the horizontal edges
                greatestWidth = position.x + halfWidth + 0;
                leastWidth = position.x - (halfWidth + 0);

                //Debugger.Log($"{squad.Name} is potentially located at {position}. It's left side is at {leastWidth} and it's right side is at {greatestWidth}. It's " +
                //$"{squad.GetWidth()} wide and {squad.GetHeight()} tall");

                if (greatestWidth > Level.MaxX || leastWidth < Level.MinX)
                {
                    // Debug Log how much the squad is out of bounds horizontally
                    if (greatestWidth > Level.MaxX)
                    {
                        //Debugger.Log($"{squad.Name} is over the margin of MaxX by {Mathf.Abs(greatestWidth - MaxX)}");
                    }
                    else
                    {
                        //Debugger.Log($"{squad.Name} is over the margin of MinX by {Mathf.Abs(leastWidth - MinX)}");
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

                    //Debugger.Log(
                    //$"Because {squad.Name} is over the margin it is being relocated to {position}. It's left side is at {leastWidth} and it's right side is at {greatestWidth}.\n " +
                    //$"It's topSide is at {(position.y + halfHeight)} and it's bottom side is at {(position.y - halfHeight)}. it has a half height of {halfHeight} and a halfWidth of {halfWidth}. \n" +
                    //$" It has been moved up from the original position by {bottomHeight}"
                    //);

                    firstLevelSquad = squad; // set this squad as the first level squad of this level
                    //Debugger.Log($"Positioned first squad on this level: {squad.Name}");
                }

                //Debugger.Log($"The previous squad {previousSquad.Name} was centered on {previousSquad.GetPosition()} and has a half width of {previousSquad.GetWidth() / 2}. " +
                //   $"The current squad {squad.Name} will be centered on {position} and have a half width of {halfWidth}. ");



                // set the position
                squad.SetStartingPosition(position);
                horizontalSteps++;
                squad.SetOffsets();
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