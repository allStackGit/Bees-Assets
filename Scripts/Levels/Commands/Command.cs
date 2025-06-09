using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;
using Microsoft.CSharp;

using Assets.Scripts;
using UnityEngine.UIElements;
using Assets.Scripts.Entities;
using Assets.Scripts.Scenes;
using Assets.Scripts.Entities.Ships;
using System.Data;

namespace Assets.Scripts.Levels.Commands
{
    public class Command : MonoBehaviour
    {
        public long Age;
        /// <summary>
        /// How much TSV has been gained or lost over the lifetime of the command
        /// </summary>
        public long Tsv;  
        /// <summary>
        /// The Id of this command relative to the server.
        /// </summary>
        public long OutcomeId = 0; 
        /// <summary>
        /// The enemy squad that this command is attacking, if it has one. Attack commands require an enemy and end when the enemy dies.
        /// Other commands  may involve attacking but aren't about that and don't require an enemy
        /// </summary>
        public Squad EnemySquad;
        private Squad _squad;
        public string Matchup, FinalizationCause;
        public ConfigData.CommandTypes CommandType = ConfigData.CommandTypes.Uninitialized;
        public MatchupStrategy MatchupStrategy;
        public ShootingStrategy ShootingStrategy;
        public bool IsDead;
        public bool IsAttacking, IsCloseToTarget;
        public bool HasShootingStrategy;
        public bool HasEnemy;
        public float CommandFrequency = 3;
        public Level Level;
        public int Side;
        /// <summary>
        /// The Id of this command relative to the stage. Guarenteed unique for this stage.
        /// </summary>
        public int ItemId;
        /// <summary>
        /// The targeting queue, unmodified from when it was generated, only regenerated when a new ship is added to the enemy squad
        /// </summary>
        public Queue<Ship> OriginalQueue = new Queue<Ship>();
        /// <summary>
        /// The list of ships (in order) that this squad's ships should follow after, modified each time a ship takes an enemy ship off the queue and follows it
        /// </summary>
        public Queue<Ship> TargetingQueue = new Queue<Ship>();
        public Stage Stage;

        private List<Vector2> _destinations = new List<Vector2>();


        public bool IsFinalized;
        public bool IsHiveMindCommand;
        /// <summary>
        /// Keeps the Id of the original enemy squad so we can check if the enemy has died and been respawned as a new squad in between timer() calls
        /// </summary>
        public int OriginalEnemyId;

        //public int OriginalSquadId, CreationId; // [debug]
        public ScaledTimer CommandTimer = new ScaledTimer();
        public ScaledTimer TimeoutTimer = new ScaledTimer();


        private List<Ship> _tempShips;


