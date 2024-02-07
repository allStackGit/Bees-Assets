using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Level.Commands;
using Assets.Scripts.Scenes;
using Assets.Scripts.Server;
using UnityEngine;

namespace Assets.Scripts.Level

{
    public class Squad : MonoBehaviour
    {
        public LevelStage Level;
        public int Side, SquadNumber, OpponentId, Id;
        public long Age;
        public List<ShipDamageStatus> DamageSentToEnemyShipsBySquad = new List<ShipDamageStatus>();
        public Command Command;
        public List<StoredCommand> PastCommands = new List<StoredCommand>();
        public MatchupStrategy MatchupStrategy; // the matchup strategy belongs to the squad and not the command because it is used to determine the command by making the matchup
        public HashSet<string> BannedStrats = new HashSet<string>();
        public string Status;
        public string Name;
        public Color Color;
        public Color SquadBoxColor;
        public SavedSquad SavedSquad;
        public GameObject SquadBox;
        public bool HasMovedBox, IsMatchingSpeed, CeaseFire, IsRetreating, HasAddedShips, IsShowingRanges = false;
        public float CurrentSpeed;

        private bool _died;
        private List<Ship> _ships = new List<Ship>();
        private bool _shouldChase = false;
        private string _chosenShootingStrategy; // there is a shooting strategy attached to the squad because users attach shooting strategies to the squad whereas the AI attaches them to the command

        public long LastKilled => GetShips().Max(s => s.LastKilled);
        public int DamageDone => GetShips().Sum(s => s.FleetShip.DamageDone);
        public int Health => GetShips().Sum(s => s.Health);
        public float Firepower => GetShips().Sum(s => s.Firepower);
        public int Range => GetShips().Max(s => s.Range);
        public float TotalSpeed => GetShips().Sum(s => s.Speed);
        public float MaxSpeed => GetShips().Max(s => s.Speed);
        public int Tsv => GetShips().Sum(s => s.Tsv);
        public float SlowestSpeed => GetShips().Min(s => s.Speed);
        public bool IsMoving => GetShips().Any(ship => ship.IsMoving);
        public bool IsDead => _died;
        public bool HasCommand => Command != null;
        public bool HasEnemy => HasCommand && Command.HasEnemy;
        public bool IsAttacking => HasCommand && Command.IsAttacking;
        public Vector2 StartingPosition => Side == ConfigData.Configuration.UserSide ? Level.UserStartingPosition : Level.AIStartingPosition;
        public bool IsDefenseless => GetShips().All((s) => s.Firepower == 0);
        public bool AttackOnSight => !CeaseFire;
        public bool Holding => !ShouldChase();
        public bool IsCarrierSquad => this is CarrierSquad;
        public bool HasOnlyYellowJackets => GetShips().All((s) => s.ShipType == "Yellow Jacket");
        public bool HasOnlyStrikers => GetShips().All((s) => s.ShipType == "Striker");
        public bool HasOnlyBombers => GetShips().All((s) => s.ShipType == "Striker" || s.ShipType == "Yellow Jacket" || s.ShipType == "Fire Ship");
        public bool IsUserControlled => Side == ConfigData.Configuration.UserSide && Level.HasPlayer;
        public bool IsHiveMindControlled => Side == ConfigData.Configuration.AISide || (Side == ConfigData.Configuration.UserSide && !Level.HasPlayer);
        /// <summary>
        /// Is this squad is selected by the user?
        /// </summary>
        public bool IsSelected => Level.GetState().GetSelectedSquads().Any((squad) => Equals(squad));
        public bool HasReachedDestination => GetShips().All((s) => s.HasReachedDestination);
        public bool HasColor => Color != ConfigData.UnsetColor;
        public bool HasBrain => GetShips().All((s) => s.HasBrain);
        public bool InCombat => GetShips().Any((s) => s.InCombat);


