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
        public long Age, Tsv;  
        /// <summary>
        /// The Id of this command relative to the server.
        /// </summary>
        public long OutcomeId = 0; 
        public Squad EnemySquad, Squad;
        public string Matchup, FinalizationCause;
        public ConfigData.CommandTypes CommandType = ConfigData.CommandTypes.Uninitialized;
        public Strategy Strategy;
        public MatchupStrategy MatchupStrategy;
        public ShootingStrategy ShootingStrategy;
        public bool IsAttacking, IsCloseToTarget;
        public bool HasStrategy;
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

        public bool IsFinalized, IsStored, IsHiveMindCommand;

        public virtual void Create(Stage stage)
        {
            Stage = stage;
            Strategy = new Strategy();
            ShootingStrategy = new ShootingStrategy();
        }
        public virtual void ClearData()
        {
            Tsv = 0;
            OutcomeId = 0;
            EnemySquad = null;
            FinalizationCause = null;
            CommandType = ConfigData.CommandTypes.Uninitialized;
            IsAttacking = false;
            IsCloseToTarget = false;
            HasStrategy = false;
            HasShootingStrategy = false;
            HasEnemy = false;
            OriginalQueue.Clear();
            TargetingQueue.Clear();
            _destinations.Clear();
            IsFinalized = false;
            IsStored = false;
        }
        public void Setup(Squad squad, bool isHiveMindCommand, Squad enemy, string matchup)
        {
            ClearData();
            Level = squad.Level;
            Side = squad.Side;
            Squad = squad;
            Squad.HasCommand = true;
            EnemySquad = enemy;
            Matchup = matchup;
            IsHiveMindCommand = isHiveMindCommand;
            ItemId = Level.State.GetId();

            if (EnemySquad != null)
            {
                OriginalQueue = new Queue<Ship>(MakeTargetingQueue());
                TargetingQueue = new Queue<Ship>(OriginalQueue);
                HasEnemy = true;
            }
        }
        public virtual void Execute(ConfigData.CommandTypes commandType, ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy)
        {
            if (noEnemy || HasEnemy)
            {
                OutcomeId = commandOutcomeId;
                CommandType = commandType;
                Strategy.Setup(CommandType, OutcomeId);
                ShootingStrategy.Setup(shootingStrategy, shootingStrategyOutcomeId);
                HasShootingStrategy = true;
                Squad.SetShootingStrategy(ShootingStrategy.ShootingStrategyType);
                Squad.ClearTargets(); // Clear all old targets before starting the new command

                if (!Stage.IsTraining)
                {
                    Squad.PastCommands.Add(new StoredCommand(this));
                }
                Level.State.AddCommand(this);

                Squad.Status = $"Executing Command #{OutcomeId}";
                //Debug.Log("Set status for command");
            }
            else
            {
                //Debug.Log($"Could not find the enemy for command #{commandOutcomeId}");
                SetFinalize("Could not find the enemy squad for command");
                return;
            }

        }

        public void SetDestination(Vector2 destination)
        {
            ClearDestinations();
            AddDestination(destination);
        }
        public List<Vector2> GetDestinations()
        {
            return _destinations;
        }
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
        public void SetAndMove(Vector2 destination)
        {
            SetDestination(destination);
            Squad.Move(GetDestination());
        }
        public void MoveTowardsEnemies()
        {
            Squad.GetShips().ForEach((ship) =>
            {
                ship.MoveToPoint(ship.SetAndGetTargetEnemy().GetPosition());

            });
        }
        protected void Timeout()
        {
            SetFinalize("The command ran out of time");
        }


        private List<Ship> _f_queue;
        public List<Ship> MakeTargetingQueue()
        {

            _f_queue = EnemySquad.GetShips();
            ConfigData.ShootingStrategyTypes strategy = Squad.GetShootingStrategy();
            //Debug.Log($"Making targeting queue for {Ship.Name}. The squad is using {Squad.GetShootingStrategy()}");
            switch (strategy)
            {
                case ConfigData.ShootingStrategyTypes.FirstSeen:
                    return _f_queue;
                case ConfigData.ShootingStrategyTypes.Random:
                    _f_queue.Shuffle();
                    break;
                case ConfigData.ShootingStrategyTypes.Revenge:
                    _f_queue.Sort((a, b) => b.LastKilled - a.LastKilled);
                    break;
                case ConfigData.ShootingStrategyTypes.MostDangerous:
                    _f_queue.Sort((a, b) => b.FleetShip.DamageDone - a.FleetShip.DamageDone);
                    break;
                case ConfigData.ShootingStrategyTypes.LeastHealth:
                    _f_queue.Sort((a, b) => a.Health - b.Health);
                    break;
                case ConfigData.ShootingStrategyTypes.MostHealth:
                    _f_queue.Sort((a, b) => b.Health - a.Health);
                    break;
                case ConfigData.ShootingStrategyTypes.MostPowerful:
                    _f_queue.Sort((a, b) => (int)(b.Firepower - a.Firepower));
                    break;
                case ConfigData.ShootingStrategyTypes.LeastPowerful:
                    _f_queue.Sort((a, b) => (int)(a.Firepower - b.Firepower));
                    break;
                case ConfigData.ShootingStrategyTypes.Closest:
                    _f_queue.Sort((a, b) => (int)(Squad.DistanceToPoint(a.GetPosition()) - Squad.DistanceToPoint(b.GetPosition())));
                    break;
                case ConfigData.ShootingStrategyTypes.Furthest:
                    _f_queue.Sort((a, b) => (int)(Squad.DistanceToPoint(b.GetPosition()) - Squad.DistanceToPoint(a.GetPosition())));
                    break;
                case ConfigData.ShootingStrategyTypes.MostRange:
                    _f_queue.Sort((a, b) => b.MaxRange - a.MaxRange);
                    break;
                case ConfigData.ShootingStrategyTypes.LeastRange:
                    _f_queue.Sort((a, b) => a.MaxRange - b.MaxRange);
                    break;
                case ConfigData.ShootingStrategyTypes.Fastest:
                    _f_queue.Sort((a, b) => (int)(b.Speed - a.Speed));
                    break;
                case ConfigData.ShootingStrategyTypes.Slowest:
                    _f_queue.Sort((a, b) => (int)(a.Speed - b.Speed));
                    break;
                case ConfigData.ShootingStrategyTypes.MostValuable:
                    _f_queue.Sort((a, b) => b.Tsv - a.Tsv);
                    break;
                case ConfigData.ShootingStrategyTypes.LeastValuable:
                    _f_queue.Sort((a, b) => a.Tsv - b.Tsv);
                    break;
                default:
                    if ((int)strategy > 15)
                    {
                        ConfigData.ShipTypeLetters type = Utilities.ConvertShipTypeToShipTypeLetter[Utilities.ConvertShootingStrategyToShipType[strategy]];
                        _f_queue.Sort((a, b) =>
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
                        return _f_queue;
                    }
                    else
                    {
                        return _f_queue;
                    }
            }
            return _f_queue;
        }

        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for PrepareDamageToSendEntries() method:
        //////////////////////////////////////////////////////////////////////////////

        private List<Ship> _prepareDamage_ships = new List<Ship>();
        private Squad _prepareDamage_closestEnemy;
        /// <summary>
        /// This method finds the enemies of the command's squad and makes sure there's a ship damage status entry for each enemy ship
        /// </summary>
        /// <param name="which"></param>
        public void PrepareDamageToSendEntries(string which = "")
        {
            if (!Squad.IsDefenseless)
            {
                _prepareDamage_ships.Clear();

                if (which == "closest")
                {
                    _prepareDamage_closestEnemy = Squad.GetClosestEnemySquad();
                    if (_prepareDamage_closestEnemy != null)
                    {
                        _prepareDamage_ships = _prepareDamage_closestEnemy.GetShips();
                    }
                }
                else if (EnemySquad != null)
                {
                    _prepareDamage_ships = EnemySquad.GetShips();
                }

                foreach (Ship ship in _prepareDamage_ships)
                {
                    Level.State.GetShipDamageStatus(Side, ship);
                }
            }
        }

        // Finalizes the command and stops the squad so long as the command hasn't already been finalized
        public virtual void SetFinalize(string cause)
        {
            if (!Squad.IsDead)
            {
                //StandStill();
                //Squad.StopMoving("Command ended");
                Finalize(cause);
            }

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
            Finalize("This squad got killed");
        }
        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for Finalize() method:
        //////////////////////////////////////////////////////////////////////////////

        private StoredCommand _finalize_storedCommand;
        private StoredCommand _finalize_squadCommand;

        private List<Ship> _finalize_ships;

        private string _finalize_enemyName;

        private bool _finalize_isStored;

        private void Finalize(string cause)
        {
            CancelInvoke();
            StopAllCoroutines();

            FinalizationCause = cause;
            IsFinalized = true;
            Strategy.Kill();
            MatchupStrategy.Kill();
            ShootingStrategy.Kill();

            _finalize_ships = Squad.GetShips();
            _finalize_ships.ForEach(ship => ship.TargetEnemyShipToFollow = null);

            Squad.Status = "idle";
            if (Squad.IsChasing())
            {
                Squad.SetChase(false);
            }
            if (Squad.IsSelected && Stage.Menus != null)
            {
                Stage.Menus.ActionBox.HighlightSelectedButtons();
            }

            Debug.Log($"Finalizing and setting Squad Command #{OutcomeId}:{Strategy?.CommandType} to null for {Squad.Name} because of {FinalizationCause}");

            Squad.Command = null;
            Squad.HasCommand = false;

            if (Squad.IsHiveMindControlled && Stage.ActivateHiveMind)
            {
                Squad.AddToCommandList();
            }

            if (Stage.IsDebugging)
            {
                _finalize_storedCommand = Level.State.GetPastCommands().FirstOrDefault(c => c.OutcomeId == OutcomeId);
                if (_finalize_storedCommand != null)
                {
                    _finalize_isStored = _finalize_storedCommand.IsStored;
                    if (_finalize_isStored)
                    {
                        Debug.Log($"Trying to finalize a command #${OutcomeId} with cause [{cause}] that has already been stored");
                        return;
                    }

                    _finalize_storedCommand.Tsv = Tsv;
                    _finalize_storedCommand.IsFinalized = true;

                    _finalize_squadCommand = Squad.PastCommands.FirstOrDefault(c => c.OutcomeId == OutcomeId);
                    if (_finalize_squadCommand != null)
                    {
                        _finalize_enemyName = EnemySquad != null ? EnemySquad.Name : "";
                        _finalize_squadCommand.Enemy = _finalize_enemyName;
                        _finalize_squadCommand.Tsv = Tsv;
                        _finalize_squadCommand.FinalizationCause = cause;
                    }
                }
                else
                {
                    Debug.Log($"Couldn't find a past command with id #{OutcomeId}  with cause [{cause}]");
                }
            }

            Stage.Pool.ReturnCommandToPool(this);
        }

        public override string ToString()
        {
            return $"Command #{(OutcomeId != 0 ? OutcomeId : "N/A")} with Strategy {(HasStrategy ? Strategy.CommandType : "N/A")} attached to " +
                $"Squad #{Squad.Id} - {Squad.Name}";
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
