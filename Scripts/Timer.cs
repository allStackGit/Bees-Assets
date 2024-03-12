using System;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts
{
    public class Timer

    {
        public float Length;
        public float Elapsed;
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
                Elapsed = 0;
                return true;
            }
            return false;
        }
    }
}