using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public class AutoBuiltSquad 
    {
        public int Side;
        public SavedSquad Squad;
        public bool UseSwarm, PowerfulShipsFirst;
        public AutoBuiltSquad(int side, string formation, SavedSquad opposingSquad, bool useSwarm, bool powerfulShipsFirst)
        {
            Side = side;
            UseSwarm = useSwarm;
            PowerfulShipsFirst = powerfulShipsFirst;
            MakeMatchedSquad(opposingSquad);
            PositionShipsInSquad(formation);
        }
        //public AutoBuiltSquad(int side, string formation, List<string> shipTypeList)
        //{
        //    Side = side;
        //    MakeSquadFromShipTypeList(shipTypeList);
        //    PositionShipsInSquad(formation);
        //}

        // Make squad
        public SavedSquad MakeEmptySquad()
        {
            // ConfigData.GetUserProgressData().GetNextSavedSquadId()
            int id = Utilities.GetNegativeSavedSquadId();
            SavedSquad squad = new SavedSquad(id, Side, $"Squadron #{id}", Vector2.zero, false, false, 
                ConfigData.StartingSettings.DefaultShootingStrategy, ConfigData.UnsetColor, null);

            return squad;
        }
        public void AddFleetShipToSquad(FleetShip ship)
        {
            SquadShip squadShip = new SquadShip(ship.Id, ship.Type, Vector2.zero, Squad);
            Squad.AddShipToSquad(squadShip);
        }
        public List<FleetShip> MakeTSVEquivilentSquad(int maxTsv)
        {
            // pick a ship type
            HashSet<string> shipTypes = new HashSet<string>();
            List<FleetShip> ships = new List<FleetShip>();
            System.Random rnd = Utilities.GetRandom();

            int usedTsv = 0;
            int remainingTsv = maxTsv - usedTsv;
            int cheapestShipTsv = ConfigData.AllShips.GetAvailableShips().Where((s) => s.Side == Side).OrderBy((s) => s.GetMaxTsv()).First().GetMaxTsv();
            int mostExpensiveShipTsv = ConfigData.AllShips.GetAvailableShips().Where((s) => s.Side == Side).OrderByDescending((s) => s.GetMaxTsv()).First().GetMaxTsv();
            int shipIndex = 0;

            //Debug.Log($"The cheapest ship Tsv is {cheapestShipTsv}, the most expensive is {mostExpensiveShipTsv}");

            // sort ships
            if (PowerfulShipsFirst)
            {
                shipTypes = ConfigData.AllShips.GetAvailableShips().Where((s) => s.Side == Side).OrderByDescending((s) => s.Firepower).Select((s) => s.Type).ToHashSet();
            }
            else
            {
                if (Side == ConfigData.Configuration.HumanSide)
                {
                    if (UseSwarm)
                    {
                        shipTypes = new HashSet<string>() {"Gunship", "Carrier" }.OrderBy(s => rnd.Next()).ToHashSet();
                    }
                    else
                    {
                        shipTypes = ConfigData.Configuration.VisibleBeeShipTypes.OrderBy(s => rnd.Next()).ToHashSet();

                    }

                }
                else if (Side == ConfigData.Configuration.BeeSide)
                {
                    if (UseSwarm)
                    {
                        shipTypes = new HashSet<string>() { "Hornet", "Yellow Jacket", "Wasp" }.OrderBy(s => rnd.Next()).ToHashSet();
                    }
                    else
                    {
                        shipTypes = ConfigData.Configuration.VisibleBeeShipTypes.OrderBy(s => rnd.Next()).ToHashSet();

                    }
                }
                else
                {
                    Debugger.Exception($"Invalid side given: {Side}");
                }
            }
            //Debugger.PrintList(shipTypes.ToList());

            // add the ships
            int loopCount = 0;
            int maxLoops = 50;
            while (
                remainingTsv >= cheapestShipTsv && 
                shipIndex < shipTypes.Count && 
                loopCount < maxLoops
                )
            {
                loopCount++;
                string shipType = shipTypes.ElementAt(shipIndex);
                FleetShip ship = ConfigData.AllShips.GetAvailableShipsOfType(shipType).Where((s) => !Squad.HasShip(s)).First();
                int shipTsv = ship.GetMaxTsv();
                //Debug.Log($"Trying to add a number of {shipType} worth {shipTsv} each");

                // if the ship TSV is less than max, try to fill up with that ship type
                int innerLoopCount = 0;
                while (
                    ship != null && 
                    shipTsv <= remainingTsv && 
                    (((shipTsv * ConfigData.Configuration.MaxSquadSize) > maxTsv) || shipTsv == mostExpensiveShipTsv || UseSwarm) && // make sure that this ship type is either a) worth enough TSV when there are max ships, or b) the most expensive ship or c) we are using swarms
                    ships.Count < ConfigData.Configuration.MaxSquadSize && 
                    innerLoopCount < maxLoops
                    )
                {
                    innerLoopCount++;
                    ships.Add(ship);
                    AddFleetShipToSquad(ship);
                    usedTsv += shipTsv;
                    remainingTsv = maxTsv - usedTsv;
                    //Debug.Log($"Adding  {ship.Name} worth {shipTsv}. Used {usedTsv} Tsv and there is {remainingTsv} left.");

                    ship = ConfigData.AllShips.GetAvailableShipsOfType(shipType).Where((s) => !Squad.HasShip(s)).First();
                    shipTsv = ship.GetMaxTsv();
                }
                if (innerLoopCount >= maxLoops)
                {
                    Debug.Log($"Broke out of inner loop because we hit {maxLoops}");
                }
                shipIndex++;
            }
            if (loopCount >= maxLoops)
            {
                Debug.Log($"Broke out of outer loop because we hit {maxLoops}");
            }


            // if it's not, pick a different ship type
            return ships;
            
        }
        public void MakeMatchedSquad(SavedSquad opposingSquad)
        {
            Squad = MakeEmptySquad();
            int maxTsv = opposingSquad.GetMaxTsv();
            //Debug.Log($"Try to make a squad with {maxTsv} tsv");
            MakeTSVEquivilentSquad(maxTsv);

            if (Squad.GetShips().Count == 0)
            {
                Debugger.Exception($"Could not fill a TSV equivilent squad for {opposingSquad.Name}");
            }


        }
        //public FleetShip GetShipOfRange(int range)
        //{
        //    return ConfigData.Ships.GetAvailableShips().Where((s) => s.Range.Contains(range) && !Squad.HasShip(s)).FirstOrDefault();
        //}
        //public void MakeSquadFromShipTypeList(List<string> shipTypeList)
        //{
        //    Squad = MakeEmptySquad();
        //    shipTypeList.ForEach((shipType) =>
        //    {
        //        FillSquadWithShip(shipType, 1);
        //    });
        //}
        //public void FillSquadWithShip(string shipType, int shipCount)
        //{
        //    List<FleetShip> ships = ConfigData.Ships.GetAvailableShipsOfType(shipType).Where((s) => !Squad.HasShip(s)).ToList();
        //    int shipsFilled = 0;
        //    ships.ForEach(ship =>
        //    {
        //        if (shipsFilled < shipCount)
        //        {
        //            //Debug.Log($"Adding ship #{ship.Id} - {ship.Type} to the squad");
        //            SquadShip squadShip = new SquadShip(ship.Id, ship.Type, Vector2.zero, Squad);
        //            Squad.AddShipToSquad(squadShip, false);
        //            shipsFilled++;
        //        }


        //    });
        //    if (shipsFilled < shipCount)
        //    {
        //        Debugger.Exception($"Could not fill Squad #{Squad.Id} with enough ({shipCount}) {shipType} ships. Could only fill {shipsFilled}");
        //    }
        //}

        // Position squad in formation
        public void PositionShipsInSquad(string formation)
        {
            //Debug.Log($"Formation: {formation}");
            if (formation == "Box")
            {
                BoxFormation();
            }
            else if (formation == "Line")
            {
                LineFormation();
            }
            else if (formation == "Arrow")
            {
                PyramidFormation(true);
            }
            else if (formation == "Rectangle")
            {
                RectangleFormation();
            }
            else if (formation == "Pyramid")
            {
                PyramidFormation(false);
            }
            else if (formation == "random")
            {
                RandomFormation();
            }
            else
            {
                Debugger.Exception($"Invalid formation ({formation}) requested.");
            }
        }
        public void RandomFormation()
        {
            int r = Utilities.RandomInt(4);
            switch (r)
            {
                case 0:
                    BoxFormation();
                    break;
                //case 1:
                //    LineFormation();
                //    break;
                case 1:
                    PyramidFormation(true);
                    break;
                case 2:
                    RectangleFormation();
                    break;
                case 3:
                    PyramidFormation(false);
                    break;
            }
        }
        public void LineFormation()
        {
            Debug.Log($"Making a line formation");
            MultiLine(ConfigData.Configuration.MaxSquadWidth, ConfigData.Configuration.MaxSquadHeight);
        }
        public void MultiLine(int maxWidth, int maxLines, bool hollow = false)
        {
            List<SquadShip> ships = Squad.GetShips();
            List<SquadShip> positioned = new List<SquadShip>();
            for (int row = 0; row < maxLines && ships.Count > 0; row++)
            {
                //Debug.Log("Calling line maker");
                LineMaker(maxWidth, row + 1, ships.GetRange(0, Math.Clamp(maxWidth, 0, ships.Count)), hollow)
                    .ForEach((positionedShip) => { positioned.Add(positionedShip); });
                ships = ships.Where((i) => !positioned.Contains(i)).ToList();
            }
        }
        public List<SquadShip> LineMaker(int maxWidth, int level, List<SquadShip> ships, bool hollow = false)
        {
            //if (ships == null)
            //{
            //    ships = Squad.GetShips();
            //}
            level = Math.Clamp(level - 1, 0, ConfigData.Configuration.MaxSquadHeight);
            maxWidth = Math.Clamp(maxWidth, 0, ConfigData.Configuration.MaxSquadWidth);
            List<SquadShip> positioned = new List<SquadShip>();
            //Debug.Log($"Making a line");
            // loop through all the drag icons out there
            for (int shipsPlaced = 0; shipsPlaced < maxWidth && shipsPlaced < ships.Count; shipsPlaced++)
            {
                // for each drag icon, determine its position based off of its place in the order
                SquadShip ship = ships[shipsPlaced];

                //Vector2 screenPoint = Camera.WorldToScreenPoint(ConfigData.ShipOffset);
                //Vector2 change = new Vector2(Mathf.Abs(BaseWorldPoint.x - screenPoint.x), Mathf.Abs(BaseWorldPoint.y - screenPoint.y));

                Vector2 change = ConfigData.ShipOffset * 1.05f;

                //Debug.Log($"Ship offset world units for auto placing: {ConfigData.ShipOffset}, screen pixels {change}");

                //Debug.Log($"change: {change}");

                float xIncrement = change.x;
                float yIncrement = change.y;


                Vector2 position = Vector2.zero;
                float movement;
                float movementDown = level * yIncrement;
                int steps = shipsPlaced;

                if (steps % 2 == 0)
                {
                    int sideSteps = (int)Math.Floor((double)steps / 2);
                    movement = sideSteps * xIncrement;
                }
                else
                {
                    int sideSteps = (int)Math.Ceiling((double)steps / 2);
                    movement = -1 * sideSteps * xIncrement;
                }
                int sideCheck = maxWidth;
                if (!hollow || maxWidth < 3 || shipsPlaced == sideCheck - 2 || shipsPlaced == sideCheck - 1)
                {
                    //Debug.Log($"Placing the ship because it's either not hollow ({hollow}) or the maxWidth is less than 3 ({maxWidth}) or the shipIndex" +
                    //    $"is equal to {sideCheck - 2} or {sideCheck - 1} ({ships})");
                    Vector2 movedPosition = new Vector2(position.x + movement, position.y - movementDown);

                    ship.Offset = movedPosition;
                    positioned.Add(ship);
                }
                else
                {
                    Debug.Log($"NOT placing the ship because it's hollow ({hollow}) and the maxWidth is more than or equal to 3 ({maxWidth}) and the shipIndex" +
                        $"is Not equal to {sideCheck - 2} or {sideCheck - 1} ({ships})");
                    ships.Add(ship);
                }

            }
            return positioned;
        }
        public void BoxFormation()
        {
            Debug.Log($"Making a box formation");
            List<SquadShip> ships = Squad.GetShips();
            if (ships.Count < 4) // make a line across
            {
                LineMaker(ships.Count, 1, ships);
            }
            else if (ships.Count == 4) // make a 2x2 square
            {
                MultiLine(2, 2);
            }
            else if (ships.Count < 10) // tile across no wider than 3
            {
                MultiLine(3, 3);
            }
            else if (ships.Count < 17) // tile across no wider than 4
            {
                MultiLine(4, 4);
            }
            else // tile across no wider than 5
            {
                MultiLine(5, ConfigData.Configuration.MaxSquadHeight);
            }
        }
        public void RectangleFormation()
        {
            Debug.Log($"Making a rectangle formation");
            List<SquadShip> ships = Squad.GetShips();
            List<SquadShip> validShips = ships;
            List<SquadShip> dropped = new List<SquadShip>();
            if (ships.Count < 5)
            {
                LineFormation();
            }
            else if (ships.Count < 11)
            {
                int lineLength = Math.Clamp((ships.Count - 2) / 2, 3, ConfigData.Configuration.MaxSquadWidth);


                // top line
                //Debug.Log($"Making a line of length {lineLength} on row 1 with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, 1, validShips.GetRange(0, Math.Clamp(lineLength, 0, validShips.Count))).ForEach((di) => { dropped.Add(di); });
                validShips = validShips.Where((i) => !dropped.Contains(i)).ToList();

                // hollow middle line
                //Debug.Log($"Making a hollow line of length {lineLength} on row 2 with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, 2, validShips.GetRange(0, Math.Clamp(lineLength, 0, validShips.Count)), true).ForEach((di) => { dropped.Add(di); });
                validShips = validShips.Where((i) => !dropped.Contains(i)).ToList();

                // bottom line
                //Debug.Log($"Making a line of length {lineLength} on row 3 with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, 3, validShips.GetRange(0, Math.Clamp(lineLength, 0, validShips.Count))).ForEach((di) => { dropped.Add(di); });
                validShips = validShips.Where((i) => !dropped.Contains(i)).ToList();
            }
            else
            {
                int lineLength = Math.Clamp((ships.Count - 4) / 2, 3, ConfigData.Configuration.MaxSquadWidth);

                // top line
                //Debug.Log($"Making a line of length {lineLength} on row 1 with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, 1, validShips.GetRange(0, Math.Clamp(lineLength, 0, validShips.Count))).ForEach((di) => { dropped.Add(di); });
                validShips = validShips.Where((i) => !dropped.Contains(i)).ToList();

                // hollow middle line
                //Debug.Log($"Making a hollow line of length {lineLength} on row 2 with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, 2, validShips.GetRange(0, Math.Clamp(lineLength, 0, validShips.Count)), true).ForEach((di) => { dropped.Add(di); });
                validShips = validShips.Where((i) => !dropped.Contains(i)).ToList();

                // second hollow middle line
                //Debug.Log($"Making a hollow line of length {lineLength} on row 2 with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, 3, validShips.GetRange(0, Math.Clamp(lineLength, 0, validShips.Count)), true).ForEach((di) => { dropped.Add(di); });
                validShips = validShips.Where((i) => !dropped.Contains(i)).ToList();

                // bottom line
                //Debug.Log($"Making a line of length {lineLength} on row 3 with {validDragIcons.Count} icons left ------------------------------------");
                while (validShips.Count > lineLength)
                {
                    lineLength++;
                }
                LineMaker(lineLength, 4, validShips.GetRange(0, Math.Clamp(lineLength, 0, validShips.Count))).ForEach((di) => { dropped.Add(di); });
                validShips = validShips.Where((i) => !dropped.Contains(i)).ToList();


            }
        }
        public void PyramidFormation(bool hollow)
        {
            Debug.Log($"Making a pyramid formation");
            List<SquadShip> ships = Squad.GetShips();
            List<SquadShip> validShips = ships;
            List<SquadShip> dropped = new List<SquadShip>();
            for (int row = 0; row < ConfigData.Configuration.MaxSquadHeight && validShips.Count > 0; row++)
            {

                int lineLength = (row * 2) + 1;
                //Debug.Log($"Making a hollow line of length {lineLength} on row {row+1} with {validDragIcons.Count} icons left ------------------------------------");
                LineMaker(lineLength, row + 1, validShips.GetRange(0, Math.Clamp(lineLength, 0, validShips.Count)), hollow).ForEach((di) => { dropped.Add(di); });
                validShips = validShips.Where((i) => !dropped.Contains(i)).ToList();
            }


        }
    }
}