        // Setup methods
        public void Setup(LevelStage level, SavedSquad savedSquad, string shootingStrategy, bool ceaseFire, bool isMatchingSpeed, 
            int id, int side, int squadNumber, string name, Color color)
        {
            Level = level;
            SavedSquad = savedSquad;
            Id = id;
            Side = side;
            Status = "idle";
            Name = name;
            Color = color;
            SquadNumber = squadNumber;
            IsMatchingSpeed = isMatchingSpeed;
            CeaseFire = ceaseFire;
            SetShootingStrategy(shootingStrategy);
            SetOpponent();
            if (IsHiveMindControlled)
            {
                AddToCommandList();
            }
            else
            {
                //Debugger.Log($"Squad: {Name}, Side: {Side}, HiveMindControlled: {IsHiveMindControlled}, Has Brain: {HasBrain}");
            }

            if (HasColor)
            {
                //Debugger.Log($"SDC: {ConfigData.GetUIColor("squadbox-default-color").a}");
                SquadBoxColor = new Color(Color.r, Color.g, Color.b, ConfigData.GetUIColor("squadbox-default-color").a);
            }
            else
            {
                SquadBoxColor = ConfigData.GetUIColor("squadbox-default-color");
            }
            transform.parent = Level.Map.transform;
        }
        public void SetupRandomSquadShips(string squadType)
        {
            int squadId = -1 * Utilities.RandomInt(1000000);


            int shipCount = 8;
            if ((new List<string> { "Queen", "Fire Ship", "Carrier" }).Contains(squadType))
            {
                shipCount = UnityEngine.Random.Range(1, 3);
            }
            else if ((new List<string> { "Bumblebee", "Flagship", "Barge" }).Contains(squadType))
            {
                shipCount = UnityEngine.Random.Range(1, 5);
            }
            else if ((new List<string> { "Leafcutter", "Wasp", "Cruiser", "Dreadnought", "Frigate", "Gunship" }).Contains(squadType))
            {
                shipCount = UnityEngine.Random.Range(2, 6);
            }
            else if ((new List<string> { "Honeybee", "Hornet", "Yellow Jacket", "Scout" }).Contains(squadType))
            {
                shipCount = UnityEngine.Random.Range(4, 12);
            }

            for (int shipIndex = 0; shipIndex < shipCount; shipIndex++)
            {
                int id = (int)Utilities.Hash() + ConfigData.Ships.GetFleetShips().Count;
                Vector2 offset = ConfigData.CarrierColumnFormationOffsets[shipIndex];


                //Debugger.Log($"Offset: {offset}");
                Ship ship;
                (GameObject, Ship) tuple = Level.LevelConstructor.InstantiateShip(squadType);
                ship = tuple.Item2;


                if (ship != null)
                {
                    ship.Setup(
                        Level,
                        Level.GetState().EntityCount++,
                        new FleetShip(id, Side, $"Random {squadType} - #{id}", squadType, true, false, 0, 0, 0, 0, 0, 0),
                        this,
                        offset
                    );
                }
                AddShip(ship);
                ship.SetColor();
            }
        }
        private void SetOpponent()
        {
            if (Side == ConfigData.Configuration.AISide)
            { // ai side
                if (Level.HasPlayer)
                {
                    OpponentId = ConfigData.GetUserId();
                }
                else
                {
                    OpponentId = 1;
                }
            }
            else if (Side == ConfigData.Configuration.UserSide)
            { // user side
                OpponentId = 0;

                if (Level.HasPlayer && !Level.IsTrainingNueralNetwork)
                {
                    SquadBox = LevelStage.Instantiate(Level.SquadBox, new Vector3(0, 0, 0), Quaternion.identity);
                    SquadBox.transform.parent = Level.Map.transform;
                    SquadBox.SetActive(false);
                }
            }
        }