        public virtual void Create(Stage stage, ConfigData.CommandTypes commandType)
        {
            Stage = stage;
            MatchupStrategy = new MatchupStrategy();
            ShootingStrategy = new ShootingStrategy();
            CommandType = commandType;
            IsDead = true;
            //CreationId = Utilities.Hash();
        }
        public virtual void ClearData()
        {
            Tsv = 0;
            OutcomeId = 0;
            OriginalEnemyId = 0;
            EnemySquad = null;
            IsAttacking = false;
            IsCloseToTarget = false;
            HasShootingStrategy = false;
            HasEnemy = false;
            OriginalQueue.Clear();
            TargetingQueue.Clear();
            _destinations.Clear();
            IsFinalized = false;
        }
        public void Setup(Squad squad, bool isHiveMindCommand, Squad enemy, string matchup)
        {
            //if (!IsDead)
            //{
            //    Debug.LogError($"Trying to setup a command that's already active! {this}");
            //}
            
            IsDead = false;
            Level = squad.Level;
            //Debug.Log((int)CommandType);
            //Debug.Log(Stage.__CommandCounts.Length);
            Stage.DebugLogger.__CommandCounts[(int)CommandType]++;
            Side = squad.Side;
            SetSquad(squad);
            EnemySquad = enemy;
            Matchup = matchup;
            ItemId = Level.State.GetId();
            GetSquad().OriginalCommandId = ItemId;
            IsHiveMindCommand = isHiveMindCommand;

            if (EnemySquad != null)
            {
                OriginalQueue = new Queue<Ship>(MakeTargetingQueue());
                TargetingQueue = new Queue<Ship>(OriginalQueue);
                HasEnemy = true;
                OriginalEnemyId = EnemySquad.ItemId;
            }
            enabled = true;
            //Debug.Log($"Just set up command {this}");
        }
        //public void Update()
        //{
        //    if (!IsDead && OutcomeId > 0 && GetSquad().Id != OriginalSquadId)
        //    {
        //        Debug.LogError($"{this} no longer has the original squad id! {GetSquad().Id}");
        //    }
        //}
        public Squad GetSquad()
        {
            return _squad;
        }
        public void SetSquad(Squad squad)
        {
            //Debug.Log($"Setting squad to {squad} for {this}");
            _squad = squad;
            //OriginalSquadId = GetSquad().Id;
            GetSquad().HasCommand = true;
        }
        public virtual void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy)
        {
            //if (GetSquad().GetShips().Any((s) => s.ShipType == ConfigData.ShipTypes.Beacon))
            //{
            //    Debug.Log($"{this} has a squad with beacons and it just started");
            //}
            if (noEnemy || HasEnemy)
            {
                OutcomeId = commandOutcomeId;
                ShootingStrategy.Setup(shootingStrategy, shootingStrategyOutcomeId);
                HasShootingStrategy = true;
                MatchupStrategy.Setup(GetSquad().MatchupStrategy.MatchupType, GetSquad().MatchupStrategy.OutcomeId, GetSquad());
                GetSquad().SetShootingStrategy(ShootingStrategy.ShootingStrategyType);
                GetSquad().ClearTargets(); // Clear all old targets before starting the new command

                if (Stage.DebugLogger.IsDebugging)
                {
                    GetSquad().PastCommands.Add(new StoredCommand(this));
                }
                Level.State.AddCommand(this);

                GetSquad().Status = $"Executing Command #{OutcomeId}";
                //Debug.Log("Set status for command");
                //Debug.Log($"Executing command {this}");

            }
            else
            {
                //Debug.Log($"Could not find the enemy for command #{commandOutcomeId}");
                SetFinalize("Could not find the enemy squad for command");
                return;
            }

        }
        /// <summary>
        /// Sets the destination for the squad to move to. This clears all previous destinations and sets the new one.
        /// </summary>
        /// <param name="destination"></param>
        public void SetDestination(Vector2 destination)
        {
            ClearDestinations();
            AddDestination(destination);
        }
        public List<Vector2> GetDestinations()
        {
            return _destinations;
        }
        /// <summary>
        /// Adds a destination to the list of destinations for the squad to move to. This does not clear previous destinations.
        /// </summary>
        /// <param name="destination"></param>
        public void AddDestination(Vector2 destination)
        {
            //float x = Mathf.Clamp(destination.x, Level.MinX, Level.MaxX);
            //float y = Mathf.Clamp(destination.y, Level.MinY, Level.MaxY);

            //destination = new Vector2(x, y);

            _destinations.Add(destination); 
        }
        public void ClearDestinations()
        {
            _destinations.Clear();
        }
        public void RemoveDestination(Vector2 destination)
        {
            _destinations.Remove(destination);
        }
        public Vector2 GetDestination()
        {
            return GetDestinations().FirstOrDefault();
        }
        /// <summary>
        /// Sets the destination and moves the squad to it.
        /// </summary>
        /// <param name="destination"></param>
        public void SetAndMove(Vector2 destination)
        {
            SetDestination(destination);
            GetSquad().Move(GetDestination());
        }
        /// <summary>
        /// Moves the squad towards the enemy squad's current position.
        /// </summary>
        public void MoveTowardsEnemies()
        {
            GetSquad().GetShips().ForEach((ship) =>
            {
                //Debug.Log($"Ship: {ship?.Name}");
                ship.MoveToPoint(ship.SetAndGetTargetEnemy().GetPosition());

            });
            //Debug.Log("----");
            //try
            //{
            //    Squad.GetShips().ForEach((ship) =>
            //    {
            //        Debug.Log($"Ship: {ship?.Name}");
            //        ship.MoveToPoint(ship.SetAndGetTargetEnemy().GetPosition());

            //    });
            //}
            //catch (Exception e)
            //{
            //    Debug.Log($"Command: {this}, Ships: {Utilities.ListToString(Squad.GetShips())}");
            //    throw e;
            //}

        }
        protected void Timeout()
        {
            SetFinalize("The command ran out of time");
        }


