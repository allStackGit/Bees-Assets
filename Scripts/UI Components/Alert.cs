using Assets.Scripts.Scenes;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Assets.Scripts.UI_Components
{
    public class Alert : Dialogue
    {
        public  Alert(GameObject prefab, string title, string explantion, string buttonText) : 
            base(prefab, title, explantion, new List<string> {buttonText}, new List<UnityAction>())
        {
            
        }
    }
}