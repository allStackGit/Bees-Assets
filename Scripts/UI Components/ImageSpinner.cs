using Assets.Scripts.Levels;
using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace Assets.Scripts.UI_Components
{
    public class ImageSpinner : MonoBehaviour
    {
        public int DegreesPerSecond;
        public float UpdatesPerSecond;
        public Level Level;
        public ScaledTimer Timer = new ScaledTimer();
        public void Setup(Level level)
        {
            Level = level;
            Timer.Reuse(1 / UpdatesPerSecond, Rotate, true);
            Level.AddTimer(Timer);
        }
        private void Rotate()
        {
            transform.eulerAngles = new Vector3(transform.eulerAngles.x, transform.eulerAngles.y, transform.eulerAngles.z + DegreesPerSecond / UpdatesPerSecond);
            //Invoke(nameof(Rotate), 1 / UpdatesPerSecond);
        }
    }
}