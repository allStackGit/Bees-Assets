using System;
using UnityEngine;

namespace Assets.Scripts
{
    /// <summary>
    /// A custom timer that runs recurring actions after unscaled time
    /// </summary>
    public class Timer
    {
        /// <summary>
        /// The amount of time to pass before running the action
        /// </summary>
        public float Length;
        /// <summary>
        /// The amount of time in seconds that have elapsed since the last time the action has been run
        /// </summary>
        public float Elapsed;
        /// <summary>
        /// The action to be run after [Elapsed] seconds have elapsed
        /// </summary>
        Action Action;

        /// <summary>
        /// Takes the time in seconds to elapse before calling action()
        /// </summary>
        /// <param name="length"></param>
        /// <param name="action"></param>
        public Timer(float length, Action action)
        {
            Length = length;
            Action = action;
        }

        /// <summary>
        /// Checks how much time has passed and calls the action if necessary. Must be called from outside of the method since it's not directly tied to Update()
        /// Returns true when the action has completed. Runs in unscaled time.
        /// </summary>
        public bool Update()
        {
            Elapsed += Time.unscaledDeltaTime;
            if (Elapsed > Length)
            {
                Action();
                Elapsed -= Length;
                return true;
            }
            return false;
        }
    }
}