        /* When squads are setup, they are position centrally like a line formation, first in the center, second on the right, third on the left, fourth on the right, and so on
         * The squad is set to the center point and all the ships are positioned based off their original offset
         */
        public void SetStartingPosition(Vector2 position)
        {
            //float largestShipSize = ConfigData.ShipSizeFactor.GetValueOrDefault(
            //    GetShips().OrderByDescending((s) => ConfigData.ShipSizeFactor.GetValueOrDefault(s.ShipType)).ToList().First().ShipType
            //    );
            GetShips().ForEach((ship) =>
            {
                // The size factor (1-16)
                //Vector2 sizeFactor = (largestShipSize / ConfigData.DragIconSize) * ConfigData.WorldUnitScaleFactor;


                // trying to place ships on the map according to where they were in the squad maker
                // Option 1: Convert the squadmaker coordinates directly to map coordinates

                //Debugger.Log($"Ship: {ship.Name} Position: {position}, Offset from Center: {ship.OffsetFromCenter}");

                Vector2 adjustment = ship.OffsetFromCenter;
                
                float x = Mathf.Clamp((position.x + adjustment.x), Level.MinX, Level.MaxX);
                float y = Mathf.Clamp((position.y + adjustment.y), Level.MinY, Level.MaxY);

                //Debugger.Log($"Sizefactor for {ship.Name}: {sizeFactor}");
                //Debugger.Log($"Local starting position for {ship.Name}: {new Vector2(x, y)}");
                ship.transform.localPosition = new Vector2(x, y);
            });

        }
        protected void Update()
        {

            if (!Level.IsPaused)
            {
                Age++;
                if (Command != null)
                {
                    Command.Age++;
                }
            }

            // Debug.Log($"{name} ship has lived for {tickLife} ticks and {ShowLifeTime()} seconds with {GetHealth()} health");
        }
        public void FixedUpdate()
        {
            if (IsUserControlled && !Level.IsPaused)
            {
                HasMovedBox = false;
                CheckChase();
            }
        }
        public void SetOffsets()
        {
            Vector2 center = GetCenterPoint();
            List<Ship> ships = GetShips();
            foreach (Ship ship in ships)
            {
                ship.OffsetFromCenter = new Vector2(ship.GetX() - center.x, ship.GetY() - center.y);
            }
        }


        // Movement methods
        public void Move(Vector2 destination)
        {
            //if (IsAttacking)
            //{
            //    GetShips().ForEach((Ship ship) =>
            //    {
            //        Vector2 offset = ship.OffsetFromCenter;
            //        offset.x = Mathf.Clamp(offset.x, 2, ship.Range);
            //        offset.y = Mathf.Clamp(offset.y, 2, ship.Range);
            //        float x = Mathf.Clamp((destination.x + offset.x), Level.MinX, Level.MaxX);
            //        float y = Mathf.Clamp((destination.y + offset.y), Level.MinY, Level.MaxY);
            //        ship.TargetCoordinates = new Vector2(x, y);
            //    });
            //}
            //else
            //{
            //    GetShips().ForEach((Ship ship) =>
            //    {
            //        float x = Mathf.Clamp((destination.x + ship.OffsetFromCenter.x), Level.MinX, Level.MaxX);
            //        float y = Mathf.Clamp((destination.y + ship.OffsetFromCenter.y), Level.MinY, Level.MaxY);
            //        ship.TargetCoordinates = new Vector2(x, y);
            //    });
            //}
            if (Level.Menus.HasSquadActionBox && IsSelected)
            {
                Level.Menus.ActionBox.HighlightSelectedButtons();
            }
            List<Ship> ships = GetShips();
            foreach (Ship ship in ships)
            {
                float x = Mathf.Clamp((destination.x + ship.OffsetFromCenter.x), Level.MinX, Level.MaxX);
                float y = Mathf.Clamp((destination.y + ship.OffsetFromCenter.y), Level.MinY, Level.MaxY);
                ship.TargetCoordinates = new Vector2(x, y);
            }
                

        }
        public void MatchSpeed(float speed = 0)
        {
            IsMatchingSpeed = true;
            if (speed > 0)
            {
                SetSquadSpeed(speed);

            }
            else
            {
                SetSquadSpeed(SlowestSpeed);
            }
        }
        public void UnmatchSpeed()
        {
            List<Ship> ships = GetShips();
            foreach (Ship ship in ships)
            {
                ship.SetCurrentSpeed(ship.Speed);
            }
            IsMatchingSpeed = false;
        }
        public void SetSquadSpeed(float speed)
        {
            CurrentSpeed = speed;
            List<Ship> ships = GetShips();
            foreach (Ship ship in ships)
            {
                ship.SetCurrentSpeed(speed);
            }
        }
        public void SetChase(bool chase)
        {
            _shouldChase = chase;
        }
        public bool IsChasing()
        {
            return Command != null && Command.Type == "Aggressive" && _shouldChase;
        }
        public void StopChasing()
        {
            if (Command != null && Command.Type == "Aggressive")
            {
                Command.SetFinalize("Stopped Chasing");
            }
            SetChase(false);
        }
        public bool ShouldChase()
        {
            return _shouldChase;
        }



