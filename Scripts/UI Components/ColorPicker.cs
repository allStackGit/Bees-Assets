
using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Assets.Scripts.UIComponents
{
    public class ColorPicker : MonoBehaviour
    {
        private bool _hasActiveTexture = false;
        private Color[] _colors;
        private Texture2D _colorTexture = null;
        private RectTransform _mouse;
        private Vector2 _screenScaleFactor = Vector2.one;
        private Image _colorSquareImage;
        private TMP_InputField _hexInput;
        private RectTransform _colorSheetRect;

        public GameObject ColorSheet, ColorSquare, MouseIndicator, HexInput;
        public Camera Camera;
        public Vector2 ReferenceScreenSize;
        public Vector2 RectAdjustment;

        public bool IsActive => gameObject.activeSelf;

        private void EnsureReferences()
        {
            if (_mouse == null) _mouse = MouseIndicator.GetComponent<RectTransform>();
            if (_colorSquareImage == null) _colorSquareImage = ColorSquare.GetComponent<Image>();
            if (_hexInput == null) _hexInput = HexInput.GetComponent<TMP_InputField>();
            if (_colorSheetRect == null) _colorSheetRect = ColorSheet.GetComponent<RectTransform>();
        }

        public void SetScreenScaleFactor()
        {
            UpdateScreenScaleFactor();
            _hasActiveTexture = false;
            MouseIndicator.SetActive(false);

            // A resolution/aspect change invalidates the sampled screen texture, but it must not
            // close an open picker. Re-sample after layout/overlay placement settles this frame.
            if (IsActive)
            {
                StartCoroutine(SetTexture());
            }
        }

        void Start()
        {
            EnsureReferences();
            UpdateScreenScaleFactor();
        }

        private void UpdateScreenScaleFactor()
        {
            Canvas canvas = GetComponentInParent<Canvas>();
            float liveCanvasScale = canvas != null ? canvas.rootCanvas.scaleFactor : 0f;
            if (liveCanvasScale > 0f)
            {
                // CanvasScaler produces one rendered UI scale. Screen.width/referenceWidth and
                // Screen.height/referenceHeight diverge on non-reference aspect ratios and therefore
                // cannot describe the rendered Color Sheet dimensions independently.
                _screenScaleFactor = new Vector2(liveCanvasScale, liveCanvasScale);
                return;
            }

            if (ReferenceScreenSize.x > 0f && ReferenceScreenSize.y > 0f)
            {
                _screenScaleFactor = new Vector2(
                    Screen.width / ReferenceScreenSize.x,
                    Screen.height / ReferenceScreenSize.y);
                return;
            }

            _screenScaleFactor = Vector2.one;
        }

        public IEnumerator SetTexture()
        {
            yield return new WaitForEndOfFrame();
            EnsureReferences();
            UpdateScreenScaleFactor();
            if (IsActive && !_hasActiveTexture)
            {
                Vector2 size = Size();
                int width = Mathf.Max(1, Mathf.RoundToInt(size.x * _screenScaleFactor.x));
                int height = Mathf.Max(1, Mathf.RoundToInt(size.y * _screenScaleFactor.y));

                Rect rect = new Rect((ColorSheet.transform.position.x - (width / 2f)) - RectAdjustment.x,
                    (ColorSheet.transform.position.y - (height / 2f)) - RectAdjustment.y, width, height);

                if (_colorTexture != null)
                {
                    Destroy(_colorTexture);
                }

                _colorTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
                _colorTexture.ReadPixels(rect, 0, 0);
                _colorTexture.Apply(true);
                _hasActiveTexture = true;
                _colors = _colorTexture.GetPixels();

                MouseIndicator.SetActive(true);
            }
            ChangeHexValue(_hexInput.text);
        }

        private Vector2 Size()
        {
            EnsureReferences();
            return new Vector2(_colorSheetRect.rect.width, _colorSheetRect.rect.height);
        }

        public Color GetColor(BaseEventData data)
        {
            EnsureReferences();
            PointerEventData pointer = data as PointerEventData;

            _mouse.position = pointer.position;
            _mouse.localPosition = new Vector2(Math.Clamp(_mouse.localPosition.x, -1 * ((_colorTexture.width / 2) / _screenScaleFactor.x), ((_colorTexture.width / 2) / _screenScaleFactor.x)),
                Math.Clamp(_mouse.localPosition.y, -1 * ((_colorTexture.height / 2) / _screenScaleFactor.y), (_colorTexture.height / 2) / _screenScaleFactor.y));

            Vector2 imagePosition = new Vector2(
                (int)(Math.Clamp(_mouse.localPosition.x * _screenScaleFactor.x, -1 * _colorTexture.width / 2, _colorTexture.width / 2)),
                (int)(Math.Clamp(_mouse.localPosition.y * _screenScaleFactor.y, -1 * _colorTexture.height / 2, _colorTexture.height / 2)));

            int index = PositionToColorIndex(imagePosition);
            Color color = _colors[index];
            _colorSquareImage.color = color;
            _hexInput.SetTextWithoutNotify($"#{ColorUtility.ToHtmlStringRGB(color).Substring(0, 6).ToLower()}");
            return color;
        }

        private int PositionToColorIndex(Vector2 position)
        {
            int x = (int)(position.x + (_colorTexture.width / 2));
            int y = (int)(position.y + (_colorTexture.height / 2));

            x = Math.Clamp(x, 0, _colorTexture.width - 1);
            y = Math.Clamp(y, 0, _colorTexture.height - 1);

            int index = (y * _colorTexture.width) + x;
            return Math.Clamp(index, 0, _colors.Length - 1);
        }

        private Vector2 ColorIndexToPosition(int index)
        {
            int y = (int)Math.Floor((double)index / _colorTexture.width);
            int x = index % _colorTexture.width;
            return new Vector2(((x - (_colorTexture.width / 2)) / _screenScaleFactor.x), ((y - (_colorTexture.height / 2)) / _screenScaleFactor.y));
        }

        private Color SetColor(Color color)
        {
            EnsureReferences();
            if (_colors == null || _colors.Length == 0)
            {
                _colorSquareImage.color = color;
                return color;
            }

            int closestIndex = 0;
            float closestDistanceSquared = float.MaxValue;
            for (int i = 0; i < _colors.Length; i++)
            {
                Color textureColor = _colors[i];
                float red = color.r - textureColor.r;
                float green = color.g - textureColor.g;
                float blue = color.b - textureColor.b;
                float alpha = color.a - textureColor.a;
                float distanceSquared = (red * red) + (green * green) + (blue * blue) + (alpha * alpha);
                if (distanceSquared < closestDistanceSquared)
                {
                    closestDistanceSquared = distanceSquared;
                    closestIndex = i;
                }
            }

            _colorSquareImage.color = color;
            _mouse.localPosition = ColorIndexToPosition(closestIndex);
            return color;
        }

        public void Toggle()
        {
            gameObject.SetActive(!gameObject.activeSelf);
            if (IsActive)
            {
                UpdateScreenScaleFactor();
                StartCoroutine(SetTexture());
            }
        }

        public Color ChangeHexValue(string hex)
        {
            if (!hex.StartsWith("#") && hex.Length == 6)
            {
                hex = $"#{hex}";
            }
            if (hex.Length == 7 && UnityEngine.ColorUtility.TryParseHtmlString(hex, out Color color))
            {
                return SetColor(color);
            }
            return Color.white;
        }
    }
}