
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;

using UnityEngine;
using UnityEngine.EventSystems;


namespace Assets.Scripts.UIComponents
{
    public class ColorPicker : MonoBehaviour
    {

        private bool _hasActiveTexture = false;
        private Color[] _colors;
        private Texture2D _colorTexture = null;
        private RectTransform _mouse;
        private Vector2 _screenScaleFactor;

        public GameObject ColorSheet, ColorSquare, MouseIndicator, HexInput;
        public Camera Camera;
        public Vector2 ReferenceScreenSize;
        public Vector2 RectAdjustment;



        public bool IsActive => gameObject.activeSelf;
        // Use this for initialization
        public void SetScreenScaleFactor()
        {
            //Debug.Log("Setting base world point");
            _screenScaleFactor = new Vector2(Screen.width / ReferenceScreenSize.x, Screen.height / ReferenceScreenSize.y);
            _hasActiveTexture = false;
            MouseIndicator.SetActive(false);

            if (IsActive)
            {
                Toggle();
            }
        }
        void Start()
        {
            _screenScaleFactor = new Vector2(Screen.width / ReferenceScreenSize.x, Screen.height / ReferenceScreenSize.y);
            //Debug.Log($"Screen Scale Factor: {_screenScaleFactor}");
            //SetBaseWorldPoint();
            _mouse = MouseIndicator.GetComponent<RectTransform>();
        }
        private void Update()
        {
            //Debug.Log($"Frame {frameCount++}");
        }
        public IEnumerator SetTexture()
        {
            //Debug.Log("Coroutined");
            //GameObject.Find("Pixels Read").transform.position = ColorSheet.transform.position;
            yield return new WaitForEndOfFrame();
            //Debug.Log("Yielded");
            if (IsActive && !_hasActiveTexture)
            {
                Vector2 size = Size();
                int width = (int) (size.x * _screenScaleFactor.x);
                int height = (int) (size.y * _screenScaleFactor.y);
                //Debug.Log($"Width and height: {size}");

                Rect rect = new Rect((ColorSheet.transform.position.x-((width/2)))-RectAdjustment.x, 
                    (ColorSheet.transform.position.y-((height/2)))-RectAdjustment.y, width, height);

                //Debug.Log($"Set texture, ColorSheet position: {ColorSheet.transform.position}, Rect {rect.size}, {size}");


                //_colorTexture = new Texture2D((int) (width * _screenScaleFactor.x), (int) (height * _screenScaleFactor.y));
                _colorTexture = new Texture2D(width, height, TextureFormat.RGB24, false);

                //_colorTexture = ColorSheet.GetComponent<UnityEngine.UI.Image>().sprite.texture;

                _colorTexture.ReadPixels(rect, 0, 0);
                _colorTexture.Apply(true);
                _hasActiveTexture = true;
                _colors = _colorTexture.GetPixels();

                //string path = $"{ConfigData.GetBasePath()}/{Utilities.Nonce()}.png";
                //File.WriteAllBytes(path, _colorTexture.EncodeToPNG());
                MouseIndicator.SetActive(true);
                StopCoroutine(SetTexture());
            }
            ChangeHexValue(HexInput.GetComponent<TMP_InputField>().text);
        }
        private Vector2 Size()
        {
            //Vector2 screenPoint = Camera.WorldToScreenPoint(new Vector2(ColorSheet.GetComponent<RectTransform>().rect.width, ColorSheet.GetComponent<RectTransform>().rect.height));
            //Vector2 adjusted = new Vector2(Mathf.Abs(_baseWorldPoint.x - screenPoint.x), Mathf.Abs(_baseWorldPoint.y - screenPoint.y));
            //return new Vector2(ColorSheet.GetComponent<RectTransform>().rect.width * _screenScaleFactor.x, ColorSheet.GetComponent<RectTransform>().rect.height * _screenScaleFactor.y);
            return new Vector2(ColorSheet.GetComponent<RectTransform>().rect.width, ColorSheet.GetComponent<RectTransform>().rect.height);

        }
        public Color GetColor(BaseEventData data)
        {
            PointerEventData pointer = data as PointerEventData;


            _mouse.position = pointer.position;
            //Debug.Log($"RAW position over color chart: {_mouse.localPosition}");

            _mouse.localPosition = new Vector2(Math.Clamp(_mouse.localPosition.x, -1 * ((_colorTexture.width / 2) / _screenScaleFactor.x), ((_colorTexture.width / 2) / _screenScaleFactor.x)),
                Math.Clamp(_mouse.localPosition.y, -1 * ((_colorTexture.height / 2) / _screenScaleFactor.y), (_colorTexture.height / 2) / _screenScaleFactor.y));



            Vector2 imagePosition = new Vector2(
                (int) (Math.Clamp(_mouse.localPosition.x * _screenScaleFactor.x, -1 * _colorTexture.width / 2, _colorTexture.width / 2)),
                (int) (Math.Clamp(_mouse.localPosition.y * _screenScaleFactor.y, -1 * _colorTexture.height / 2, _colorTexture.height / 2))
                );

            //Debug.Log($"Clamped image position over color chart: {imagePosition}");


            int index = PositionToColorIndex(imagePosition);

            //Debug.Log($"Index at mouse position {MousePosition.x}, {MousePosition.y} index {index}/{_colors.Length}");

            Color color = _colors[index];
            //Color color = ColorTexture.GetPixel(x, y);
            //Debug.Log($"Color at mouse position {imagePosition.x}, {imagePosition.y} index {index}: {color.ToHexString()}");
            Vector2 position = ColorIndexToPosition(index);
            //Debug.Log($"Mouse position at color: {color.ToHexString()}, mouse position: {position}");


            ColorSquare.GetComponent<UnityEngine.UI.Image>().color = color;
            HexInput.GetComponent<TMP_InputField>().SetTextWithoutNotify($"#{ColorUtility.ToHtmlStringRGB(color).Substring(0, 6).ToLower()}");
            return color;

        }
        private int PositionToColorIndex(Vector2 position)
        {


            int x = (int)(position.x + (_colorTexture.width / 2));
            int y = (int)(position.y + (_colorTexture.height / 2));

            x = Math.Clamp(x, 0, _colorTexture.width - 1);
            y = Math.Clamp(y, 0, _colorTexture.height - 1);


            int index = (y * _colorTexture.width) + x;
            //Debug.Log($"Original position: {position} gives index: {(position.y * _colorTexture.width) + position.x}");

            //Debug.Log($"Modified position: {x}, {y} gives index: {index}/{_colors.Length} and color {_colors[index].ToHexString()}");

            //Debug.Log($"Unclamped Index at position {x}, {y} index {index}/{_colors.Length}");
            index = Math.Clamp(index, 0, _colors.Length-1);
            return index;
        }
        private Vector2 ColorIndexToPosition(int index)
        {
            int y = (int) Math.Floor((double) index / _colorTexture.width);
           
            int x = index % _colorTexture.width;

            Vector2 position = new Vector2(((x-(_colorTexture.width/2))/_screenScaleFactor.x), ((y-(_colorTexture.height/2))/_screenScaleFactor.y));
            //Debug.Log($"Mouse position at color index: {index}, mouse position: {position}");
            return position;
        }
        private Color SetColor(Color color)
        {
            //Debug.Log($"Color received: {color.ToHexString()}");
            int maxColors = (int)Math.Pow(16, 6);
            int storedColors = _colors.Length;
            double ratio = maxColors / storedColors;

            //Debug.Log($"There are {maxColors} possible colors in the RGB space. There are {storedColors} in the color array. " +
            //               $"For every {ratio} real colors, there is 1 stored color.");

            List<dynamic> distances = new List<object>();
            
            for (int i = 0; i < _colors.Length; i++)
            {
                Color textureColor = _colors[i];
                distances.Add(new
                {
                    distance = Vector4.Distance(color, textureColor), //DistanceBetweenColors(color, textureColor),
                    color = textureColor,
                    index = i
                });
            }

            distances = distances.OrderBy((d) => d.distance).ToList();
            dynamic closest = distances.First();
            Color closestColor = closest.color;
            int index = (int) closest.index;
            ColorSquare.GetComponent<UnityEngine.UI.Image>().color = color;
            _mouse.localPosition = ColorIndexToPosition(index);

            //Debug.Log($"We found the closest matching color: {closestColor.ToHexString()}, at index: {index} " +
            //            $"The distance between the two colors is {closest.distance}. Trying to color the square and move the indicator");
            return color;
        }
        public void Toggle()
        {
            //Debug.Log($"Called toggle() and the gameObject was active: {IsActive}:{gameObject.activeSelf}");
            gameObject.SetActive(!gameObject.activeSelf);
            //_hasActiveTexture = false;
            if (IsActive)
            {
                StartCoroutine(SetTexture());
            }
        }
        public Color ChangeHexValue(string hex)
        {
            if (!hex.StartsWith("#") && hex.Length == 6)
            {
                hex = $"#{hex}";
            }
            if (hex.Length == 7)
            {
                //Debug.Log($"Hex value: {hex}");
                Color color;
                if (UnityEngine.ColorUtility.TryParseHtmlString(hex, out color))
                {
                    return SetColor(color);
                }
            }
            return Color.white;
        }

    }
}