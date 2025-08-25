using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public class Trigger
    {
        public Action TriggeredAction;
        public Func<bool> Conditional;
        public string Name;
        public bool HasBeenTriggered;

        public Trigger(Func<bool> conditional, Action triggeredAction, string name)
        {
            TriggeredAction = triggeredAction;
            Conditional = conditional;
            Name = name;
        }

        public void Action()
        {
            TriggeredAction();
            HasBeenTriggered = true;
        }
    }
}