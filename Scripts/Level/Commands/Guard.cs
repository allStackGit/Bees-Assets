
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
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
        public void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy, Squad guardedSquad)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);
            if (Squad != null)
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
                    //Debug.Log($"{Squad.Name} is guarding {GuardedSquad.Name}!");
                    // add this squad to the list for all other guard squads
                    Level.GetState().GetSquadsBySide(Squad.Side).ForEach((guardingSquad) =>
                    {
                        // check if it's a guarding squad and guarding the same squad as this squad
                        if (!guardingSquad.Equals(Squad) && guardingSquad.HasCommand && guardingSquad.Command.HasStrategy &&
                        guardingSquad.Command.Strategy.Name == "Guard" && ((Guard)guardingSquad.Command)._guardedSquad.Equals(_guardedSquad))
                        {
                            ((Guard)guardingSquad.Command).OtherGuardSquads.Add(Squad);
                            OtherGuardSquads.Add(guardingSquad);
                        }
                    });
                    Squad.Status = $"Guarding {_guardedSquad.Name}";
                    if (IsHiveMindCommand)
                    {
                        Invoke(nameof(FinishGuardingCommand), ConfigData.Configuration.AISquadGuardTime);

                    }
                    InvokeRepeating(nameof(Timer), .1f, .1f);
                }
                else
                {
                    Squad.BannedStrats.Add(Strategy.Name);
                    SetFinalize("There are no squads to guard");
                }
            }
            else
            {
                SetFinalize("The squad is dead");
            }
           

        }
        private void Timer()
        {
            // determine initial destination based on other guarding squads
            if (!Squad.IsDead)
            {
                if (_guardedSquad != null && !_guardedSquad.IsDead)
                {
                    Vector2 position = _guardedSquad.GetCenterPoint();
                    int offset = 4;
                    Vector2 offsetFromSquad = new Vector2(Squad.GetWidth() + offset, Squad.GetHeight() + offset);


                    switch (GetGuardingSquads().Count % 4)
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
            return Level.GetState().GetSquadsBySide(Side)
                .Where((s) => !s.Equals(Squad) && (!s.HasCommand || !s.Command.HasStrategy || s.Command.Strategy.Name != "Guard"))
                .OrderBy(s => s.DistanceTo(Squad.GetPosition())).ToList().FirstOrDefault();
        }
        public List<Squad> GetGuardingSquads()
        {
            if (_guardedSquad != null && OtherGuardSquads.Count > 0)
            {
                List<Squad> otherGuardSquads = OtherGuardSquads.Where(
                (squad) => squad.HasCommand && squad.Command.HasStrategy && !squad.Equals(Squad)
                && squad.Command.Strategy.Name == "Guard").ToList();

                if (otherGuardSquads.Count > 0)
                {
                    return otherGuardSquads.Where((squad) => ((Guard)squad.Command)._guardedSquad != null
                    && ((Guard)squad.Command)._guardedSquad.Equals(_guardedSquad)).ToList();
                }
            }
            return new List<Squad>();
            
        }

        private void FinishGuardingCommand()
        {
            if (Squad != null)
            {
                Squad.SetSquadSpeed(Squad.MaxSpeed);
                GetGuardingSquads().ForEach((squad) =>
                {
                    ((Guard)squad.Command).OtherGuardSquads.Remove(Squad);
                }); // [alert] need to do this when the user finishes too
               
            }
            CancelInvoke(nameof(Timer));
            SetFinalize("Finished Guarding");

        }
    }
}