        public List<Ship> MakeTargetingQueue()
        {

            _tempShips = EnemySquad.GetShips();
            ConfigData.ShootingStrategyTypes strategy = GetSquad().GetShootingStrategy();
            //Debug.Log($"Making targeting queue for {Ship.Name}. The squad is using {Squad.GetShootingStrategy()}");
            switch (strategy)
            {
                case ConfigData.ShootingStrategyTypes.FirstSeen:
                    return _tempShips;
                case ConfigData.ShootingStrategyTypes.Random:
                    _tempShips.Shuffle();
                    break;
                case ConfigData.ShootingStrategyTypes.Revenge:
                    _tempShips.Sort((a, b) => b.LastKilled - a.LastKilled);
                    break;
                case ConfigData.ShootingStrategyTypes.MostDangerous:
                    _tempShips.Sort((a, b) => b.FleetShip.DamageDone - a.FleetShip.DamageDone);
                    break;
                case ConfigData.ShootingStrategyTypes.LeastHealth:
                    _tempShips.Sort((a, b) => (a.Health - a.OriginalHealth) - (b.Health - b.OriginalHealth));
                    break;
                case ConfigData.ShootingStrategyTypes.MostHealth:
                    _tempShips.Sort((a, b) => b.Health - a.Health);
                    break;
                case ConfigData.ShootingStrategyTypes.MostPowerful:
                    _tempShips.Sort((a, b) => (int)(b.Firepower - a.Firepower));
                    break;
                case ConfigData.ShootingStrategyTypes.LeastPowerful:
                    _tempShips.Sort((a, b) => (int)(a.Firepower - b.Firepower));
                    break;
                case ConfigData.ShootingStrategyTypes.Closest:
                    _tempShips.Sort((a, b) => (int)(GetSquad().DistanceToPoint(a.GetPosition()) - GetSquad().DistanceToPoint(b.GetPosition())));
                    break;
                case ConfigData.ShootingStrategyTypes.Furthest:
                    _tempShips.Sort((a, b) => (int)(GetSquad().DistanceToPoint(b.GetPosition()) - GetSquad().DistanceToPoint(a.GetPosition())));
                    break;
                case ConfigData.ShootingStrategyTypes.MostRange:
                    _tempShips.Sort((a, b) => b.MaxRange - a.MaxRange);
                    break;
                case ConfigData.ShootingStrategyTypes.LeastRange:
                    _tempShips.Sort((a, b) => a.MaxRange - b.MaxRange);
                    break;
                case ConfigData.ShootingStrategyTypes.Fastest:
                    _tempShips.Sort((a, b) => (int)(b.Speed - a.Speed));
                    break;
                case ConfigData.ShootingStrategyTypes.Slowest:
                    _tempShips.Sort((a, b) => (int)(a.Speed - b.Speed));
                    break;
                case ConfigData.ShootingStrategyTypes.MostValuable:
                    _tempShips.Sort((a, b) => b.Tsv - a.Tsv);
                    break;
                case ConfigData.ShootingStrategyTypes.LeastValuable:
                    _tempShips.Sort((a, b) => a.Tsv - b.Tsv);
                    break;
                default:
                    if ((int)strategy > 15)
                    {
                        ConfigData.ShipTypeLetters type = Utilities.ConvertShipTypeToShipTypeLetter[Utilities.ConvertShootingStrategyToShipType[strategy]];
                        _tempShips.Sort((a, b) =>
                        {
                            //Debug.Log($"Strategy: {strategy}, Type: {type}, A ShipTypeLetter: {a.ShipTypeLetter}, B ShipTypeLetter: {b.ShipTypeLetter}");
                            if (a.ShipTypeLetter == type && b.ShipTypeLetter != type)
                            {
                                return -1;
                            }
                            else if (b.ShipTypeLetter == type && a.ShipTypeLetter != type)
                            {
                                return 1;
                            }
                            else
                            {
                                return 0;
                            }
                        });
                        //if (_f_queue.Count > 0)
                        //{
                        //    Debug.Log($"The first entry in the sorted _f_queue is {_f_queue.First().Name}");
                        //}
                        return _tempShips;
                    }
                    else
                    {
                        return _tempShips;
                    }
            }
            return _tempShips;
        }

        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for PrepareDamageToSendEntries() method:
        //////////////////////////////////////////////////////////////////////////////

