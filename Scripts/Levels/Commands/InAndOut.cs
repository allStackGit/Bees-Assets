
using System.Collections;
using System.Security.Cryptography;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    /*
    Sends the squad straight towards the target until it gets within range of the squad and then retreats a distance away.
    */
    public class InAndOut : Command
    {
        public Vector2 ReturnPoint;
        public bool HasReachedReturnPoint, HasReachedEnemySquad;
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);
            if (EnemySquad != null && !EnemySquad.IsDead)
            {
                IsAttacking = true;

                PrepareDamageToSendEntries();
                Vector2 position = Squad.GetPosition();
                Vector2 enemyPosition = EnemySquad.GetPosition();
                float distance = Squad.DistanceToPoint(enemyPosition);
                ReturnPoint = distance > EnemySquad.MaxRange && distance < 50 ?
                    Utilities.RandomCoordinate(Level, position, Vector2.one * 45, Vector2.zero) :
                    Utilities.RandomCoordinate(Level, enemyPosition, Vector2.one * (EnemySquad.MaxRange + 45), Vector2.one * (EnemySquad.MaxRange + 10));

                InvokeRepeating(nameof(Timer), 0, CommandFrequency);
                if (IsHiveMindCommand)
                {
                    Invoke(nameof(Timeout), ConfigData.StandardMaxCommandTime);
                }
            }

        }
        private void Timer()
        {
            if (!Squad.IsDead)
            {
                if (EnemySquad != null && !EnemySquad.IsDead)
                {

                    // if you haven't reached the enemy squad yet, check if you are within range and go to them
                    if (!HasReachedEnemySquad)
                    {
                        if (!Squad.AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad))
                        {
                            Squad.Status = $"Targeting enemy squad #{EnemySquad.SquadNumber} for In and Out";
                            MoveTowardsEnemies();
                        }
                        else // if you have reached the enemy squad, retreat to the return point
                        {
                            HasReachedEnemySquad = true;

                            //Squad.IsRetreating = true;
                            Squad.Status = $"Retreating away from enemy squad #{EnemySquad.SquadNumber} for In and Out";
                            HasReachedReturnPoint = false;
                            SetAndMove(ReturnPoint);
                            ReturnPoint = GetDestination();
                        }
                    }
                    else if (Squad.HasReachedDestination) // if you have hit the return point, end the command
                    {
                        CancelInvoke(nameof(Timer));
                        SetFinalize("Returned to starting point");
                    }

                    

                    // if you have hit the return point, end the command

                    //if (!HasReachedEnemySquad && Squad.AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad))
                    //{
                    //    HasReachedEnemySquad = true;
                    //}
                    //else if (HasReachedEnemySquad)
                    //{
                    //    Squad.IsRetreating = true;
                    //    Squad.Status = $"Retreating away from enemy squad #{EnemySquad.SquadNumber} for In and Out";
                    //    HasReachedReturnPoint = false;
                    //    SetAndMove(ReturnPoint);
                    //    ReturnPoint = GetDestination();
                    //}
                    //else
                    //{
                    //    Squad.Status = $"Targeting enemy squad #{EnemySquad.SquadNumber} for In and Out";
                    //    MoveTowardsEnemies();

                    //}
                    
                    //if (HasReachedEnemySquad && !HasReachedReturnPoint && Squad.HasReachedDestination)
                    //{
                    //    HasReachedReturnPoint = true;
                    //}
                    //else if (HasReachedReturnPoint)
                    //{
                    //    CancelInvoke(nameof(Timer));
                    //    SetFinalize("Returned to starting point");
                    //}

                    //if (!_hasReachedEnemySquad && !Squad.AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad))
                    //{
                    //    Squad.Status = $"Targeting enemy squad #{EnemySquad.SquadNumber} for In and Out";
                    //    //Debug.Log($"Enemy: {Enemy.Name} IsDead: {Enemy.IsDead}");
                    //    //SetAndMove(Enemy.GetPosition());
                    //    MoveTowardsEnemies();
                    //}
                    //else if ()
                    //else if (!Squad.HasReachedDestination)
                    //{
                    //    Squad.IsRetreating = true;
                    //    Squad.Status = $"Retreating away from enemy squad #{EnemySquad.SquadNumber} for In and Out";
                    //    _hasReachedDestination = true;
                    //    SetAndMove(_returnPoint);
                    //    _returnPoint = GetDestination();
                    //}
                    //else
                    //{
                    //    CancelInvoke(nameof(Timer));
                    //    SetFinalize("Returned to starting point");
                    //}
                }
                else
                {
                    CancelInvoke(nameof(Timer));
                    SetFinalize("The enemy squad is gone or dead");
                }
            }
            
        }
    }
}

