using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using Assets.Scripts.Server;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public class LevelConstructor
    {
        public Level Level;
        public Stage Stage;

        private readonly List<SavedSquad> _overrideSquads = new List<SavedSquad>();
        private readonly List<Squad> _setupSquads = new List<Squad>();
        private readonly List<Ship> _carriers = new List<Ship>();
        private readonly List<SquadShip> _liveSquadShips = new List<SquadShip>();

        public LevelConstructor(Level level)
        {
            Level = level;
            Stage = Level.Stage;
        }

        public void RequestServerSetup()
        {
            Level.IsLevelSetupOnServer = false;
            ConfigData.Socket.SendRequest(new SetupLevelRequest(
                new SetupLevel(
                    ConfigData.CurrentGameMode == ConfigData.GameModes.FreePlay ||
                    ConfigData.CurrentGameMode == ConfigData.GameModes.FishTank
                        ? -1
                        : ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.UserSide),
                    ConfigData.GetUserId()),
                ConfigData.StandardMaxTimeOnQueue,
                Level));
        }

        public void SetCarrierShipFleetships(List<Squad> squads)
        {
            for (int squadIndex = 0; squadIndex < squads.Count; squadIndex++)
            {
                if (!(squads[squadIndex] is CarrierSquad carrierSquad))
                {
                    continue;
                }

                List<Ship> ships = carrierSquad.GetShips();
                for (int shipIndex = 0; shipIndex < ships.Count; shipIndex++)
                {
                    CarrierShip carrierShip = (CarrierShip)ships[shipIndex];
                    carrierShip.CarrierShipSetup(
                        carrierSquad.Carrier.FleetShip,
                        carrierSquad.CarrierSquadType,
                        carrierSquad.Carrier);
                }
            }
        }

        public void AddOverrideSquads(int side)
        {
            _overrideSquads.Clear();
            AddOverrideSquadIfPresent(side, 8);
            AddOverrideSquadIfPresent(side, 20);
            AddOverrideSquadIfPresent(side, 2);
            AddOverrideSquadIfPresent(side, 19);

            if (side == ConfigData.Configuration.AISide)
            {
                AddUniqueSquads(_overrideSquads, Level.CurrentLevelOptions.EnemySquads);
                if (Level.CurrentLevelOptions.EnemyReinforcementsOption == 1)
                {
                    AddUniqueSquads(_overrideSquads, Level.CurrentLevelOptions.EnemyReinforcements);
                }
            }
            else
            {
                AddUniqueSquads(_overrideSquads, Level.CurrentLevelOptions.ChosenSquads);
            }
        }

        private void AddOverrideSquadIfPresent(int side, long id)
        {
            List<SavedSquad> squads = ConfigData.CurrentShips.GetSavedSquads();
            for (int i = 0; i < squads.Count; i++)
            {
                SavedSquad squad = squads[i];
                if (squad.Side == side && squad.Id == id)
                {
                    _overrideSquads.Add(squad);
                    return;
                }
            }
        }

        private static void AddUniqueSquads(List<SavedSquad> source, List<SavedSquad> destination)
        {
            for (int i = 0; i < source.Count; i++)
            {
                SavedSquad squad = source[i];
                if (squad != null && !destination.Contains(squad))
                {
                    destination.Add(squad);
                }
            }
        }

        public void SpawnShipsAndSquads(
            List<SavedSquad> squads,
            Vector2 startingPosition,
            Vector2 moveToPoint,
            bool squadsAreReinforcements)
        {
            _setupSquads.Clear();
            _carriers.Clear();
            Level.AllSquads.AddRange(squads);

            for (int savedSquadIndex = 0; savedSquadIndex < squads.Count; savedSquadIndex++)
            {
                SavedSquad savedSquad = squads[savedSquadIndex];
                Squad squad = savedSquad.ToSquad(Level);
                _setupSquads.Add(squad);

                _liveSquadShips.Clear();
                List<SquadShip> savedShips = savedSquad.GetSquadShips();
                for (int shipIndex = 0; shipIndex < savedShips.Count; shipIndex++)
                {
                    SquadShip squadShip = savedShips[shipIndex];
                    if (!squadShip.GetFleetShip().IsDead)
                    {
                        _liveSquadShips.Add(squadShip);
                    }
                }

                if (_liveSquadShips.Count == 0)
                {
                    continue;
                }

                squad.SquadNumber = Level.State.OriginalSquadCounts[squad.Side - 1] + 1;
                squad.SetSquadTab();
                Level.State.AddSquad(squad);

                for (int shipIndex = 0; shipIndex < _liveSquadShips.Count; shipIndex++)
                {
                    SquadShip squadShip = _liveSquadShips[shipIndex];
                    FleetShip fleetShip = squadShip.GetFleetShip();
                    Ship ship = InstantiateShip(fleetShip.Type);
                    ship.Setup(Level, fleetShip, squad, squadShip.Offset);
                    squad.AddShip(ship);
                    ship.SetColor();

                    if (ship.ShipType == ConfigData.ShipTypes.Carrier)
                    {
                        _carriers.Add(ship);
                    }
                }

                if (squad.IsMatchingSpeed)
                {
                    squad.MatchSpeed();
                }
                if (squad.CeaseFire)
                {
                    squad.SetSquadCeaseFire(true);
                }
                if (squad.ShouldChase())
                {
                    squad.SetChase(true);
                }
                Level.State.InitialTsv[squad.Side - 1] += squad.Tsv;
            }

            for (int carrierIndex = 0; carrierIndex < _carriers.Count; carrierIndex++)
            {
                Carrier carrier = (Carrier)_carriers[carrierIndex];
                // Normal gameplay has no RL cap. In training, each Carrier gets only the
                // slots left after all initial roster ships and earlier Carrier children.
                int available = global::RlOneVsOneTrainingBootstrap.GetAvailableSpawnSlots(Level, carrier.Side);
                if (global::RlOneVsOneTrainingBootstrap.IsActiveFor(Stage))
                {
                    // Split finite deck slots across multiple friendly Carriers instead of
                    // giving the first Carrier the whole complement.
                    int carriersRemaining = _carriers.Count - carrierIndex;
                    available = available / carriersRemaining +
                        (available % carriersRemaining == 0 ? 0 : 1);
                }
                int droneMaximum = Mathf.Max(0, ConfigData.Configuration.CarrierCarryDroneMax);
                int strikerMaximum = Mathf.Max(0, ConfigData.Configuration.CarrierCarryStrikerMax);

                for (int squadIndex = 0;
                     squadIndex < ConfigData.Configuration.CarrierSquadCount && available > 0;
                     squadIndex++)
                {
                    int drones = 0;
                    int strikers = 0;
                    // Allocate Drone, Striker, Drone, Striker until the team is full
                    // or this pair of Carrier squads reaches its natural capacities.
                    while (available > 0 && (drones < droneMaximum || strikers < strikerMaximum))
                    {
                        if (drones < droneMaximum && available > 0)
                        {
                            drones++;
                            available--;
                        }
                        if (strikers < strikerMaximum && available > 0)
                        {
                            strikers++;
                            available--;
                        }
                    }

                    CarrierSquad droneSquad = drones > 0
                        ? AddCarrierChildSquad(carrier, ConfigData.ShipTypes.Drone, squadIndex + 1)
                        : null;
                    CarrierSquad strikerSquad = strikers > 0
                        ? AddCarrierChildSquad(carrier, ConfigData.ShipTypes.Striker, squadIndex + 1)
                        : null;

                    for (int shipIndex = 0; shipIndex < Mathf.Max(drones, strikers); shipIndex++)
                    {
                        if (shipIndex < drones)
                        {
                            droneSquad.AddInitialCarrierShip();
                        }
                        if (shipIndex < strikers)
                        {
                            strikerSquad.AddInitialCarrierShip();
                        }
                    }

                    if (droneSquad != null)
                    {
                        droneSquad.CompleteInitialCarrierSquad();
                    }
                    if (strikerSquad != null)
                    {
                        strikerSquad.CompleteInitialCarrierSquad();
                    }
                    if (carrier.Squad.IsMatchingSpeed)
                    {
                        droneSquad?.MatchSpeed();
                        strikerSquad?.MatchSpeed();
                    }
                }
            }

            PositionSquads(_setupSquads, startingPosition, moveToPoint, squadsAreReinforcements);

            if (_carriers.Count > 0)
            {
                SetCarrierShipFleetships(_setupSquads);
            }
        }

        private CarrierSquad AddCarrierChildSquad(Carrier carrier, ConfigData.ShipTypes childType, int squadNumber)
        {
            CarrierSquad childSquad = Stage.Pool.GetCarrierSquadFromPool();
            childSquad.Setup(
                Level,
                carrier.Squad.SavedSquad,
                carrier.Squad.GetShootingStrategy(),
                carrier.Squad.CeaseFire,
                carrier.Squad.IsMatchingSpeed,
                carrier.Squad.ShouldChase(),
                false,
                TransientIdAllocator.GetSavedSquadId(),
                carrier.Side,
                Level.State.OriginalSquadCounts[carrier.Side - 1] + 1,
                $"{carrier.Squad.Name} - {(childType == ConfigData.ShipTypes.Drone ? "Drone" : "Striker")} Force #{squadNumber}",
                carrier.Squad.Color);
            Level.State.AddSquad(childSquad);
            _setupSquads.Add(childSquad);
            childSquad.SetupCarrierSquad(carrier, childType);
            return childSquad;
        }

        private void PositionSquads(
            List<Squad> squads,
            Vector2 startingPosition,
            Vector2 moveToPoint,
            bool squadsAreReinforcements)
        {
            Squad firstLevelSquad = null;
            Squad previousLeftSquad = null;
            Squad previousRightSquad = null;
            bool firstSquad = true;
            int horizontalSteps = 0;
            float rightWidth = 0;
            float leftWidth = 0;
            float bottomHeight = 0;
            float halfWidth;
            float halfHeight;

            for (int i = 0; i < squads.Count; i++)
            {
                squads[i].SetStartingPosition(startingPosition);
            }

            for (int i = 0; i < squads.Count; i++)
            {
                Squad squad = squads[i];
                if (squad.GetShips().Count == 0)
                {
                    continue;
                }

                Vector2 position = squad.GetPosition();
                halfWidth = squad.GetWidth() / 2;
                halfHeight = squad.GetHeight() / 2;

                if (firstSquad)
                {
                    firstSquad = false;
                    firstLevelSquad = squad;
                    previousLeftSquad = squad;
                    previousRightSquad = squad;
                }
                else
                {
                    position.y = firstLevelSquad.GetPosition().y;
                    if (horizontalSteps % 2 == 0)
                    {
                        rightWidth += halfWidth + 10;
                        rightWidth += previousRightSquad.GetWidth() / 2;
                        position.x += rightWidth;
                        previousRightSquad = squad;
                    }
                    else
                    {
                        leftWidth += halfWidth + 10;
                        leftWidth += previousLeftSquad.GetWidth() / 2;
                        position.x -= leftWidth;
                        previousLeftSquad = squad;
                    }
                }

                float greatestWidth = position.x + halfWidth;
                float leastWidth = position.x - halfWidth;

                if (!squadsAreReinforcements && (greatestWidth > Level.MaxX || leastWidth < Level.MinX))
                {
                    position.x = squad.StartingPosition.x;
                    bottomHeight = halfHeight + (firstLevelSquad.GetHeight() / 2);
                    position.y += bottomHeight + 20;
                    rightWidth = halfWidth;
                    leftWidth = halfWidth;
                    horizontalSteps = 0;
                    firstLevelSquad = squad;
                }

                squad.SetStartingPosition(position);
                horizontalSteps++;
                squad.SetOffsets();
                if (moveToPoint != Vector2.zero)
                {
                    squad.Move(moveToPoint);
                }
            }
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
                case ConfigData.ShipTypes.HumanTarget:
                    ship = Stage.Pool.HumanTargetPool.Get();
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
