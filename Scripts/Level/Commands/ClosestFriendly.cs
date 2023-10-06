
using System.Collections;
using System.Security.Cryptography;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class ClosestFriendly : Command
    {
        /*
        Sends the squad to go to the nearest friendly squad. Once they're close to the squad they 
        just follow that squad for a period before finalizing the strategy.
        */
        private Squad _closestFriendlySquad;
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);

            
            if (Squad != null && !Squad.IsDead)
            {
                //_parameters.setTimer = false;
                _closestFriendlySquad = Squad.GetClosestValidFriendlySquad();
                if (_closestFriendlySquad != null)
                {
                    InvokeRepeating(nameof(Timer), .1f, .1f);
                }
                else
                {
                    Squad.BannedStrats.Add(Strategy.Name);
                    SetFinalize("There are no friendly squads to follow");
                }
            }
            else
            {
                SetFinalize("The squad is dead");
            }


        }
        private void Timer()
        {
            if (Squad != null && !Squad.IsDead)
            {
                if (_closestFriendlySquad != null && !_closestFriendlySquad.IsDead)
                {
                    //Debugger.Log($"_closestFriendlySquad: {_closestFriendlySquad.Name} IsDead: {_closestFriendlySquad.IsDead}");
                    Vector2 position = _closestFriendlySquad.GetPosition();
                    if (Squad.HasReachedDestination)
                    {
                        Squad.Status = $"Trying to catch up to friendly squad #{_closestFriendlySquad.SquadNumber}";
                        SetAndMove(position);
                    }
                    else
                    {
                        Squad.Status = $"Following friendly squad #{_closestFriendlySquad.SquadNumber}";
                        SetAndMove(_closestFriendlySquad.GetPosition());
                        Invoke(nameof(FinishFollowing), ConfigData.Configuration.AISquadFollowingTime);
                    }

                }
                else
                {
                    CancelInvoke(nameof(Timer));
                    SetFinalize("The friendly squad to follow is gone or dead");
                }
            }
            else
            {
                CancelInvoke(nameof(Timer));
                SetFinalize("The squad is dead");
            }
            
        }
        private void FinishFollowing()
        {
            SetFinalize("Finished following friendly squad");
        }
    }
}