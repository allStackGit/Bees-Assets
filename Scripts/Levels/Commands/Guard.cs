
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class Guard : Command
    {
        /* The guarding squad(s) moves at the speed (if it's fast enough) of the guarded squad (the squad that's to be guarded). 
         * The squad(s) take up position in order of N, W, E, S points from the guarded squad, checking the positions of the other squad(s) and positioning accordingly
         * A timer will check the position of the guarded squad and tell the guarding squad(s) to move accordingly. 
         * If the Squad is an AI squad, a timer will stop the command 
         */
        private Squad _guardedSquad;
        public List<Squad> OtherGuardSquads = new List<Squad>();
        /// <summary>
        /// The position of the squad as either, 0, 1, 2, or 3. Corresponds to the cardinal directions to determine where the squad should be
        /// </summary>
        public int GuardPosition;
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, Squad guardedSquad)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);
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
                // add this squad to the list for all other guard squads
                Level.State.GetSquadsBySide(GetSquad().Side).ForEach((guardingSquad) =>
                {
                    // check if it's a guarding squad and guarding the same squad as this squad
                    if (guardingSquad != GetSquad() && guardingSquad.HasCommand &&
                    guardingSquad.GetCommand().CommandType == ConfigData.CommandTypes.Guard && ((Guard)guardingSquad.GetCommand())._guardedSquad == _guardedSquad)
                    {
                        ((Guard)guardingSquad.GetCommand()).OtherGuardSquads.Add(GetSquad());
                        OtherGuardSquads.Add(guardingSquad);
                    }
                });
                GuardPosition = GetGuardingSquads().Count % 4;
                //Debug.Log($"{Squad.Name} is guarding {_guardedSquad.Name} at position #{GuardPosition}");
                GetSquad().Status = $"Guarding {_guardedSquad.Name}";

                CommandTimer.Reuse(CommandFrequency, Timer, true);
                Level.AddTimer(CommandTimer);
                if (IsHiveMindCommand)
                {
                    TimeoutTimer.Reuse(ConfigData.Configuration.AISquadGuardTime, FinishGuardingCommand);
                    Level.AddTimer(TimeoutTimer);
                    //Invoke(nameof(FinishGuardingCommand), ConfigData.Configuration.AISquadGuardTime);

                }
                //InvokeRepeating(nameof(Timer), 0, CommandFrequency);
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
        }
        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for Timer() method:
        //////////////////////////////////////////////////////////////////////////////

        private Vector2 _timer_position;
        private Vector2 _timer_offsetFromSquad;
        private int _timer_offset = 4;

        private void Timer()
        {
            // Determine initial destination based on other guarding squads
            if (!IsDead)
            {
                if (!_guardedSquad.IsDead)
                {
                    _timer_position = _guardedSquad.GetCenterPoint();
                    _timer_offsetFromSquad = new Vector2(GetSquad().GetWidth() + _timer_offset, GetSquad().GetHeight() + _timer_offset);

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

                    // Set the destination
                    SetAndMove(_timer_position);
                    if (Vector2.Distance(_timer_position, GetSquad().GetPosition()) < ConfigData.CloseEnoughCoordinateVariance)
                    {
                        GetSquad().SetSquadSpeed(_guardedSquad.SlowestSpeed);
                    }
                }
                else
                {
                    GetSquad().SetSquadSpeed(GetSquad().MaxSpeed);
                    //CancelInvoke(nameof(Timer));
                    SetFinalize("Guarded squad died");
                }
            }
        }
        private Squad GetClosestAvailableSquadToGuard()
        {
            return Level.State.GetSquadsBySide(Side)
                .Where((s) => s != GetSquad() && (!s.HasCommand || s.GetCommand().CommandType != ConfigData.CommandTypes.Guard))
                .OrderBy(s => s.DistanceToPoint(GetSquad().GetPosition())).FirstOrDefault();
        }
        private List<Squad> _f_otherGuardSquads = new List<Squad>();
        public List<Squad> GetGuardingSquads()
        {
            if (_guardedSquad != null && OtherGuardSquads.Count > 0)
            {
                _f_otherGuardSquads = OtherGuardSquads.Where(
                (squad) => squad.HasCommand && squad != GetSquad()
                && squad.GetCommand().CommandType == ConfigData.CommandTypes.Guard).ToList();

                if (_f_otherGuardSquads.Count > 0)
                {
                    return _f_otherGuardSquads.Where((squad) => ((Guard)squad.GetCommand())._guardedSquad != null
                    && ((Guard)squad.GetCommand())._guardedSquad == _guardedSquad).ToList();
                }
            }
            _f_otherGuardSquads.Clear();
            return _f_otherGuardSquads;
            
        }

        private void FinishGuardingCommand()
        {
            if (!GetSquad().IsDead)
            {
                GetSquad().SetSquadSpeed(GetSquad().MaxSpeed);
                GetGuardingSquads().ForEach((squad) =>
                {
                    ((Guard)squad.GetCommand()).OtherGuardSquads.Remove(GetSquad());
                }); // [alert] need to do this when the user finishes too
                //CancelInvoke(nameof(Timer));
                SetFinalize("Finished Guarding");

            }


        }
    }
}