        private Squad _prepareDamage_closestEnemy;
        /// <summary>
        /// This method finds the enemies of the command's squad and makes sure there's a ship damage status entry for each enemy ship.
        /// If there's not an enemy squad it returns the closest visible squad
        /// </summary>
        /// <param name="which"></param>
        public void PrepareDamageToSendEntries(int which = 0)
        {
            if (!GetSquad().IsDefenseless)
            {
                _tempShips = new List<Ship>();

                if (which == 1) // closest
                {
                    _prepareDamage_closestEnemy = GetSquad().GetClosestEnemySquad();
                    if (_prepareDamage_closestEnemy != null)
                    {
                        _tempShips = _prepareDamage_closestEnemy.GetShips();
                    }
                }
                else if (EnemySquad != null)
                {
                    _tempShips = EnemySquad.GetShips();
                }

                foreach (Ship ship in _tempShips)
                {
                    Level.State.GetShipDamageStatus(Side, ship);
                }
            }
        }

        // Finalizes the command and stops the squad so long as the command hasn't already been finalized
        public virtual void SetFinalize(string cause)
        {
            //if (!Squad.IsDead)
            //{
            //    //StandStill();
            //    //Squad.StopMoving("Command ended");
            //}
            Finalize(cause);


        }
        public void SquadKilled()
        {
            if (CommandType == ConfigData.CommandTypes.Mining)
            {
                ((Mining)this).CleanupAsteroid();
            }else if (CommandType == ConfigData.CommandTypes.FullRetreat)
            {
                ((FullRetreat)this).CleanupWarpGate();
            }
            SetFinalize("This squad got killed");
        }
        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for Finalize() method:
        //////////////////////////////////////////////////////////////////////////////

        private StoredCommand _finalize_storedCommand;
        private StoredCommand _finalize_squadCommand;

        private string _finalize_enemyName;

