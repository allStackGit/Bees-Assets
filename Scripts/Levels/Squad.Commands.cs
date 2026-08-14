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
            if (!Stage.IsTraining)
            {
                Debug.Log($"Setting {this} Command to {command}");
            }
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
            if (!Stage.IsTraining)
            {
                Debug.Log($"Adding {this} to squads awaiting hive mind commands");
            }
            Level.State.AddToSquadsAwaitingHiveMindCommands(this);
        }

        public bool IsInBounds()
        {
            if (!_isInBounds)
            {
                List<Ship> ships = GetShips();
                _isInBounds = true;
                for (int i = 0; i < ships.Count; i++)
                {
                    if (!ships[i].IsInBounds())
                    {
                        _isInBounds = false;
                        break;
                    }
                }
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

        private readonly HashSet<ConfigData.ShipTypes> _banned = new HashSet<ConfigData.ShipTypes>();
        private readonly HashSet<ConfigData.ShipTypes> _enemyShips = new HashSet<ConfigData.ShipTypes>();
        private string[] _bannedTypes;
        private readonly List<Ship> _matchupShips = new List<Ship>(64);
        private readonly char[] _matchupLetters = new char[64];

        public List<Ship> GetShipsForMatchup()
        {
            _matchupShips.Clear();
            List<Ship> ships = GetShips();
            int count = Math.Min(64, ships.Count);
            for (int i = 0; i < count; i++)
            {
                _matchupShips.Add(ships[i]);
            }
            return _matchupShips;
        }

        public void MakeMatchupStrat()
        {
            _enemyShips.Clear();
            int enemySide = Side == ConfigData.Configuration.BeeSide
                ? ConfigData.Configuration.HumanSide
                : ConfigData.Configuration.BeeSide;
            for (int i = 0; i < Level.State.Ships.Count; i++)
            {
                Ship ship = Level.State.Ships[i];
                if (ship.Side == enemySide)
                {
                    _enemyShips.Add(ship.ShipType);
                }
            }

            _banned.Clear();
            foreach (ConfigData.ShipTypes type in ConfigData.UserProgressData.AllShipTypes)
            {
                if (!_enemyShips.Contains(type))
                {
                    _banned.Add(type);
                }
            }

            _bannedTypes = new string[_banned.Count];
            int bannedTypeIndex = 0;
            foreach (ConfigData.ShipTypes shipType in _banned)
            {
                _bannedTypes[bannedTypeIndex++] = $"Type {Utilities.ConvertShipTypeToCharacter[shipType]}";
            }
            ConfigData.Socket.SendRequest(new MatchupStrategyRequest(
                new GetMatchupStrategy(AddToMatchup(GetShipsForMatchup()), OpponentId, _bannedTypes),
                this,
                Level,
                ConfigData.StandardMaxTimeOnQueue));
        }

        public string AddToMatchup(List<Ship> ships)
        {
            int count = Math.Min(ships.Count, _matchupLetters.Length);
            for (int i = 0; i < count; i++)
            {
                _matchupLetters[i] = Utilities.ConvertShipTypeLetterToCharacter[ships[i].ShipTypeLetter];
            }
            Array.Sort(_matchupLetters, 0, count);
            return new string(_matchupLetters, 0, count);
        }

        private string _matchup;
        private readonly StringBuilder _sb = new StringBuilder();
        private readonly HashSet<ConfigData.CommandTypes> _bannedStrats = new HashSet<ConfigData.CommandTypes>();
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

            double total = 0d;
            for (int i = 0; i < ships.Count; i++)
            {
                Ship ship = ships[i];
                if (ship.OriginalHealth > 0)
                {
                    total += ((double)ship.Health / ship.OriginalHealth) * 100d;
                }
            }
            return total / ships.Count;
        }

        public void MakeMatchupAndGetCommand(Squad enemy = null)
        {
            _bannedStrats.Clear();
            foreach (ConfigData.CommandTypes bannedStrat in BannedStrats)
            {
                _bannedStrats.Add(bannedStrat);
            }

            List<Ship> actingShips = GetShipsForMatchup();
            _sb.Clear();
            _sb.Append(AddToMatchup(actingShips));

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
                _matchupFriendlyHealthShips.AddRange(actingShips);
                int allyHealthLimit = Math.Max(0, 64 - _matchupFriendlyHealthShips.Count);
                for (int i = 0; i < _matchupAllies.Count && i < allyHealthLimit; i++)
                {
                    _matchupFriendlyHealthShips.Add(_matchupAllies[i]);
                }
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
            _closestFriendlySquadCount = 0;
            _friendlySquadCount = 0;
            for (int i = 0; i < Level.State.Squads.Count; i++)
            {
                Squad friendly = Level.State.Squads[i];
                if (friendly.Side != Side || friendly.IsDead)
                {
                    continue;
                }
                _friendlySquadCount++;
                if (friendly.GetCommand()?.CommandType == ConfigData.CommandTypes.ClosestFriendly)
                {
                    _closestFriendlySquadCount++;
                }
            }
            if (_friendlySquadCount - 1 <= _closestFriendlySquadCount)
            {
                _bannedStrats.Add(ConfigData.CommandTypes.ClosestFriendly);
            }

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
            else
            {
                bool hasHealingCapacity = false;
                for (int i = 0; i < Level.State.Ships.Count; i++)
                {
                    Ship ship = Level.State.Ships[i];
                    if (ship.Side == ConfigData.Configuration.BeeSide && ship.IsBeehive &&
                        ((Beehive)ship).ShipsHealingHere.Count < 4)
                    {
                        hasHealingCapacity = true;
                        break;
                    }
                }
                if (!hasHealingCapacity)
                {
                    _bannedStrats.Add(ConfigData.CommandTypes.Heal);
                }
            }

            string[] bannedStratNames = new string[_bannedStrats.Count];
            int bannedStratIndex = 0;
            foreach (ConfigData.CommandTypes bannedStrat in _bannedStrats)
            {
                bannedStratNames[bannedStratIndex++] = Utilities.ConvertCommandTypeToName[bannedStrat];
            }
            ConfigData.Socket.SendRequest(new CommandRequest(
                new GetStrategy(_matchup, OpponentId, bannedStratNames),
                this,
                enemy,
                Level,
                _matchup,
                ConfigData.StandardMaxTimeOnQueue));
        }

        public void ClearTargets()
        {
            List<Ship> ships = GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                ships[i].ClearTargets();
            }
        }

        public void SetShootingStrategy(ConfigData.ShootingStrategyTypes strategy)
        {
            _chosenShootingStrategy = strategy;
            if (HasCommand && GetCommand() != null && GetCommand().HasShootingStrategy)
            {
                GetCommand().ShootingStrategy.ShootingStrategyType = strategy;
            }
            List<Ship> ships = GetShips();
            for (int i = 0; i < ships.Count; i++)
            {
                ships[i].ShootingStrategy = _chosenShootingStrategy;
            }
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
            TargetingSquadMarkerPool markerPool = TargetingSquadMarkerPool.GetOrCreate(Stage);
            List<Ship> enemyShips = enemy.GetShips();
            for (int i = 0; i < enemyShips.Count; i++)
            {
                markerPool.Show(enemyShips[i]);
            }
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
            MiningAsteroid closest = null;
            float closestDistance = float.MaxValue;
            foreach (MiningAsteroid asteroid in Level.State.MiningAsteroids)
            {
                if (asteroid == null || asteroid.IsDead)
                {
                    continue;
                }
                float distance = DistanceToPoint(asteroid.GetPosition());
                if (closest == null || distance < closestDistance)
                {
                    closest = asteroid;
                    closestDistance = distance;
                }
            }
            return closest;
        }
    }
}