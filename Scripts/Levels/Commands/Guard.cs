
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Guard : Command
    {
        private Squad _guardedSquad;
        public List<Squad> OtherGuardSquads = new List<Squad>();
        public int GuardPosition;

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, Squad guardedSquad)
        {
            if (IsHiveMindCommand)
            {
                _guardedSquad = GetClosestAvailableSquadToGuard();
            }
            else
            {
                _guardedSquad = guardedSquad;
            }
            if (_guardedSquad != null)
            {
                base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);

                List<Squad> squads = Level.State.Squads;
                for (int i = 0; i < squads.Count; i++)
                {
                    Squad guardingSquad = squads[i];
                    if (guardingSquad.Side != GetSquad().Side || guardingSquad.IsDead ||
                        guardingSquad == GetSquad() || !guardingSquad.HasCommand)
                    {
                        continue;
                    }
                    if (guardingSquad.GetCommand()?.CommandType == ConfigData.CommandTypes.Guard &&
                        ((Guard)guardingSquad.GetCommand())._guardedSquad == _guardedSquad)
                    {
                        ((Guard)guardingSquad.GetCommand()).OtherGuardSquads.Add(GetSquad());
                        OtherGuardSquads.Add(guardingSquad);
                    }
                }

                GuardPosition = GetGuardingSquads().Count % 4;
                if (!Stage.IsTraining)
                {
                    GetSquad().Status = $"Guarding {_guardedSquad.Name}";
                }

                CommandTimer.Reuse(CommandFrequency, Timer, true, true);
                Level.AddTimer(CommandTimer);
                if (IsHiveMindCommand)
                {
                    TimeoutTimer.Reuse(ConfigData.Configuration.AISquadGuardTime, FinishGuardingCommand);
                    Level.AddTimer(TimeoutTimer);
                }
            }
            else
            {
                GetSquad().BannedStrats.Add(CommandType);
                SetFinalize("There are no squads to guard");
            }
        }

        public override void ClearData()
        {
            base.ClearData();
            _guardedSquad = null;
            OtherGuardSquads.Clear();
            _f_otherGuardSquads.Clear();
        }

        private Vector2 _timer_position;
        private Vector2 _timer_offsetFromSquad;
        private int _timer_offset = 4;

        private void Timer()
        {
            if (!IsDead)
            {
                if (!_guardedSquad.IsDead)
                {
                    _timer_position = _guardedSquad.GetCenterPoint();
                    try
                    {
                        _timer_offsetFromSquad = new Vector2(GetSquad().GetWidth() + _timer_offset, GetSquad().GetHeight() + _timer_offset);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Squad: {GetSquad()}, Command: {this}, Squad Ships: {Utilities.ListToString(GetSquad().GetShips())}");
                        throw e;
                    }

                    switch (GuardPosition)
                    {
                        case 0:
                            _timer_position.y += _timer_offsetFromSquad.y;
                            break;
                        case 1:
                            _timer_position.x -= _timer_offsetFromSquad.x;
                            break;
                        case 2:
                            _timer_position.x += _timer_offsetFromSquad.x;
                            break;
                        case 3:
                            _timer_position.y -= _timer_offsetFromSquad.y;
                            break;
                    }

                    SetDestination(_timer_position);
                    GetSquad().MoveTracked(GetDestination());
                    if (Vector2.Distance(_timer_position, GetSquad().GetPosition()) < ConfigData.CloseEnoughCoordinateVariance)
                    {
                        GetSquad().SetSquadSpeed(_guardedSquad.GetSlowestShipSpeed());
                    }
                }
                else
                {
                    try
                    {
                        GetSquad().SetSquadSpeed(GetSquad().MaxSpeed);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Squad: {GetSquad()}, Command: {this}, Squad Ships: {Utilities.ListToString(GetSquad().GetShips())}");
                        GetSquad().SetSquadSpeed(GetSquad().MaxSpeed);
                        throw e;
                    }
                    SetFinalize("Guarded squad died");
                }
            }
        }

        private Squad GetClosestAvailableSquadToGuard()
        {
            Squad closest = null;
            float closestDistance = float.MaxValue;
            Vector2 origin = GetSquad().GetPosition();
            List<Squad> squads = Level.State.Squads;
            for (int i = 0; i < squads.Count; i++)
            {
                Squad candidate = squads[i];
                if (candidate.Side != Side || candidate.IsDead || candidate == GetSquad() ||
                    (candidate.HasCommand && candidate.GetCommand()?.CommandType == ConfigData.CommandTypes.Guard))
                {
                    continue;
                }

                float distance = candidate.DistanceToPoint(origin);
                if (closest == null || distance < closestDistance)
                {
                    closest = candidate;
                    closestDistance = distance;
                }
            }
            return closest;
        }

        private readonly List<Squad> _f_otherGuardSquads = new List<Squad>();
        public List<Squad> GetGuardingSquads()
        {
            _f_otherGuardSquads.Clear();
            if (_guardedSquad == null || OtherGuardSquads.Count == 0)
            {
                return _f_otherGuardSquads;
            }

            for (int i = 0; i < OtherGuardSquads.Count; i++)
            {
                Squad squad = OtherGuardSquads[i];
                if (squad == null || squad == GetSquad() || !squad.HasCommand)
                {
                    continue;
                }
                if (squad.GetCommand() is Guard guard && guard._guardedSquad == _guardedSquad)
                {
                    _f_otherGuardSquads.Add(squad);
                }
            }
            return _f_otherGuardSquads;
        }

        public override void SetFinalize(string cause)
        {
            if (!IsDead)
            {
                if (GetSquad() != null && !GetSquad().IsDead)
                {
                    GetSquad().SetSquadSpeed(GetSquad().MaxSpeed);
                }

                List<Squad> guardingSquads = GetGuardingSquads();
                for (int i = 0; i < guardingSquads.Count; i++)
                {
                    Squad guardSquad = guardingSquads[i];
                    if (guardSquad != null && guardSquad.HasCommand && guardSquad.GetCommand() is Guard guardCommand)
                    {
                        guardCommand.OtherGuardSquads.Remove(GetSquad());
                    }
                }
            }

            base.SetFinalize(cause);
        }

        private void FinishGuardingCommand()
        {
            if (!GetSquad().IsDead)
            {
                SetFinalize("Finished Guarding");
            }
        }
    }
}