
using System.Collections;
using System.Security.Cryptography;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    public class ClosestFriendly : Command
    {
        /*
        Sends the squad to go to the nearest friendly squad. Once they're close to the squad they 
        just follow that squad for a period before finalizing the strategy.
        */
        private Squad _closestFriendlySquad;
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy)
        {
            base.Execute(ConfigData.CommandTypes.ClosestFriendly, shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, noEnemy);


            //_parameters.setTimer = false;
            _closestFriendlySquad = Squad.GetClosestValidFriendlySquad();
            InvokeRepeating(nameof(Timer), CommandFrequency, CommandFrequency);
            Invoke(nameof(FinishFollowing), ConfigData.Configuration.AISquadFollowingTime);


        }
        public override void ClearData()
        {
            base.ClearData();
            _closestFriendlySquad = null;
        }
        private void Timer()
        {
            if (!Squad.IsDead)
            {
                if (_closestFriendlySquad != null && !_closestFriendlySquad.IsDead)
                {
                    //Debug.Log($"_closestFriendlySquad: {_closestFriendlySquad.Name} IsDead: {_closestFriendlySquad.IsDead}");
                    Vector2 position = _closestFriendlySquad.GetPosition();
                    if (Squad.HasReachedDestination)
                    {
                        Squad.Status = $"Trying to catch up to friendly squad #{_closestFriendlySquad.SquadNumber}";
                        SetAndMove(position);
                    }
                    else
                    {
                        Squad.Status = $"Following friendly squad #{_closestFriendlySquad.SquadNumber}";
                        SetAndMove(position);
                        
                    }

                }
                else
                {
                    CancelInvoke(nameof(Timer));
                    SetFinalize("The friendly squad to follow is gone or dead");
                }
            }
            
        }
        private void FinishFollowing()
        {
            SetFinalize("Finished following friendly squad");
        }
    }
}