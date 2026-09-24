using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        /// <summary>
        /// Rebuilds the Hive Mind command queue from the production controller router. This is
        /// intentionally separate from SetupHivemind so legacy scenes keep their serialized defaults,
        /// while mixed Hive Mind / neural-network ownership can coexist on the same Level.
        /// </summary>
        internal void ConfigureProductionControllerOwnership()
        {
            if (Stage == null || State == null || ConfigData.Configuration == null)
            {
                return;
            }

            State.ClearSquadsAwaitingHiveMindCommands();
            bool hasHiveMind = false;
            List<Squad> squads = State.GetAllSquads();
            for (int i = 0; i < squads.Count; i++)
            {
                Squad squad = squads[i];
                if (squad == null || squad.IsDead)
                {
                    continue;
                }

                global::RlProductionControllerRouter.ControllerKind controller =
                    global::RlProductionControllerRouter.Resolve(Stage, this, squad.Side);
                switch (controller)
                {
                    case global::RlProductionControllerRouter.ControllerKind.Player:
                        squad.IsUserControlled = true;
                        squad.IsHiveMindControlled = false;
                        squad.CanAcceptUserInput = true;
                        break;
                    case global::RlProductionControllerRouter.ControllerKind.HiveMind:
                        squad.IsUserControlled = false;
                        squad.IsHiveMindControlled = true;
                        squad.CanAcceptUserInput = false;
                        if (!squad.IsImmobile && !squad.HasCommandQueue)
                        {
                            squad.AddToCommandList();
                            hasHiveMind = true;
                        }
                        break;
                    case global::RlProductionControllerRouter.ControllerKind.NeuralNetwork:
                        squad.IsUserControlled = false;
                        squad.IsHiveMindControlled = false;
                        squad.CanAcceptUserInput = false;
                        break;
                    default:
                        squad.IsHiveMindControlled = false;
                        break;
                }
            }

            CancelTimer(_hivemindTimer);
            CancelTimer(_initialCommandDelayTimer);
            if (!hasHiveMind)
            {
                return;
            }

            // Hive Mind request/learning remains the existing authoritative path. Only the queue
            // membership changes: NN/player sides are never submitted for Hive commands.
            _hivemindTimer.Reuse(.25f, GetHiveMindCommands, true);
            if (_startHivemindTimerCallback == null)
            {
                _startHivemindTimerCallback = StartHivemindTimer;
            }
            _initialCommandDelayTimer.Reuse(Stage.InitialCommandDelay - .25f, _startHivemindTimerCallback);
            AddTimer(_initialCommandDelayTimer);
        }
    }
}