        // Combat methods
        private void CheckChase()
        {
            if (_shouldChase && (Command == null || Command.Strategy.Name != "Aggressive"))
            {
                Squad closestSquad = GetClosestEnemySquad();
                if (CanSeeSquad(closestSquad))
                {
                    UserAggressive(closestSquad);
                }
            }
        }
        public void Kill(bool endKill = false)
        {
            _died = true;
            //Debugger.Log($"Killing squad {Name}");
            if (!endKill)
            {
                if (HasCommand)
                {
                    Command.SetFinalize("This squad got killed");
                }

                if (IsUserControlled)
                {
                    DeactivateSquadBox();
                }

                GameState state = Level.GetState();

                if (state.IsSideKilled(Side))
                {

                    state.GameOver = true;

                    state.GetAllSquads().ForEach((squad) =>
                    {
                        if (squad.HasCommand)
                        {
                            squad.Command.SetFinalize("Level ended");
                        }
                    });
                }
                else
                {
                    if (state.GetSelectedSquads().Count == 0)
                    {
                        state.SelectSquad(state.GetSquadsBySide(Side).First());
                    }
                }
            }

            //if (HasCommand)
            //{
            //    //Debugger.Log($"Destroying command {Command.OutcomeId}");
            //    Destroy(gameObject.GetComponents<Command>().First((c) => c.OutcomeId == Command.OutcomeId));
            //}

            // destroys all commands connected to dead squads, including this one
            gameObject.GetComponents<Command>().Where((c) => c.Squad == null || c.Squad.IsDead).ToList().ForEach((c) => {
                Destroy(c);
            });
            if (IsUserControlled)
            {
                Destroy(SquadBox);
            }
            Destroy(this);

        }
        public Squad GetClosestEnemySquad()
        {
            List<Squad> squads = Level.GetState().GetEnemySquads(Side);

            // Debug.Log($"Number of enemy squads {squads.Count}, {_level.GetState().GetSquads()}");
            return squads.OrderBy(squad => squad.DistanceTo(GetPosition())).FirstOrDefault();
        }
        public Squad GetClosestValidFriendlySquad()
        {
            List<Squad> squads = Level.GetState().GetSquadsBySide(Side).Where(squad => !squad.Equals(this) && (!squad.HasCommand || squad.Command.Type != "Closest Friendly")).ToList();
            return squads.OrderBy(squad => squad.DistanceTo(GetPosition())).FirstOrDefault();
        }
        public Squad GetEnemy()
        {
            if (Command != null)
            {
                return Command.Enemy;
            }
            else
            {
                if (IsUserControlled)
                {
                    return GetClosestEnemySquad();
                }
                return null;
            }
        }
        /// <summary>
        /// Gets all ships in the level that are on the opposing side
        /// </summary>
        /// <returns></returns>
        public List<Ship> GetEnemyShips()
        {
            if (Side == ConfigData.Configuration.BeeSide) // bee side, get the human ships
            {
                return Level.GetState().GetHumanShips();
            }
            else
            {
                return Level.GetState().GetBeeShips();
            }
        }
        public List<Ship> GetFriendlyShips()
        {
            return Level.GetState().GetShips(Side);
        }
        public List<Ship> GetPotentialEnemies(Squad target)
        {
            List<Ship> enemies = target.GetShips().ToList(); // the ToList() is very important to prevent this list from modifying the main squad list
            List<Ship> potentialEnemies = GetEnemyShips();

            foreach(Ship potentialEnemy in  potentialEnemies)
            {
                if (!potentialEnemy.Squad.Equals(target))
                {
                    if (potentialEnemy.IsAnySquadShipWithinRange(target)) // if any ship in the target squad is within the range of another of its allies (the potential enemy)
                    {
                        enemies.Add(potentialEnemy);
                    }
                    else if (potentialEnemy.Squad.IsAnySquadShipWithinRangeOfAnyOfOurSquadShips(this)) // if the squad is within the range of the potential enemy
                    {
                        enemies.Add(potentialEnemy);
                    }
                }
            }

            return enemies;
        }
        public List<Ship> GetPotentialAllies(Squad target)
        {
            List<Ship> allies = GetShips().ToList(); // the ToList() is very important to prevent this list from modifying the main squad list
            List<Ship> potentialAllies = GetFriendlyShips();

            foreach(Ship potentialAlly in potentialAllies)
            {
                if (!Equals(potentialAlly.Squad))
                {
                    if (potentialAlly.IsAnySquadShipWithinRange(target)) // if any ship in the target squad is within the range of another of its allies
                    {
                        allies.Add(potentialAlly);
                    }
                }
            }

            return allies;
        }