        private void Finalize(string cause)
        {
            if (!IsDead)
            {

                //Debug.Log($"Finalizing Command {this} because of {cause}");
                if (cause == "")
                {
                    Debug.LogError($"Trying to finalize Command without cause");
                }
                Level.CancelTimer(CommandTimer);
                Level.CancelTimer(TimeoutTimer);
                //CancelInvoke();
                StopAllCoroutines();

                FinalizationCause = cause;
                IsFinalized = true;
                IsDead = true;

                // It seems like none of these are needed
                //MatchupStrategy.Kill(); // Only needed if we are storing the Matchup strategy which we are currently not
                //ShootingStrategy.Kill();

                _tempShips = GetSquad().GetShips();
                _tempShips.ForEach(ship => ship.TargetEnemyShipToFollow = null);

                GetSquad().Status = "idle";
                if (GetSquad().IsChasing())
                {
                    GetSquad().SetChase(false);
                }
                if (GetSquad().IsSelected && Stage.Menus != null)
                {
                    Stage.Menus.ActionBox.HighlightSelectedButtons();
                }

                //Debug.Log($"Finalizing and setting Squad Command #{OutcomeId}:{Strategy?.CommandType} to null for {Squad.Name} because of {FinalizationCause}");

                GetSquad().SetCommandNull();
                GetSquad().HasCommand = false;
                Level.State.CommandsToRelease.Add(this);

                if (GetSquad().IsHiveMindControlled && Stage.ActivateHiveMind)
                {
                    //if (GetSquad().GetShips().Any((s) => s.ShipType == ConfigData.ShipTypes.Beacon))
                    //{
                    //    Debug.Log($"{this} has a squad with beacons and it just finalized");
                    //}
                    GetSquad().AddToCommandList();
                }
                // If this is a hivemind command with an outcomeId then find the past command in the state, update the TSV, and finalize it
                if (IsHiveMindCommand && OutcomeId > 0)
                {

                    _finalize_storedCommand = Level.State.PastCommands[Level.State.OutcomeIdToPastCommandIndex[OutcomeId]];
                    //try
                    //{
                    //    _finalize_storedCommand = Level.State.PastCommands[Level.State.OutcomeIdToPastCommandIndex[OutcomeId]];

                    //}
                    //catch (Exception e)
                    //{
                    //    Debug.Log($"OutcomeId: {OutcomeId}, Past Commands Count: {Level.State.PastCommands.Count}");
                    //    throw e;
                    //}
                    //if (_finalize_storedCommand.IsStored)
                    //{
                    //    Debug.LogError($"Trying to finalize a command #${OutcomeId} with cause [{cause}] that has already been stored");
                    //    return;
                    //}

                    _finalize_storedCommand.Tsv = Tsv;
                    _finalize_storedCommand.IsFinalized = true;

                    if (Stage.DebugLogger.IsDebugging)
                    {
                        _finalize_squadCommand = GetSquad().PastCommands.FirstOrDefault(c => c.OutcomeId == OutcomeId);
                        if (_finalize_squadCommand != null)
                        {
                            _finalize_enemyName = EnemySquad != null ? EnemySquad.Name : "N/A";
                            _finalize_squadCommand.Enemy = _finalize_enemyName;
                            _finalize_squadCommand.Tsv = Tsv;
                            _finalize_squadCommand.FinalizationCause = cause;
                            _finalize_squadCommand.IsFinalized = true;
                        }
                        else
                        {
                            Debug.LogError($"Could not find squad command for OutcomeId {OutcomeId} in Squad {GetSquad().Name}");
                        }
                    }
                }

                ClearData();
                enabled = false;
                //Stage.Pool.ReturnCommandToPool(this);
            }
            else
            {
                Debug.LogError($"Trying to finalize a command ({this}) that's already been finalized with {FinalizationCause}");
            }
            
        }

        public override string ToString()
        {
            return $"Command #{(OutcomeId != 0 ? OutcomeId : "N/A")} with Strategy {CommandType} attached to " +
                $"Squad {GetSquad()} with Enemy Squad: {EnemySquad?.Name} and OriginalId: "; 
                // +$"#{OriginalSquadId} ItemId: #{ItemId} and CreationId: #{CreationId}";
        }

        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
            Command x = obj as Command;
            if (x == null)
            {
                return false;
            }

            return ItemId == x.ItemId;
        }

        public bool Equals(Command other)
        {
            return ItemId == other.ItemId;
        }

        public override int GetHashCode()
        {
            return ItemId.GetHashCode();
        }

        public static bool operator ==(Command a, Command b)
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

            return a.ItemId == b.ItemId;
        }

        public static bool operator !=(Command a, Command b)
        {
            return !(a == b);
        }

    }
}
