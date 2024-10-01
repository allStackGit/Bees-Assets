using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace Assets.Scripts.UI_Components
{
    public class ImageSpinner : MonoBehaviour
    {
        public int DegreesPerSecond;
        public float UpdatesPerSecond;
        void Start()
        {
            Invoke(nameof(Rotate), 1 / UpdatesPerSecond);
        }

        private void Rotate()
        {
            transform.eulerAngles = new Vector3(transform.eulerAngles.x, transform.eulerAngles.y, transform.eulerAngles.z + DegreesPerSecond / UpdatesPerSecond);
            Invoke(nameof(Rotate), 1 / UpdatesPerSecond);
        }
    }
}