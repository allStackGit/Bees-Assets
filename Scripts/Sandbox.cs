

using Assets.Scripts.UIComponents;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Assets.Scripts
{
    public class Sandbox : MonoBehaviour
    {
        public GameObject ColorPicker;
        // Use this for initialization
        void Start()
        {

        }

        // Update is called once per frame
        void Update()
        {

        }
        public void OpenColorPicker()
        {
            ColorPicker.GetComponent<ColorPicker>().Toggle();
            //Debugger.Log("Opening/closing color picker");

        }

        public void PickColor(BaseEventData data)
        {
            ColorPicker.GetComponent<ColorPicker>().GetColor(data);
        }

        public void TypeColor(string color)
        {
            ColorPicker.GetComponent<ColorPicker>().ChangeHexValue(color);
        }
    }
}