        // Command and control methods
        public void AddToCommandList()
        {          
            Level.GetState().AddToSquadsAwaitingHiveMindCommands(this);
        }
        public void MakeMatchupStrat()
        {
            // Can't get any invisible ship types and start by blocking the visible ship types too
            HashSet<string> banned = ConfigData.Configuration.AllShipTypes.ToHashSet();

            if (ConfigData.Configuration.UserSide == ConfigData.Configuration.BeeSide)
            {
                // if you're the bees you can only get available human ship types
                HashSet<string> enemyShips = Level.GetState().GetHumanShipTypes();
                banned = banned.Where((type) => !enemyShips.Contains(type)).ToHashSet();
            }
            else
            {
                // if you're the humans you can only get available bee ship types
                HashSet<string> enemyShips = Level.GetState().GetBeeShipTypes();
                banned = banned.Where((type) => !enemyShips.Contains(type)).ToHashSet();
            }
            
            string[] bannedTypes = banned.Select((ship) => $"Type {(Utilities.ConvertShipNameToType(ship))}").ToArray();

            Level.Socket.SendRequest(new MatchupStrategyRequest(new GetMatchupStrategy(AddToMatchup(GetShips()), OpponentId, bannedTypes),
                this, ConfigData.StandardMaxTimeOnQueue));
        }
        public static string AddToMatchup(List<Ship> ships)
        {
            //string unsorted = "";
            //StringBuilder stringBuilder = new StringBuilder();
            char[] letters = ships.Select(s => s.ShipTypeLetter.First()).ToArray();
            //ships.ForEach((ship) =>
            //{
            //    //unsorted += ship.ShipTypeLetter;
            //    //stringBuilder.Append(ship.ShipTypeLetter);
            //    letters.Add(ship.ShipTypeLetter.First());
            //});


            
            Array.Sort(letters);
            //Debugger.Log(new string(letters));
            return new string(letters);
        }
        public void MakeMatchup(Squad enemy)
        {
            //Debugger.Log("Making matchup");
            if (enemy != null)
            {
                //Debugger.Log("Enemy is not null");
                List<Ship> enemies = GetPotentialEnemies(enemy);
                List<Ship> allies = GetPotentialAllies(enemy);

                /*
                Determines whether or not the squad is at the "walls"
                 */

                int atTheWalls = 0;
                int distance = 15;
                Vector2 position = GetPosition();
                if (position.x < (Level.MapRenderer.bounds.min.x + distance) || position.x > (Level.MapRenderer.bounds.max.x - distance)) // check if it's at the sides
                {
                    atTheWalls = 1;
                    if (position.y < (Level.MapRenderer.bounds.min.y + distance) || position.y > (Level.MapRenderer.bounds.max.y - distance))
                    {
                        atTheWalls = 2;
                    }
                }
                else if (position.y < (Level.MapRenderer.bounds.min.y + distance) || position.y > (Level.MapRenderer.bounds.max.y - distance))
                {
                    atTheWalls = 1;
                }

                /*
                This is the calculation for the enemy's current average percentage of health for each ship and then the same for the allies, and then compares the allies to the enemies
                 */

                int comparativeHealth = (int)Math.Round((Ship.GetAverageHealthPercent(allies) / Ship.GetAverageHealthPercent(enemies)) * 100);   

                /*
                 comparativeHealth
                 < 50 0
                 50 - 85 1
                 85 - 115 2
                 115 - 165 3
                 165+ 4
                 */

                if (comparativeHealth < 50)
                {
                    comparativeHealth = 0;
                }
                else if (comparativeHealth < 85)
                {
                    comparativeHealth = 1;
                }
                else if (comparativeHealth < 115)
                {
                    comparativeHealth = 2;
                }
                else if (comparativeHealth < 165)
                {
                    comparativeHealth = 3;
                }
                else
                {
                    comparativeHealth = 4;
                }
                StringBuilder sb = new StringBuilder();
                sb.Append(AddToMatchup(allies));
                sb.Append("|");
                sb.Append(AddToMatchup(enemies));
                sb.Append("|");
                sb.Append((enemy.IsAnySquadShipWithinRangeOfAnyOfOurSquadShips(this) ? 1 : 0));
                sb.Append("|");
                sb.Append(comparativeHealth);
                sb.Append("|");
                sb.Append(atTheWalls);

                string matchup = sb.ToString();

                Level.Socket.SendRequest(new CommandRequest(new GetStrategy(matchup, OpponentId, BannedStrats.ToArray()),
                    this, enemy, matchup, ConfigData.StandardMaxTimeOnQueue));

            }
            else
            {
                Debugger.Exception($"The enemy is null so a matchup can't be made with this strategy");
            }


        }
        public void SetShootingStrategy(string strategy)
        {
            if (ConfigData.Configuration.ShootingStrategies.Contains(strategy))
            {
                _chosenShootingStrategy = strategy;
            }
        }
        public string GetShootingStrategy()
        {
            if (HasCommand && Command.HasShootingStrategy)
            {
                return Command.ShootingStrategy.Name;
            }
            return _chosenShootingStrategy;
        }
        public string GetCommandStrategy()
        {
            if (HasCommand && Command.HasStrategy)
            {
                return Command.Strategy.Name;
            }
            return null;
        }
        public void UserGuard(Squad squad)
        {

            (Strategy, ShootingStrategy) strategies = MakeUserCommand("Guard", null);

            ((Guard)Command).Execute(strategies.Item1, strategies.Item2, Level.GetState().AddUserCommand(), true, squad);
            if (Level.Menus.HasSquadActionBox)
            {
                Level.Menus.ActionBox.HighlightSelectedButtons();
            }
        }
        public void UserPatrol(Vector2 topLeft, Vector2 bottomRight)
        {
            //Debugger.Log($"Selecting patrol area for {Name}");
            (Strategy, ShootingStrategy) strategies = MakeUserCommand("Patrol", null);

            ((Patrol)Command).Execute(strategies.Item1, strategies.Item2, Level.GetState().AddUserCommand(), true, topLeft, bottomRight);
            if (Level.Menus.HasSquadActionBox)
            {
                Level.Menus.ActionBox.HighlightSelectedButtons();
            }
        }
        public void UserAggressive(Squad enemy)
        {
            if (IsCarrierSquad)
            {
                CarrierSquad carrierSquad = (CarrierSquad)this;
                if (carrierSquad.SquadType == "Striker")
                {
                    UserBombingRun(enemy);
                    return;
                }
            }
            //Debugger.Log($"Creating \"Aggressive\" command for {Name} against {enemy.Name}");
            (Strategy, ShootingStrategy) strategies = MakeUserCommand("Aggressive", enemy); // selectedSquad is the user's squad, and Squad is this ship's squad
            Command.Execute(strategies.Item1, strategies.Item2, Level.GetState().AddUserCommand(), false);
        }
        public void UserBombingRun(Squad enemy)
        {
            //Debugger.Log($"Creating \"Bombing Run\" command for {Name} against {enemy.Name}");
            (Strategy, ShootingStrategy) strategies = MakeUserCommand("Bombing Run", enemy); // selectedSquad is the user's squad, and Squad is this ship's squad
            ((BombingRun)Command).Execute(strategies.Item1, strategies.Item2, Level.GetState().AddUserCommand(), false);
        }
        public (Strategy, ShootingStrategy) MakeUserCommand(string command, Squad enemy)
        {
            //Debugger.Log($"{Name} now has command against {enemy.Name}");
            FinalizeUserCommand();

            MatchupStrategy = null;

            switch (command)
            {
                case "Aggressive":
                    Command = gameObject.AddComponent<Aggressive>();
                    break;
                case "Bombing Run":
                    Command = gameObject.AddComponent<BombingRun>();
                    break;
                case "Guard":
                    Command = gameObject.AddComponent<Guard>();
                    break;
                case "Patrol":
                    Command = gameObject.AddComponent<Patrol>();
                    break;
                default:
                    Debugger.Exception($"Invalid command {command} issued to user squad");
                    break;
            }



            Command.Setup(this, false, enemy, null);

            return (new Strategy(Command, command, null, 0, 0), new ShootingStrategy(Command, GetShootingStrategy(), null, 0, 0));


        }
        public void FinalizeUserCommand()
        {
            if (HasCommand)
            {
                //Debugger.Log($"Finalizing command for {Name}");

                if (Command.Type == "Guard")
                {
                    UnmatchSpeed();
                    ((Guard)Command).GetGuardingSquads().ForEach((squad) =>
                    {
                        ((Guard)squad.Command).OtherGuardSquads.Remove(this);
                    });
                }
                Command.SetFinalize("New command given");
            }
        }


