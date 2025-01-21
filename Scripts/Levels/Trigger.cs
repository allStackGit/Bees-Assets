using System;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public class Trigger 
    {
        public Action TriggeredAction;
        public Func<bool> Conditional;
        public bool HasBeenTriggered;

        public Trigger(Func<bool> conditional, Action triggeredAction)
        {
            TriggeredAction = triggeredAction;
            Conditional = conditional;
        }
        
        public void Action()
        {
            TriggeredAction();
            HasBeenTriggered = true;
        }

    }
}