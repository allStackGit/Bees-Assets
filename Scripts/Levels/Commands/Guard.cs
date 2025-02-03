
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
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy, Squad guardedSquad)
        {
            base.Execute(ConfigData.CommandTypes.Guard, shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, noEnemy);
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
                Level.State.GetSquadsBySide(Squad.Side).ForEach((guardingSquad) =>
                {
                    // check if it's a guarding squad and guarding the same squad as this squad
                    if (guardingSquad != Squad && guardingSquad.HasCommand && guardingSquad.Command.HasStrategy &&
                    guardingSquad.Command.Strategy.CommandType == ConfigData.CommandTypes.Guard && ((Guard)guardingSquad.Command)._guardedSquad == _guardedSquad)
                    {
                        ((Guard)guardingSquad.Command).OtherGuardSquads.Add(Squad);
                        OtherGuardSquads.Add(guardingSquad);
                    }
                });
                GuardPosition = GetGuardingSquads().Count % 4;
                //Debug.Log($"{Squad.Name} is guarding {_guardedSquad.Name} at position #{GuardPosition}");
                Squad.Status = $"Guarding {_guardedSquad.Name}";
                if (IsHiveMindCommand)
                {
                    Invoke(nameof(FinishGuardingCommand), ConfigData.Configuration.AISquadGuardTime);

                }
                InvokeRepeating(nameof(Timer), 0, CommandFrequency);
            }
            else
            {
                Squad.BannedStrats.Add(Strategy.CommandType);
                SetFinalize("There are no squads to guard");
            }


        }
        public override void ClearData()
        {
            base.ClearData();
            _guardedSquad = null;
            OtherGuardSquads.Clear();
        }
        private void Timer()
        {
            // determine initial destination based on other guarding squads
            if (!Squad.IsDead)
            {
                if (!_guardedSquad.IsDead)
                {
                    Vector2 position = _guardedSquad.GetCenterPoint();
                    int offset = 4;
                    Vector2 offsetFromSquad = new Vector2(Squad.GetWidth() + offset, Squad.GetHeight() + offset);


                    switch (GuardPosition)
                    {
                        case 0:
                            position.y += offsetFromSquad.y;
                            break;
                        case 1:
                            position.x -= offsetFromSquad.x;
                            break;
                        case 2:
                            position.x += offsetFromSquad.x;
                            break;
                        case 3:
                            position.y -= offsetFromSquad.y;
                            break;

                    }
                    //Debug.Log($"There are {OtherGuardSquads.Count} other squads guarding {GuardedSquad.Name}, so {Squad.Name} is going to {position}");
                    // set the destination
                    SetAndMove(position);
                    if (Vector2.Distance(position, Squad.GetPosition()) < ConfigData.CloseEnoughCoordinateVariance)
                    {
                        Squad.SetSquadSpeed(_guardedSquad.SlowestSpeed);
                    }

                }
                else
                {
                    Squad.SetSquadSpeed(Squad.MaxSpeed);
                    CancelInvoke(nameof(Timer));
                    SetFinalize("Guarded squad died");
                }
            }
            
        }
        private Squad GetClosestAvailableSquadToGuard()
        {
            return Level.State.GetSquadsBySide(Side)
                .Where((s) => s != Squad && (!s.HasCommand || !s.Command.HasStrategy || s.Command.Strategy.CommandType != ConfigData.CommandTypes.Guard))
                .OrderBy(s => s.DistanceToPoint(Squad.GetPosition())).FirstOrDefault();
        }
        public List<Squad> GetGuardingSquads()
        {
            if (_guardedSquad != null && OtherGuardSquads.Count > 0)
            {
                List<Squad> otherGuardSquads = OtherGuardSquads.Where(
                (squad) => squad.HasCommand && squad.Command.HasStrategy && squad != Squad
                && squad.Command.Strategy.CommandType == ConfigData.CommandTypes.Guard).ToList();

                if (otherGuardSquads.Count > 0)
                {
                    return otherGuardSquads.Where((squad) => ((Guard)squad.Command)._guardedSquad != null
                    && ((Guard)squad.Command)._guardedSquad == _guardedSquad).ToList();
                }
            }
            return new List<Squad>();
            
        }

        private void FinishGuardingCommand()
        {
            if (!Squad.IsDead)
            {
                Squad.SetSquadSpeed(Squad.MaxSpeed);
                GetGuardingSquads().ForEach((squad) =>
                {
                    ((Guard)squad.Command).OtherGuardSquads.Remove(Squad);
                }); // [alert] need to do this when the user finishes too
                CancelInvoke(nameof(Timer));
                SetFinalize("Finished Guarding");

            }


        }
    }
}