        // ship list methods
        public List<Ship> GetShips()
        {
            return _ships;
        }
        public ShipDamageStatus GetShipDamageStatus(Ship potentialTargetShip)
        {
            List<ShipDamageStatus> damageSentToEnemyShipsBySquad = DamageSentToEnemyShipsBySquad;
            ShipDamageStatus shipDamageStatus = null;
            if (damageSentToEnemyShipsBySquad.Count > 0)
            {
                shipDamageStatus = damageSentToEnemyShipsBySquad.FirstOrDefault(s => s != null && s.ship != null && s.ship.Equals(potentialTargetShip));
            }

            if (shipDamageStatus == null)
            {
                shipDamageStatus = new ShipDamageStatus(potentialTargetShip);
                damageSentToEnemyShipsBySquad.Add(shipDamageStatus);
            }
            return shipDamageStatus;
        }
        public void AddShip(Ship ship)
        {
            //Debugger.Log($"Adding {ship.Name} to Squad {Name}");
            _ships.Add(ship);
            if (IsDefenseless)
            {
                BannedStrats.Add("Aggressive");
                BannedStrats.Add("Circle");
                BannedStrats.Add("Right Swipe");
                BannedStrats.Add("Left Swipe");
                BannedStrats.Add("In and Out");
            }
            else
            {
                BannedStrats.Remove("Aggressive");
                BannedStrats.Remove("Circle");
                BannedStrats.Remove("Right Swipe");
                BannedStrats.Remove("Left Swipe");
                BannedStrats.Remove("In and Out");
            }
            HasAddedShips = true;
        }
        public void RemoveShip(Ship ship)
        {
            _ships.Remove(ship);
        }       



