using System;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts
{
    /// <summary>
    /// A custom timer that runs actions after scaled time. Replaces Invoke and InvokeRepeating
    /// </summary>
    public class ScaledTimer
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
        public Action Action;
        /// <summary>
        /// Whether or not the action is recurring
        /// </summary>
        public bool IsRecurring;
        /// <summary>
        /// Whether the action has been canceled
        /// </summary>
        public bool IsCanceled;
        /// <summary>
        /// The unique Id of the ScaledTimer
        /// </summary>
        public long Id;
        /// <summary>
        /// Takes the time in seconds to elapse before calling action()
        /// </summary>
        /// <param name="length"></param>
        /// <param name="action"></param>
        public ScaledTimer(float length, Action action, bool isRecurring = false)
        {
            Length = length;
            Action = action;
            IsRecurring = isRecurring;
            Id = Utilities.Hash();
        }
        public ScaledTimer() {
            Id = Utilities.Hash();
        }
        /// <summary>
        /// Reuses an existing ScaledTimer with new parameters
        /// </summary>
        /// <param name="length"></param>
        /// <param name="action"></param>
        /// <param name="isRecurring"></param>
        public void Reuse(float length, Action action, bool isRecurring = false)
        {
            Length = length;
            Action = action;
            IsRecurring = isRecurring;
            Elapsed = 0;
            IsCanceled = false;
        }

        /// <summary>
        /// Checks how much time has passed and calls the action if necessary. Must be called from outside of the method since it's not directly tied to Update()
        /// Returns true when the action has completed. Runs in unscaled time.
        /// </summary>
        public bool Update()
        {
            Elapsed += Time.deltaTime;
            if (Elapsed > Length && !IsCanceled)
            {
                //Debug.Log($"Running {this}");
                Action();
                Elapsed -= Length;
                return true;
            }
            return false;
        }

        public override string ToString()
        {
            return $" #{Id}: {Action?.Method?.Name} at {Length}s isCanceled? {IsCanceled}";
        }
        private ScaledTimer s;
        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
            s = obj as ScaledTimer;
            if (s == null)
            {
                return false;
            }

            return Id == s.Id;
        }
        public bool Equals(ScaledTimer other)
        {
            return Id == other.Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        public static bool operator ==(ScaledTimer a, ScaledTimer b)
        {
            // If both are null, or both are same instance, return true.
            if (System.Object.ReferenceEquals(a, b))
            {
                return true;
            }

            // If one is null, but not both, return false.
            if (((object)a == null) || ((object)b == null))
            {
                return false;
            }

            return a.Id == b.Id;
        }

        public static bool operator !=(ScaledTimer a, ScaledTimer b)
        {
            return !(a == b);
        }
    }
}