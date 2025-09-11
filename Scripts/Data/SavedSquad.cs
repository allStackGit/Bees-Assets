
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public class SavedSquad : ICloneable
    {
        /// <summary>
        /// Unique identifier for the squad. A negative Id indicates a randomly generated squad
        /// </summary>
        public long Id;
        public int Side;
        public string Name;
        public Color Color;
        public Vector2 StartingPosition;
        public bool CeaseFire, IsMatchingSpeed, IsSetToChase, HasBeenSavedToStorage, HasCustomColor;
        public ConfigData.ShootingStrategyTypes ChosenShootingStrategy = ConfigData.DefaultShootingStrategy;
        public SquadStatBlock Stats;
        private List<SquadShip> _ships = new List<SquadShip>();
        private bool _hasChanged;

        // Not saved to JSON
        public bool IsLoadedIntoLevel;

        public bool HasMaxShips => GetSquadShips().Count == ConfigData.Configuration.MaxSquadSize;
        public bool IsEmptySquad => GetSquadShips().Count == 0 && Name == "" && !CeaseFire && !IsMatchingSpeed;
        public bool HasChanged => _hasChanged;
        public bool HasDeadShips => GetDeadShips().Any();
        public bool HasAliveShips => GetDeadShips().Count < GetSquadShips().Count;
        public bool HasShips => GetSquadShips().Any();

        public SavedSquad(long id, int side, string name, Vector2 startingPosition, bool ceaseFire, bool isMatchingSpeed,
            ConfigData.ShootingStrategyTypes chosenShootingStrategy, Color color, SquadStatBlock stats = null)
        {
            this.Id = id;
            this.Side = side;
            this.Name = name;
            this.StartingPosition = startingPosition;
            this.CeaseFire = ceaseFire;
            this.IsMatchingSpeed = isMatchingSpeed;
            this.ChosenShootingStrategy = chosenShootingStrategy;
            this.Color = color;
            this.Stats = stats;
            if (stats == null)
            {
                Stats = new SquadStatBlock(Utilities.GenerateCommanderName(), 0, 0, 0, 0, 0, 0);
            }
            if (Id > -1)
            {
                HasBeenSavedToStorage = true;
            }
            if (Color != ConfigData.UnsetColor)
            {
                HasCustomColor = true;
            }
        }

        public void SetupRandomShips(ConfigData.ShipTypes squadType)
        {
            int shipCount = 10;
            if ((new List<ConfigData.ShipTypes> { ConfigData.ShipTypes.Queen, ConfigData.ShipTypes.FireBarge, ConfigData.ShipTypes.Carrier, ConfigData.ShipTypes.Flagship, ConfigData.ShipTypes.WarpGate, ConfigData.ShipTypes.Beehive }).Contains(squadType))
            {
                shipCount = 1;
            }
            else if ((new List<ConfigData.ShipTypes> { ConfigData.ShipTypes.Bumblebee, ConfigData.ShipTypes.Barge, ConfigData.ShipTypes.CarpenterBee, ConfigData.ShipTypes.Factory, ConfigData.ShipTypes.Honeybee, ConfigData.ShipTypes.Scout }).Contains(squadType))
            {
                shipCount = UnityEngine.Random.Range(1, 4);
            }
            else if ((new List<ConfigData.ShipTypes> { ConfigData.ShipTypes.Leafcutter, ConfigData.ShipTypes.Wasp, ConfigData.ShipTypes.Cruiser, ConfigData.ShipTypes.Dreadnought, ConfigData.ShipTypes.Frigate, ConfigData.ShipTypes.Gunship }).Contains(squadType))
            {
                shipCount = UnityEngine.Random.Range(2, 8);
            }
            else if ((new List<ConfigData.ShipTypes> { ConfigData.ShipTypes.Hornet, ConfigData.ShipTypes.YellowJacket }).Contains(squadType))
            {
                shipCount = UnityEngine.Random.Range(4, 10);
            }

            Vector2[] offsets = ConfigData.GeneratedSquadFormationOffsets4x4;

            if (shipCount <= 4)
            {
                if (ConfigData.LargeShips.Contains(squadType))
                {
                    offsets = ConfigData.GeneratedSquadFormationOffsets2x2Large;
                }
                else
                {
                    offsets = ConfigData.GeneratedSquadFormationOffsets2x2;
                }
            }
            if (ConfigData.MediumShips.Contains(squadType))
            {
                offsets = ConfigData.GeneratedSquadFormationOffsets4x4Medium;
            }

            for (int shipIndex = 0; shipIndex < shipCount; shipIndex++)
            {
                long id = Utilities.GetNegativeFleetshipId();

                FleetShip fleetShip = new FleetShip(id, squadType, false, true, false, 0, 0, 0, 0, 0, 0, 0);
                AddShipToSquad(new SquadShip(fleetShip.Id, fleetShip.Type, offsets[shipIndex]));

            }
        }
        public List<SquadShip> GetSquadShips()
        {
            return _ships;
        }
        public List<SquadShip> GetAliveSquadShips()
        {
            return GetSquadShips().Where((ship) => !ship.GetFleetShip().IsDead).ToList();
        }
        public SquadShip GetShip(long fleetId)
        {
            return GetSquadShips().FirstOrDefault((ship) => ship.FleetId == fleetId);
        }
        public SquadShip GetMostValuableShip()
        {
            return GetSquadShips().OrderByDescending(s => s.GetFleetShip().GetTsv()).First();
        }
        public void AddShipToSquad(SquadShip ship)
        {
            FleetShip fleetShip = ship.GetFleetShip();
            if (!HasShip(fleetShip))
            {
                //Debug.Log($"Adding {ship.GetFleetShip().Name} #{ship.GetFleetShip().Id} to {Name}");
                _ships.Add(ship);
                fleetShip.DoesBelongToSavedSquad = true;
            }
            
        }
        public void AutoRepositionSquad()
        {
            int shipCount = GetSquadShips().Count;
            Vector2[] offsets = ConfigData.GeneratedSquadFormationOffsets4x4;
            if (shipCount == 1)
            {
                offsets[0] = Vector2.zero;
            }
            else
            {
                if (shipCount <= 4)
                {
                    if (GetSquadShips().Any((s) =>  ConfigData.LargeShips.Contains(s.ShipType)))
                    {
                        offsets = ConfigData.GeneratedSquadFormationOffsets2x2Large;
                    }
                    else
                    {
                        offsets = ConfigData.GeneratedSquadFormationOffsets2x2;
                    }
                }
                if (GetSquadShips().Any((s) => ConfigData.MediumShips.Contains(s.ShipType)))
                {
                    offsets = ConfigData.GeneratedSquadFormationOffsets4x4Medium;
                }
            }

            for (int i = 0; i < shipCount; i++)
            {
                GetSquadShips()[i].Offset = offsets[i];
            }
        }
        public void SetChanged(bool changed)
        {
            _hasChanged = changed;
        }
        public void RemoveShipFromSquad(SquadShip ship)
        {
            _ships.Remove(ship);
            if (_ships.Any())
            {
                StartingPosition = GetCenterPoint();
            }
            SetChanged(true);
        }
        public List<SquadShip> GetDeadShips()
        {
            return GetSquadShips().Where((ship) => ship.GetFleetShip().IsDead).ToList();
        }
        public bool HasShip(FleetShip ship)
        {
            //Debug.Log($"Fleetship: {ship}");
            return GetShip(ship.Id) != null;
        }
        public Vector2 GetLeftMostPoint()
        {
            List<SquadShip> ships = GetSquadShips().OrderBy((ship) => ship.GetLeftSide().x).ToList();
            SquadShip ship = ships.First();
            return ship.GetLeftSide();
        }
        public Vector2 GetRightMostPoint()
        {
            List<SquadShip> ships = GetSquadShips().OrderByDescending((ship) => ship.GetRightSide().x).ToList();
            SquadShip ship = ships.First();
            return ship.GetRightSide();
        }
        public Vector2 GetTopMostPoint()
        {
            List<SquadShip> ships = GetSquadShips().OrderByDescending((ship) => ship.GetTopSide().y).ToList();
            SquadShip ship = ships.First();
            return ship.GetTopSide();
        }
        public Vector2 GetBottomMostPoint()
        {
            List<SquadShip> ships = GetSquadShips().OrderBy((ship) => ship.GetBottomSide().y).ToList();
            SquadShip ship = ships.First();
            return ship.GetBottomSide();
        }
        public float GetWidth()
        {
            return Math.Abs(GetLeftMostPoint().x - GetRightMostPoint().x);
        }
        public float GetHeight()
        {
            return Math.Abs(GetTopMostPoint().y - GetBottomMostPoint().y);
        }
        public Vector2 GetCenterPoint()
        {

            // calculate width and height of box
            float width = GetWidth();
            float height = GetHeight();

            // calculate center point of box

            float midX = GetRightMostPoint().x - (width / 2);
            float midY = GetBottomMostPoint().y + (height / 2);

            return new Vector2(midX, midY);
        }

        // the ships need to orient around the center so that when they are loaded onto a map they have a reference position to the center of the squad
        public void OrientSquad()
        {
            //Debug.Log($"Before Orienting squad, center point: {StartingPosition}");

             StartingPosition = GetCenterPoint();
            //Debug.Log($"After Orienting squad, center point: {StartingPosition}");
            GetSquadShips().ForEach((ship) =>
            {
                //Debug.Log($"Squad ship offset before orienting around center: {ship.Offset}");
                ship.Offset.x = ship.Offset.x - StartingPosition.x;
                ship.Offset.y = ship.Offset.y - StartingPosition.y;
                //Debug.Log($"Squad ship offset after orienting around center: {ship.Offset}");
            });
        }
        /// <summary>
        /// The capacity of the squad as it stands now, potentially less than full capacity if the squad isn't filled
        /// </summary>
        /// <returns></returns>
        public int GetCapacity()
        {
            return GetAliveSquadShips().Sum((s) => s.GetFleetShip().GetCapacity());
        }
        /// <summary>
        /// The capacity of the squad if all the ships were alive and the squad was filled
        /// </summary>
        /// <returns></returns>
        public int GetMaxCapacity()
        {
            return GetSquadShips().Sum((s) => s.GetFleetShip().GetCapacity());
        }
        private SavedSquad _savedSquad;
        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
            _savedSquad = obj as SavedSquad;
            if (_savedSquad == null)
            {
                return false;
            }

            return Id == _savedSquad.Id;
        }

        public bool Equals(FleetShip other)
        {
            return Id == other.Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        public static bool operator ==(SavedSquad a, SavedSquad b)
        {
            // If both are null, or both are same instance, return true.
            if (System.Object.ReferenceEquals(a, b))
            {
                return true;
            }

            // If one is null, but not both, return false.
            if (((object)a == null) || ((object)b == null))
            {
                return false;
            }

            return a.Id == b.Id;
        }

        public static bool operator !=(SavedSquad a, SavedSquad b)
        {
            return !(a == b);
        }
        public object Clone()
        {
            SavedSquad clone = (SavedSquad)this.MemberwiseClone();
            clone.Stats = (SquadStatBlock) this.Stats.Clone();
            clone._ships = new List<SquadShip>();
            _ships.ForEach((ship) =>
            {
                clone.AddShipToSquad((SquadShip) ship.Clone());
            });
            if (clone.Id > -1)
            {
                clone.HasBeenSavedToStorage = true;
            }
            //clone.StartingPosition = new Vector2(StartingPosition.x, StartingPosition.y);
            return clone;
        }
        /// <summary>
        /// Converts a saved squad to an unlinked unsaved squad that has the exact same ships and features
        /// </summary>
        /// <returns></returns>
        public SavedSquad ConvertToUnsavedSquad()
        {
            SavedSquad convert = new SavedSquad(Utilities.GetNegativeSavedSquadId(), Side, Name, StartingPosition, CeaseFire, IsMatchingSpeed, ChosenShootingStrategy, Color);
            GetSquadShips().ForEach((squadShip) =>
            {
                FleetShip fleetShip = squadShip.GetFleetShip();
                FleetShip newFleetShip = new FleetShip(Utilities.GetNegativeFleetshipId(), fleetShip.Type, false, true, false, 0, 0, 0, 0, 0, 0, 0, fleetShip.Name);
                SquadShip newSquadShip = new SquadShip(newFleetShip.Id, newFleetShip.Type, squadShip.Offset);
                convert.AddShipToSquad(newSquadShip);
            });
            return convert;
        }
        public Squad ToSquad(Level level)
        {
            Squad squad = level.Stage.Pool.GetSquadFromPool();
            squad.Setup(
                level,
                HasBeenSavedToStorage ? ConfigData.CurrentShips.GetSavedSquad(Id) : this,
                ChosenShootingStrategy,
                CeaseFire,
                IsMatchingSpeed,
                IsSetToChase,
                false,
                Id,
                Side,
                0,
                Name,
                Color
            );

            return squad;
        }
        public string ToJson()
        {
            string json = $"{{\"Id\": {Id}, \"Side\": {Side}, \"Name\": \"{Name}\", \"Color\": {{\"r\": {Color.r}, \"g\": {Color.g}, \"b\": {Color.b}, \"a\": {Color.a} }}, \"StartingPosition\":" +
                $" {{\"x\": {StartingPosition.x}, \"y\": {StartingPosition.y} }}, \"CeaseFire\": {(CeaseFire ? "true" : "false")}, \"IsMatchingSpeed\": {(IsMatchingSpeed ? "true" : "false")}, \"ChosenShootingStrategy\":" +
                $" \"{Utilities.ConvertShootingStrategyTypeToName[ChosenShootingStrategy]}\", \"Stats\": {Stats.ToJson()}, \"Ships\": [";
            GetSquadShips().ForEach((s) => json += $"{s.ToJson()}, ");
            json = json.Remove(json.Length - 2);
            json += "]}";
            return json;
        }
        public override string ToString()
        {
            return $"{Name} - #{Id}, {GetSquadShips().Count} Ships: {Utilities.ListToString(GetSquadShips().Select((s) => s.GetFleetShip()).ToList())}";
        }
    }
}