        // Utility methods
        public bool Equals(Squad squad)
        {
            return squad.Id == Id;
        }
        public new string ToString()
        {
            return $"Squad Number #{SquadNumber} on side #{Side} {Name} with {_ships.Count} ships and IsDead? {IsDead}";
        }


        /* Range and distance methods */
        public bool CanSeeSquad(Squad squad)
        {
            bool canSee = false;
            List<Ship> ships = GetShips();
            foreach (Ship ship in ships)
            {
                List<Ship> squadShips = GetShips();
                foreach (Ship squadShip in squadShips)
                {
                    if (ship.CanSeeShip(squadShip))
                    {
                        canSee = true;
                    }
                }
            }
            return canSee;
        }
        public bool IsAnySquadShipWithinRangeOfAnyOfOurSquadShips(Squad squad)
        {
            return GetShips().Any((ship) => ship.IsAnySquadShipWithinRange(squad));
        }
        public bool IsAnySquadShipWithinRangeOfAllOfOurSquadShips(Squad squad)
        {
            return GetShips().Any((ship) => ship.AreAllSquadShipsWithinRange(squad));
        }
        public bool AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(Squad squad)
        {
            return GetShips().All((s) => s.AreAllSquadShipsWithinRange(squad));
        }
        public bool IsWithinRangeOfAnyShipInEnemySquad()
        {
            Squad enemy = GetEnemy();
            if (enemy != null)
            {
                return enemy.IsAnySquadShipWithinRangeOfAnyOfOurSquadShips(this);
            }
            return false;
        }
        public float DistanceTo(Vector2 point)
        {
            return Vector2.Distance(GetPosition(), point);
        }
        public Vector2 GetPosition()
        {
            return GetCenterPoint();
        }
        public Vector2 GetLeftMostPoint()
        {
            if (IsDead)
            {
                Debugger.Exception(new Exception("Tried to get the LeftMostPoint of a dead squad."));
            }
            Ship ship = GetShips().OrderBy((ship) => ship.GetLeftMostPoint().x).ToList().First();
            return new Vector2(ship.GetLeftMostPoint().x, ship.GetY());
        }
        public Vector2 GetRightMostPoint()
        {
            if (IsDead)
            {
                Debugger.Exception(new Exception("Tried to get the RightMostPoint of a dead squad."));
            }
            Ship ship = GetShips().OrderByDescending((ship) => ship.GetRightMostPoint().x).ToList().First();
            return new Vector2(ship.GetRightMostPoint().x, ship.GetY());
        }
        public Vector2 GetTopMostPoint()
        {
            Ship ship = GetShips().OrderByDescending((ship) => ship.GetTopMostPoint().y).ToList().First();
            return new Vector2(ship.GetX(), ship.GetTopMostPoint().y);
        }
        public Vector2 GetBottomMostPoint()
        {
            Ship ship = GetShips().OrderBy((ship) => ship.GetBottomMostPoint().y).ToList().First();
            return new Vector2(ship.GetX(), ship.GetBottomMostPoint().y);
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
        public float AngleToPoint(Vector3 point)
        {
            return Utilities.AngleBetweenPoints(GetPosition(), point);
        }
        public Vector2 CirclePoint(float angle, float distance)
        {
            angle *= -1;
            angle -= Mathf.PI * .5f;
            Vector2 position = GetPosition();
            return new Vector2((position.x + (Mathf.Cos(angle) * distance)), (position.y + (Mathf.Sin(angle) * distance)));
        }


        // UI Methods
        public void MoveSquadBox()
        {
            //Debugger.Log($"Squad #{squadNumber} is moving and the squad box will have width {GetWidth()}, height {GetHeight()}, and center point {GetCenterPoint()}");
            //Debugger.Log($"Right most point {GetRightMostPoint()}, Left most point {GetLeftMostPoint()}, Top most point {GetTopMostPoint()}, Bottom most point {GetBottomMostPoint()}");
            if (IsSelected && !Level.IsTrainingNueralNetwork)
            {
                SquadBox.SetActive(true);
                SquadBox.transform.localPosition = GetCenterPoint();
                SquadBox.transform.localScale = new Vector3(GetWidth() + 1, GetHeight() + 1, 0);
                if (HasColor)
                {
                    Utilities.SetUIColor(SquadBox, SquadBoxColor);

                }
                if (GetShips().Count == 1)
                {
                    Ship onlyShip = GetShips().First();
                    if (onlyShip != null)
                    {
                        SquadBox.transform.rotation = onlyShip.transform.rotation;
                    }
                }
                HasMovedBox = true;
            }
        }
        public void DeactivateSquadBox()
        {
            if (SquadBox != null)
            {
                SquadBox.SetActive(false);
            }
        }
        public void ShowSquadRanges()
        {
            GetShips().ForEach((ship) => ship.ShowWeaponRanges());
            IsShowingRanges = true;
        }
        public void HideSquadRanges()
        {
            GetShips().ForEach((ship) => ship.HideWeaponRanges());
            IsShowingRanges = false;
        }

    }

}

