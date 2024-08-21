
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    public class MoveToPoint : Command
    {
        /*
        Sends the squad towards a random spot on the map. Currently unused.
         */

        public void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy, Vector2 destination)
        {
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);

            if (Squad != null)
            {
                PrepareDamageToSendEntries("closest");
                SetAndMove(destination);
                InvokeRepeating(nameof(Timer), CommandFrequency, CommandFrequency);
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
                if (Squad.HasReachedDestination)
                {
                    CancelInvoke(nameof(Timer));
                    SetFinalize("Reached the specified destination on the map");
                }
                Vector2 destination = GetDestination();
                SetAndMove(destination);
                Squad.Status = $"Moving to specified destination: {destination}";

            }
            else
            {
                CancelInvoke(nameof(Timer));
                SetFinalize("The squad is dead");
            }

        }
    }
}