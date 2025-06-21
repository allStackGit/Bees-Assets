
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
        Vector2 _position, _enemyPosition;
        float _distance;
        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId)
        {
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, false);
            if (!EnemySquad.IsDead)
            {
                IsAttacking = true;

                PrepareDamageToSendEntries();
                _position = GetSquad().GetPosition();
                _enemyPosition = EnemySquad.GetPosition();
                _distance = GetSquad().DistanceToPoint(_enemyPosition);
                ReturnPoint = _distance > EnemySquad.MaxRange && _distance < 50 ?
                    Utilities.RandomCoordinate(Level, _position, Vector2.one * 45, Vector2.zero) :
                    Utilities.RandomCoordinate(Level, _enemyPosition, Vector2.one * (EnemySquad.MaxRange + 45), Vector2.one * (EnemySquad.MaxRange + 10));

                CommandTimer.Reuse(CommandFrequency, Timer, true, true);
                Level.AddTimer(CommandTimer);

                //InvokeRepeating(nameof(Timer), 0, CommandFrequency);
                if (IsHiveMindCommand)
                {
                    TimeoutTimer.Reuse(ConfigData.StandardMaxCommandTime, Timeout);
                    Level.AddTimer(TimeoutTimer);
                    //Invoke(nameof(Timeout), ConfigData.StandardMaxCommandTime);
                }

            }
            else
            {
                SetFinalize("The enemy squad is gone or dead");
            }

        }
        public override void ClearData()
        {
            base.ClearData();
            ReturnPoint = Vector2.zero;
            HasReachedReturnPoint = false;
            HasReachedEnemySquad = false;
        }
        private void Timer()
        {
            if (!GetSquad().IsDead)
            {
                if (!EnemySquad.IsDead)
                {

                    // if you haven't reached the enemy squad yet, check if you are within range and go to them
                    if (!HasReachedEnemySquad)
                    {
                        if (!GetSquad().AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips(EnemySquad))
                        {
                            GetSquad().Status = $"Targeting enemy squad #{EnemySquad.SquadNumber} for In and Out";
                            MoveTowardsEnemies();
                        }
                        else // if you have reached the enemy squad, retreat to the return point
                        {
                            HasReachedEnemySquad = true;

                            //Squad.IsRetreating = true;
                            GetSquad().Status = $"Retreating away from enemy squad #{EnemySquad.SquadNumber} for In and Out";
                            HasReachedReturnPoint = false;
                            SetAndMove(ReturnPoint);
                            ReturnPoint = GetDestination();
                        }
                    }
                    else if (GetSquad().HasReachedDestination) // if you have hit the return point, end the command
                    {
                        //CancelInvoke(nameof(Timer));
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
                    //CancelInvoke(nameof(Timer));
                    SetFinalize("The enemy squad is gone or dead");
                }
            }
            
        }
    }
}

