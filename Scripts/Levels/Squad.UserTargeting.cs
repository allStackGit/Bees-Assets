using Assets.Scripts.Levels.Commands;

namespace Assets.Scripts.Levels
{
    public partial class Squad
    {
        /// <summary>
        /// Dispatches a user right-click on an enemy squad to the attack behavior appropriate for
        /// this squad's composition. Barges are melee charge ships and cannot use the ordinary
        /// ranged Aggressive positioning loop: that loop eventually holds them at a firing
        /// position without ever invoking Barge.ChargeForward.
        /// </summary>
        public void UserTargetEnemy(Squad enemy)
        {
            if (!CanAcceptUserInput)
            {
                return;
            }

            if (GetShips().Count > 0 && HasOnlyBarges)
            {
                UserCharge(enemy);
                return;
            }

            UserAggressive(enemy);
        }

        private void UserCharge(Squad enemy)
        {
            if (enemy == null || enemy.IsDead)
            {
                return;
            }

            // Charge already has the full Barge pursuit/target-selection lifecycle, but the user
            // command dispatcher historically never exposed it. Set it up explicitly here rather
            // than routing Barges through Aggressive and leaving them stopped at a ranged hold.
            FinalizeUserCommand();
            SetCommand(Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge));
            GetCommand().Setup(this, false, enemy, null);
            ((Charge)GetCommand()).Execute(GetShootingStrategy(), Level.State.AddUserCommand(), 0);
            MarkTargets(enemy);
        }
    }
}
