
using Assets.Scripts.Level;
using Assets.Scripts.Scenes;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public class SavedSquad : ICloneable
    {
        /// <summary>
        /// Unique identifier for the squad. A negative Id indicates a randomly generated squad
        /// </summary>
        public int Id;
        public int Side;
        public string Name;
        public Color Color;
        public Vector2 StartingPosition;
        public bool CeaseFire, IsMatchingSpeed, IsSetToChase, HasBeenSavedToStorage, HasCustomColor;
        public string ChosenShootingStrategy = ConfigData.StartingSettings.DefaultShootingStrategy;
        public SquadStatBlock Stats;
        private List<SquadShip> _ships = new List<SquadShip>();
        private bool _hasChanged;

        public bool HasMaxShips => GetSquadShips().Count == ConfigData.Configuration.MaxSquadSize;
        public bool IsEmptySquad => GetSquadShips().Count == 0 && Name == "" && !CeaseFire && !IsMatchingSpeed;
        public bool HasChanged => _hasChanged;
        public bool HasDeadShips => GetDeadShips().Any();
        public bool HasAliveShips => GetDeadShips().Count < GetSquadShips().Count;
        public bool HasShips => GetSquadShips().Any();

        public SavedSquad(int id, int side, string name, Vector2 startingPosition, bool ceaseFire, bool isMatchingSpeed, 
            string chosenShootingStrategy, Color color, SquadStatBlock stats = null)
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

        public void SetupRandomShips(string squadType)
        {
            int shipCount = 10;
            if ((new List<string> { "Queen", "Fire Ship", "Carrier", "Flagship", "Warp Gate", "Beehive" }).Contains(squadType))
            {
                shipCount = 1;
            }
            else if ((new List<string> { "Bumblebee", "Barge", "Carpenter Bee", "Factory", "Honeybee", "Scout" }).Contains(squadType))
            {
                shipCount = UnityEngine.Random.Range(1, 4);
            }
            else if ((new List<string> { "Leafcutter", "Wasp", "Cruiser", "Dreadnought", "Frigate", "Gunship" }).Contains(squadType))
            {
                shipCount = UnityEngine.Random.Range(2, 8);
            }
            else if ((new List<string> { "Hornet", "Yellow Jacket" }).Contains(squadType))
            {
                shipCount = UnityEngine.Random.Range(4, 10);
            }

            for (int shipIndex = 0; shipIndex < shipCount; shipIndex++)
            {
                int id = Utilities.GetNegativeFleetshipId();

                FleetShip fleetShip = new FleetShip(id, Side, $"{squadType} - #{id}", squadType, false, true, false, 0, 0, 0, 0, 0, 0, 0);
                Vector2 offset = ConfigData.GeneratedSquadFormationOffsets[shipIndex];
                SquadShip squadShip = new SquadShip(fleetShip.Id, fleetShip.Type, offset, this);
                AddShipToSquad(squadShip);

            }
        }
        public List<SquadShip> GetSquadShips()
        {
            return _ships;
        }
        public SquadShip GetShip(int fleetId)
        {
            return GetSquadShips().FirstOrDefault((ship) => ship.FleetId == fleetId);
        }
        public SquadShip GetMostValuableShip()
        {
            return GetSquadShips().OrderByDescending(s => s.GetFleetShip().GetMaxTsv()).ToList().First();
        }
        public void AddShipToSquad(SquadShip ship)
        {
            if (!HasShip(ship.GetFleetShip()))
            {
                _ships.Add(ship);
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
        public int GetTsv()
        {
            return GetSquadShips().Sum((s) => s.GetFleetShip().GetTsv());
        }
        public int GetCapacity()
        {
            return GetSquadShips().Sum((s) => s.GetFleetShip().GetCapacity());
        }
        public int GetMaxCapacity()
        {
            return GetSquadShips().Sum((s) => s.GetFleetShip().GetMaxCapacity());
        }
        public int GetMaxTsv()
        {
            return GetSquadShips().Sum((s) => s.GetFleetShip().GetMaxTsv());
        }
        public bool Equals(SavedSquad squad)
        {
            return squad.Id == Id;
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
        public Squad ToSquad(LevelStage level)
        {
            Squad squad = level.gameObject.AddComponent<Squad>();
            squad.Setup(
                level,
                HasBeenSavedToStorage ? ConfigData.AllShips.GetSavedSquad(Id) : this,
                ChosenShootingStrategy,
                CeaseFire,
                IsMatchingSpeed,
                IsSetToChase,
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
                $" \"{ChosenShootingStrategy}\", \"Stats\": {Stats.ToJson()}, \"Ships\": [";
            GetSquadShips().ForEach((s) => json += $"{s.ToJson()}, ");
            json = json.Remove(json.Length - 2);
            json += "]}";
            return json;
        }
        public override string ToString()
        {
            return $"{Name} - #{Id}, Ships: {GetSquadShips().Count}";
        }
    }
}