
using System.Collections;
using System.Security.Cryptography;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    /*
    Sends the squad straight towards the target until it gets within range of the squad and then retreats a distance away.
    */
    public class InAndOut : Command
    {
        private Vector2 _returnPoint;
        private bool _hasReachedDestination;
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);
            if (Squad != null)
            {
                if (Enemy != null)
                {
                    IsAttacking = true;

                    PrepareDamageToSendEntries();
                    float distance = Squad.DistanceTo(Enemy.GetPosition());
                    Vector2 position = Squad.GetPosition();
                    _returnPoint = distance > Enemy.Range && distance < 50 ?
                        Utilities.RandomCoordinate(Level, position, Vector2.one * 10, Vector2.zero) :
                        Utilities.RandomCoordinate(Level, Enemy.GetPosition(), Vector2.one * (Enemy.Range + 20), Vector2.one * Enemy.Range);

                    _hasReachedDestination = false;
                    InvokeRepeating(nameof(Timer), .1f, .1f);
                }
            }
            else
            {
                SetFinalize("The squad is dead");
            }
            

        }
        private void Timer()
        {
            if (Squad != null)
            {
                if (Enemy != null)
                {

                    if (!_hasReachedDestination && !Squad.AreAllSquadShipsWithinRangeOfAllOfOurSquadShips(Enemy))
                    {
                        Squad.Status = $"Targeting enemy squad #{Enemy.SquadNumber} for In and Out";
                        //Debug.Log($"Enemy: {Enemy.Name} IsDead: {Enemy.IsDead}");
                        SetAndMove(Enemy.GetPosition());
                    }
                    else if (!Squad.HasReachedDestination)
                    {
                        Squad.IsRetreating = true;
                        Squad.Status = $"Retreating away from enemy squad #{Enemy.SquadNumber} for In and Out";
                        _hasReachedDestination = true;
                        SetAndMove(_returnPoint);
                        _returnPoint = GetDestination();
                    }
                    else
                    {
                        CancelInvoke(nameof(Timer));
                        SetFinalize("Returned to starting point");
                    }
                }
                else
                {
                    CancelInvoke(nameof(Timer));
                    SetFinalize("The enemy squad is gone or dead");
                }
            }
            else
            {
                CancelInvoke(nameof(Timer));
                SetFinalize("The squad is dead");
            }
            
        }
    }
}

