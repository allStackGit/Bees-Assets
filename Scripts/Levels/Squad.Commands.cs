using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Server;
using Assets.Scripts.UI_Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Squad
    {
        public HashSet<ConfigData.CommandTypes> MovementAttackTypes = new HashSet<ConfigData.CommandTypes>
        {
            ConfigData.CommandTypes.CircleSquad,
            ConfigData.CommandTypes.RightSwipe,
            ConfigData.CommandTypes.LeftSwipe,
            ConfigData.CommandTypes.InAndOut,
            ConfigData.CommandTypes.BombingRun
        };
        public bool HasMovementAttackType;

        public void ResetCommandCache()
        {
            HasMovementAttackType = GetCommand() != null && MovementAttackTypes.Contains(GetCommand().CommandType);
        }

        public Command GetCommand() => _command;

        public void SetCommand(Command command)
        {
            Debug.Log($"Setting {this} Command to {command}");
            _command = command;
            HasCommand = command != null;
            ResetCommandCache();
        }

        public void SetCommandNull()
        {
            _command = null;
            HasCommand = false;
            HasMovementAttackType = false;
        }

        private void DiscardPreparedCommand(Command command)
        {
            if (command == null || ReferenceEquals(command, _command))
            {
                return;
            }

            if (Level != null)
            {
                Level.CancelTimer(command.CommandTimer);
                Level.CancelTimer(command.TimeoutTimer);
            }
            command.StopAllCoroutines();
            command.ClearData();
            command.IsDead = true;
            command.enabled = false;

            if (Level?.State != null && !Level.State.CommandsToRelease.Contains(command))
            {
                Level.State.CommandsToRelease.Add(command);
            }
        }

        private void CancelScriptedCommandQueue()
        {
            HasCommandQueue = false;
            CommandQueueEmptyAction = null;
            while (CommandQueue.Count > 0)
            {
                DiscardPreparedCommand(CommandQueue.Dequeue());
            }
        }

        public void AddToCommandList()
        {
            Debug.Log($"Adding {this} to squads awaiting hive mind commands");
            Level.State.AddToSquadsAwaitingHiveMindCommands(this);
        }

        public bool IsInBounds()
        {
            if (!_isInBounds)
            {
                _isInBounds = GetShips().All(s => s.IsInBounds());
            }
            return _isInBounds;
        }

        public void RunCommandQueue()
        {
            if (IsDead)
            {
                return;
            }

            if (CommandQueue.Count > 0)
            {
                Command nextCommand = CommandQueue.Dequeue();
                SetCommand(nextCommand);
                switch (nextCommand.CommandType)
                {
                    case ConfigData.CommandTypes.MoveToPoint:
                        ((MoveToPoint)nextCommand).Execute(GetShootingStrategy(), 0, 0);
                        break;
                    case ConfigData.CommandTypes.MoveToRandom:
                        ((MoveToRandom)nextCommand).Execute(GetShootingStrategy(), 0, 0);
                        break;
                    case ConfigData.CommandTypes.Aggressive:
                        ((Aggressive)nextCommand).Execute(GetShootingStrategy(), 0, 0);
                        break;
                    default:
                        Debug.LogError($"Unsupported scripted command type {nextCommand.CommandType} for {this}");
                        nextCommand.SetFinalize("Unsupported scripted command type");
                        break;
                }
                return;
            }

            if (HasCommandQueue)
            {
                CommandQueueEmptyAction?.Invoke();
            }
            else if (IsHiveMindControlled && !IsImmobile)
            {
                AddToCommandList();
            }
        }

        private HashSet<ConfigData.ShipTypes> _banned, _enemyShips;
        private string[] _bannedTypes;
        public List<Ship> GetShipsForMatchup()
        {
            return GetShips().Take(64).ToList();
        }

        public void MakeMatchupStrat()
        {
            _banned = ConfigData.UserProgressData.AllShipTypes;

            if (Side == ConfigData.Configuration.BeeSide)
            {
                _enemyShips = Level.State.GetHumanShipTypes();
                _banned = _banned.Where(type => !_enemyShips.Contains(type)).ToHashSet();
            }
            else
            {
                _enemyShips = Level.State.GetBeeShipTypes();
                _banned = _banned.Where(type => !_enemyShips.Contains(type)).ToHashSet();
            }

            _bannedTypes = _banned.Select(ship => $"Type {Utilities.ConvertShipTypeToCharacter[ship]}").ToArray();
            ConfigData.Socket.SendRequest(new MatchupStrategyRequest(
                new GetMatchupStrategy(AddToMatchup(GetShipsForMatchup()), OpponentId, _bannedTypes),
                this,
                Level,
                ConfigData.StandardMaxTimeOnQueue));
        }

        private static char[] _letters;
        public static string AddToMatchup(List<Ship> ships)
        {
            _letters = ships.Select(s => Utilities.ConvertShipTypeLetterToCharacter[s.ShipTypeLetter]).ToArray();
            Array.Sort(_letters);
            return new string(_letters);
        }

        private string _matchup;
        private readonly StringBuilder _sb = new StringBuilder();
        private HashSet<ConfigData.CommandTypes> _bannedStrats;
        private int _comparativeHealth, _friendlySquadCount, _closestFriendlySquadCount;
        private List<Ship> _matchupAllies;
        private List<Ship> _matchupEnemies;
        private readonly List<Ship> _matchupFriendlyHealthShips = new List<Ship>();

        private static double GetAverageHealthPercentForMatchup(List<Ship> ships)
        {
            if (ships == null || ships.Count == 0)
            {
                return 0d;
            }

            return ships.Average(ship => ship.OriginalHealth > 0
                ? ((double)ship.Health / ship.OriginalHealth) * 100d
                : 0d);
        }

        public void MakeMatchupAndGetCommand(Squad enemy = null)
        {
            _bannedStrats = BannedStrats.ToHashSet();
            _sb.Clear();
            _sb.Append(AddToMatchup(GetShipsForMatchup()));

            if (enemy != null)
            {
                _matchupEnemies = GetPotentialEnemies(enemy);
                if (_matchupEnemies.Count == 0)
                {
                    AddToCommandList();
                    return;
                }

                _matchupAllies = GetPotentialAllies(enemy);
                _matchupFriendlyHealthShips.Clear();
                _matchupFriendlyHealthShips.AddRange(GetShipsForMatchup());
                _matchupFriendlyHealthShips.AddRange(_matchupAllies.Take(Math.Max(0, 64 - _matchupFriendlyHealthShips.Count)));
                double friendlyHealth = GetAverageHealthPercentForMatchup(_matchupFriendlyHealthShips);
                double enemyHealth = GetAverageHealthPercentForMatchup(_matchupEnemies);
                _comparativeHealth = enemyHealth <= 0d
                    ? 165
                    : (int)Math.Round((friendlyHealth / enemyHealth) * 100d);

                if (_comparativeHealth < 50) _comparativeHealth = 0;
                else if (_comparativeHealth < 85) _comparativeHealth = 1;
                else if (_comparativeHealth < 115) _comparativeHealth = 2;
                else if (_comparativeHealth < 165) _comparativeHealth = 3;
                else _comparativeHealth = 4;

                _sb.Append(AddToMatchup(_matchupAllies));
                _sb.Append("|");
                _sb.Append(AddToMatchup(_matchupEnemies));
                _sb.Append("|");
                _sb.Append(enemy.IsAnySquadShipWithinRangeOfAnyOfOurSquadShips(this) ? 1 : 0);
                _sb.Append("|");
                _sb.Append(_comparativeHealth);
            }
            else
            {
                _bannedStrats.Add(ConfigData.CommandTypes.Aggressive);
                _bannedStrats.Add(ConfigData.CommandTypes.Retreat);
                _bannedStrats.Add(ConfigData.CommandTypes.CircleSquad);
                _bannedStrats.Add(ConfigData.CommandTypes.RightSwipe);
                _bannedStrats.Add(ConfigData.CommandTypes.LeftSwipe);
                _bannedStrats.Add(ConfigData.CommandTypes.InAndOut);
                _bannedStrats.Add(ConfigData.CommandTypes.FullRetreat);
                _bannedStrats.Add(ConfigData.CommandTypes.Hold);
                _sb.Append("||0|0");
            }

            _matchup = _sb.ToString();
            _closestFriendlySquadCount = Level.State.GetSquadsBySide(Side)
                .Count(squad => squad?.GetCommand()?.CommandType == ConfigData.CommandTypes.ClosestFriendly);
            _friendlySquadCount = Level.State.GetSquadsBySide(Side).Count;
            if (_friendlySquadCount - 1 <= _closestFriendlySquadCount)
            {
                _bannedStrats.Add(ConfigData.CommandTypes.ClosestFriendly);
            }

            // These are request-local availability conditions. Do not persist them into
            // BannedStrats: asteroids, healing capacity, Warp Gates, and squad composition
            // can change later in the same level.
            if (!Level.ActivateMining || !HasMiningShips || GetNearestMiningAsteroid() == null)
            {
                _bannedStrats.Add(ConfigData.CommandTypes.Mining);
            }
            if (Side != ConfigData.Configuration.HumanSide || !Level.State.HasWarpGates || HasOnlyWarpGates)
            {
                _bannedStrats.Add(ConfigData.CommandTypes.FullRetreat);
            }
            if (Side != ConfigData.Configuration.BeeSide || !Level.State.HasBeehives || HasOnlyBeehives)
            {
                _bannedStrats.Add(ConfigData.CommandTypes.Heal);
            }
            else if (Level.State.GetBeeShips().Count(s => s.IsBeehive && ((Beehive)s).ShipsHealingHere.Count < 4) == 0)
            {
                _bannedStrats.Add(ConfigData.CommandTypes.Heal);
            }

            ConfigData.Socket.SendRequest(new CommandRequest(
                new GetStrategy(_matchup, OpponentId, _bannedStrats.Select(b => Utilities.ConvertCommandTypeToName[b]).ToArray()),
                this,
                enemy,
                Level,
                _matchup,
                ConfigData.StandardMaxTimeOnQueue));
        }

        public void ClearTargets()
        {
            GetShips().ForEach(ship => ship.ClearTargets());
        }

        public void SetShootingStrategy(ConfigData.ShootingStrategyTypes strategy)
        {
            _chosenShootingStrategy = strategy;
            if (HasCommand && GetCommand() != null && GetCommand().HasShootingStrategy)
            {
                GetCommand().ShootingStrategy.ShootingStrategyType = strategy;
            }
            GetShips().ForEach(ship => ship.ShootingStrategy = _chosenShootingStrategy);
        }

        public ConfigData.ShootingStrategyTypes GetShootingStrategy() => _chosenShootingStrategy;

        public ConfigData.CommandTypes GetCommandStrategy()
        {
            return HasCommand && GetCommand() != null
                ? GetCommand().CommandType
                : ConfigData.CommandTypes.Uninitialized;
        }

        public void UserGuard(Squad squad)
        {
            if (!CanAcceptUserInput) return;
            MakeUserCommand(ConfigData.CommandTypes.Guard, null);
            ((Guard)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, squad);
            if (Level.Stage.DoesUserHaveController)
            {
                Level.Stage.Menus.ActionBox.HighlightSelectedButtons();
            }
        }

        public void UserPatrol(Vector2 topLeft, Vector2 bottomRight)
        {
            if (!CanAcceptUserInput) return;
            MakeUserCommand(ConfigData.CommandTypes.Patrol, null);
            ((Patrol)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, topLeft, bottomRight);
            if (Level.Stage.DoesUserHaveController)
            {
                Level.Stage.Menus.ActionBox.HighlightSelectedButtons();
            }
        }

        public void UserMining(MiningAsteroid miningAsteroid)
        {
            if (!CanAcceptUserInput) return;
            if (HasMiningShips)
            {
                MakeUserCommand(ConfigData.CommandTypes.Mining, null);
                ((Mining)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, miningAsteroid);
            }
            else
            {
                FinalizeUserCommand();
                Move(miningAsteroid.GetPosition());
            }
        }

        public void UserFullRetreat(WarpGate warpGate)
        {
            if (!CanAcceptUserInput) return;
            MakeUserCommand(ConfigData.CommandTypes.FullRetreat, null);
            ((FullRetreat)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, warpGate);
        }

        public void UserHeal(List<Beehive> beehives)
        {
            if (!CanAcceptUserInput) return;
            MakeUserCommand(ConfigData.CommandTypes.Heal, null);
            ((Heal)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0, beehives);
        }

        public void UserAggressive(Squad enemy)
        {
            if (!CanAcceptUserInput) return;
            if (HasOnlyBombers)
            {
                UserBombingRun(enemy);
                return;
            }
            MakeUserCommand(ConfigData.CommandTypes.Aggressive, enemy);
            ((Aggressive)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0);
            MarkTargets(enemy);
        }

        public void MarkTargets(Squad enemy)
        {
            if (!IsUserControlled || enemy == null) return;
            enemy.GetShips().ForEach(enemyShip =>
            {
                GameObject targetingMarker = Instantiate(Stage.Prefabs.TargetingSquadPrefab, enemyShip.transform);
                targetingMarker.transform.localPosition = Vector2.zero;
                targetingMarker.GetComponent<TargetingSquadMarker>().Setup(enemyShip);
            });
        }

        public void UserBombingRun(Squad enemy)
        {
            if (!CanAcceptUserInput) return;
            MakeUserCommand(ConfigData.CommandTypes.BombingRun, enemy);
            ((BombingRun)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0);
            MarkTargets(enemy);
        }

        public void MakeUserCommand(ConfigData.CommandTypes command, Squad enemy)
        {
            Level.RecordSimulationInput("user-command", $"{ItemId}|{command}|{(enemy == null ? -1 : enemy.ItemId)}");
            FinalizeUserCommand();

            switch (command)
            {
                case ConfigData.CommandTypes.Aggressive:
                case ConfigData.CommandTypes.BombingRun:
                case ConfigData.CommandTypes.Guard:
                case ConfigData.CommandTypes.Patrol:
                case ConfigData.CommandTypes.Mining:
                case ConfigData.CommandTypes.FullRetreat:
                case ConfigData.CommandTypes.Heal:
                    SetCommand(Stage.Pool.GetCommandFromPool(command));
                    break;
                default:
                    Debug.LogError($"Invalid command {command} issued to user squad");
                    return;
            }

            GetCommand().Setup(this, false, enemy, null);
        }

        public void FinalizeUserCommand()
        {
            // User override owns the squad now. Disable/refund the scripted queue before
            // finalizing the active command so its normal finalizer cannot advance the queue.
            if (HasCommandQueue || CommandQueue.Count > 0)
            {
                CancelScriptedCommandQueue();
            }

            Command currentCommand = GetCommand();
            if (!HasCommand || currentCommand == null)
            {
                SetCommandNull();
                return;
            }

            if (currentCommand.CommandType == ConfigData.CommandTypes.Guard)
            {
                UnmatchSpeed();
                ((Guard)currentCommand).GetGuardingSquads().ForEach(squad =>
                {
                    if (squad?.GetCommand() is Guard guardCommand)
                    {
                        guardCommand.OtherGuardSquads.Remove(this);
                    }
                });
            }
            currentCommand.SetFinalize("New command given");
        }

        public MiningAsteroid GetNearestMiningAsteroid()
        {
            return Level.State.MiningAsteroids
                .Where(asteroid => asteroid != null && !asteroid.IsDead)
                .OrderBy(asteroid => DistanceToPoint(asteroid.GetPosition()))
                .FirstOrDefault();
        }
    }
}