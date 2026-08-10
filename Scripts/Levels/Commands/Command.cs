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
        public bool HasStoredOutcomeRecord;
        /// <summary>
        /// Keeps the Id of the original enemy squad so we can check if the enemy has died and been respawned as a new squad in between timer() calls
        /// </summary>
        public int OriginalEnemyId;

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
            HasStoredOutcomeRecord = false;
        }
        public virtual void Setup(Squad squad, bool isHiveMindCommand, Squad enemy, string matchup)
        {
            IsDead = false;
            Level = squad.Level;
            Stage.DebugLogger.__CommandCounts[(int)CommandType]++;
            Side = squad.Side;
            SetSquad(squad);
            EnemySquad = enemy;
            Matchup = matchup;
            ItemId = Level.State.GetId();
            IsHiveMindCommand = isHiveMindCommand;

            if (EnemySquad != null)
            {
                OriginalQueue = new Queue<Ship>(MakeTargetingQueue());
                TargetingQueue = new Queue<Ship>(OriginalQueue);
                HasEnemy = true;
                OriginalEnemyId = EnemySquad.ItemId;
            }
            enabled = true;
        }
        public Squad GetSquad()
        {
            return _squad;
        }
        public void SetSquad(Squad squad)
        {
            // Setup may happen while a command is only being prepared for a scripted queue.
            // Active ownership begins when execution starts, not when context is attached.
            _squad = squad;
        }
        public virtual void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy)
        {
            GetSquad().HasCommand = true;
            if (noEnemy || HasEnemy)
            {
                OutcomeId = commandOutcomeId;
                ShootingStrategy.Setup(shootingStrategy, shootingStrategyOutcomeId);
                HasShootingStrategy = true;
                MatchupStrategy.Setup(GetSquad().MatchupStrategy.MatchupType, GetSquad().MatchupStrategy.OutcomeId, GetSquad());
                GetSquad().SetShootingStrategy(ShootingStrategy.ShootingStrategyType);
                GetSquad().ClearTargets();

                if (Stage.DebugLogger.IsDebugging)
                {
                    GetSquad().PastCommands.Add(new StoredCommand(this));
                }
                HasStoredOutcomeRecord = Level.State.AddCommand(this);
                GetSquad().Status = $"Executing Command #{OutcomeId}";
            }
            else
            {
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
            GetSquad().Move(GetDestination());
        }
        public void MoveTowardsEnemies()
        {
            foreach (Ship ship in GetSquad().GetShips())
            {
                Ship target = ship.SetAndGetTargetEnemy();
                if (target == null)
                {
                    SetFinalize("No more enemy ships to target");
                    return;
                }
                ship.MoveToPoint(target.GetPosition());
            }
        }
        protected void Timeout()
        {
            SetFinalize("The command ran out of time");
        }

        public List<Ship> MakeTargetingQueue()
        {
            // Shooting strategies reorder their working list. GetShips() returns the
            // squad's authoritative internal list, so always sort/shuffle a snapshot.
            _tempShips = EnemySquad.GetShips().ToList();
            ConfigData.ShootingStrategyTypes strategy = GetSquad().GetShootingStrategy();
            switch (strategy)
            {
                case ConfigData.ShootingStrategyTypes.FirstSeen:
                    return _tempShips;
                case ConfigData.ShootingStrategyTypes.Random:
                    _tempShips.Shuffle();
                    break;
                case ConfigData.ShootingStrategyTypes.Revenge:
                    _tempShips.Sort((a, b) => b.LastKilled.CompareTo(a.LastKilled));
                    break;
                case ConfigData.ShootingStrategyTypes.MostDangerous:
                    _tempShips.Sort((a, b) => b.FleetShip.DamageDone.CompareTo(a.FleetShip.DamageDone));
                    break;
                case ConfigData.ShootingStrategyTypes.LeastHealth:
                    _tempShips.Sort((a, b) => a.Health.CompareTo(b.Health));
                    break;
                case ConfigData.ShootingStrategyTypes.MostHealth:
                    _tempShips.Sort((a, b) => b.Health.CompareTo(a.Health));
                    break;
                case ConfigData.ShootingStrategyTypes.MostPowerful:
                    _tempShips.Sort((a, b) => b.Firepower.CompareTo(a.Firepower));
                    break;
                case ConfigData.ShootingStrategyTypes.LeastPowerful:
                    _tempShips.Sort((a, b) => a.Firepower.CompareTo(b.Firepower));
                    break;
                case ConfigData.ShootingStrategyTypes.Closest:
                    _tempShips.Sort((a, b) => GetSquad().DistanceToPoint(a.GetPosition()).CompareTo(GetSquad().DistanceToPoint(b.GetPosition())));
                    break;
                case ConfigData.ShootingStrategyTypes.Furthest:
                    _tempShips.Sort((a, b) => GetSquad().DistanceToPoint(b.GetPosition()).CompareTo(GetSquad().DistanceToPoint(a.GetPosition())));
                    break;
                case ConfigData.ShootingStrategyTypes.MostRange:
                    _tempShips.Sort((a, b) => b.MaxRange.CompareTo(a.MaxRange));
                    break;
                case ConfigData.ShootingStrategyTypes.LeastRange:
                    _tempShips.Sort((a, b) => a.MaxRange.CompareTo(b.MaxRange));
                    break;
                case ConfigData.ShootingStrategyTypes.Fastest:
                    _tempShips.Sort((a, b) => b.Speed.CompareTo(a.Speed));
                    break;
                case ConfigData.ShootingStrategyTypes.Slowest:
                    _tempShips.Sort((a, b) => a.Speed.CompareTo(b.Speed));
                    break;
                case ConfigData.ShootingStrategyTypes.MostValuable:
                    _tempShips.Sort((a, b) => b.Tsv.CompareTo(a.Tsv));
                    break;
                case ConfigData.ShootingStrategyTypes.LeastValuable:
                    _tempShips.Sort((a, b) => a.Tsv.CompareTo(b.Tsv));
                    break;
                default:
                    if ((int)strategy > 15)
                    {
                        ConfigData.ShipTypeLetters type = Utilities.ConvertShipTypeToShipTypeLetter[Utilities.ConvertShootingStrategyToShipType[strategy]];
                        _tempShips.Sort((a, b) =>
                        {
                            if (a.ShipTypeLetter == type && b.ShipTypeLetter != type)
                            {
                                return -1;
                            }
                            else if (b.ShipTypeLetter == type && a.ShipTypeLetter != type)
                            {
                                return 1;
                            }
                            return 0;
                        });
                        return _tempShips;
                    }
                    return _tempShips;
            }
            return _tempShips;
        }

        private Squad _prepareDamage_closestEnemy;
        public void PrepareDamageToSendEntries(int which = 0)
        {
            if (!GetSquad().IsDefenseless)
            {
                _tempShips = new List<Ship>();

                if (which == 1)
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

        public virtual void SetFinalize(string cause)
        {
            Finalize(cause);
        }
        public void SquadKilled()
        {
            if (CommandType == ConfigData.CommandTypes.Mining)
            {
                ((Mining)this).CleanupAsteroid();
            }
            else if (CommandType == ConfigData.CommandTypes.FullRetreat)
            {
                ((FullRetreat)this).CleanupWarpGate();
            }
            SetFinalize("This squad got killed");
        }

        private StoredCommand _finalize_storedCommand;
        private StoredCommand _finalize_squadCommand;
        private string _finalize_enemyName;

        private void Finalize(string cause)
        {
            if (!IsDead)
            {
                Debug.Log($"Finalizing Command {this} because of {cause}");
                if (cause == "")
                {
                    Debug.LogError($"Trying to finalize Command without cause");
                }
                Level.CancelTimer(CommandTimer);
                Level.CancelTimer(TimeoutTimer);
                StopAllCoroutines();

                FinalizationCause = cause;
                IsFinalized = true;
                IsDead = true;

                _tempShips = GetSquad().GetShips();
                _tempShips.ForEach(ship => ship.TargetEnemyShipToFollow = null);
                Level.State.CommandsToRelease.Add(this);

                if (!GetSquad().IsDead)
                {
                    GetSquad().SetCommandNull();
                    GetSquad().HasCommand = false;
                    GetSquad().Status = "idle";
                    if (GetSquad().IsChasing())
                    {
                        GetSquad().SetChase(false);
                    }
                    if (GetSquad().IsSelected && Stage.Menus.HasSquadActionBox)
                    {
                        Stage.Menus.ActionBox.HighlightSelectedButtons();
                    }

                    if (IsHiveMindCommand && Stage.ActivateHiveMind)
                    {
                        GetSquad().AddToCommandList();
                    }
                    else
                    {
                        GetSquad().RunCommandQueue();
                    }
                    if (GetSquad().IsUserControlled && GetSquad().IsLockedOn)
                    {
                        GetSquad().IsLockedOn = false;
                    }
                }

                if (IsHiveMindCommand && OutcomeId > 0 && HasStoredOutcomeRecord)
                {
                    if (Level.State.OutcomeIdToPastCommandIndex.TryGetValue(OutcomeId, out int storedCommandIndex) &&
                        storedCommandIndex >= 0 &&
                        storedCommandIndex < Level.State.PastCommands.Count &&
                        Level.State.PastCommands[storedCommandIndex].OutcomeId == OutcomeId)
                    {
                        _finalize_storedCommand = Level.State.PastCommands[storedCommandIndex];
                    }
                    else
                    {
                        _finalize_storedCommand = null;
                        Debug.LogError($"Could not finalize command outcome #{OutcomeId}: its stored-command mapping is missing or stale.");
                    }

                    if (_finalize_storedCommand != null)
                    {
                        _finalize_storedCommand.Tsv = Tsv;
                        _finalize_storedCommand.IsFinalized = true;
                    }

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
                            Debug.LogError($"Could not find squad command for OutcomeId #{OutcomeId} in Squad {GetSquad().Name}");
                        }
                    }
                }

                ClearData();
                enabled = false;
            }
            else
            {
                Debug.LogError($"Trying to finalize a command ({this}) that's already been finalized with {FinalizationCause}");
            }
        }

        public override string ToString()
        {
            return $"Command #{(OutcomeId != 0 ? OutcomeId : "N/A")} #[{ItemId}] with Strategy {CommandType} attached to " +
                $"Squad {GetSquad()} with Enemy Squad: {EnemySquad?.Name}"; 
        }

        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

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
            if (System.Object.ReferenceEquals(a, b))
            {
                return true;
            }

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