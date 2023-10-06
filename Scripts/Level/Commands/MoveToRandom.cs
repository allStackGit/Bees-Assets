
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class MoveToRandom : Command
    {
        /*
        Sends the squad towards a random spot on the map
         */
        
        public override void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);

            if (Squad != null && !Squad.IsDead)
            {
                PrepareDamageToSendEntries("closest");
                Vector2 position = Squad.GetPosition();
                Vector2 randomCoordinates = Utilities.RandomCoordinate(Level, position, Vector2.one * ConfigData.Configuration.AIRandomMovementMaxDistance, Vector2.one * 10);
                SetAndMove(randomCoordinates);
                InvokeRepeating(nameof(Timer), .1f, .1f);
            }
            else
            {
                SetFinalize("The squad is dead");
            }


        }
        private void Timer()
        {
            if(Squad != null && !Squad.IsDead)
            {
                if (Squad.HasReachedDestination)
                {
                    CancelInvoke(nameof(Timer));
                    SetFinalize("Reached the random destination on the map");
                }
                Vector2 destination = GetDestination();
                SetAndMove(destination);
                Squad.Status = $"Moving to random destination: {destination}";
                
            }
            else
            {
                CancelInvoke(nameof(Timer));
                SetFinalize("The squad is dead");
            }

        }
    }
}