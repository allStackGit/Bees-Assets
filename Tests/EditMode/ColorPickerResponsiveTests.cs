using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ColorPickerResponsiveTests
    {
        private const string ColorPickerTypeName = "Assets.Scripts.UIComponents.ColorPicker";

        [Test]
        public void PickerSamplingUsesTheUniformRenderedRootCanvasScale()
        {
            GameObject canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.scaleFactor = 1.75f;

            GameObject pickerObject = new GameObject("Color Picker", typeof(RectTransform));
            pickerObject.transform.SetParent(canvasObject.transform, false);
            Component picker = pickerObject.AddComponent(RuntimeAssembly.GetType(ColorPickerTypeName));

            try
            {
                RuntimeAssembly.Invoke(picker, "UpdateScreenScaleFactor");
                Vector2 scale = (Vector2)RuntimeAssembly.GetField(picker, "_screenScaleFactor");

                Assert.That(scale.x, Is.EqualTo(canvas.scaleFactor).Within(0.001f));
                Assert.That(scale.y, Is.EqualTo(canvas.scaleFactor).Within(0.001f));
                Assert.That(scale.x, Is.EqualTo(scale.y).Within(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(canvasObject);
            }
        }

        [Test]
        public void ResponsivePickerGeometryFormsOneAlignedCompactStack()
        {
            GameObject pickerObject = new GameObject("Color Picker", typeof(RectTransform));
            RectTransform pickerRect = pickerObject.GetComponent<RectTransform>();
            pickerRect.sizeDelta = new Vector2(256f, 624f);

            RectTransform hex = CreateChild("Hex Value", pickerRect, new Vector2(220f, 24f), new Vector2(0f, 104f));
            RectTransform sheet = CreateChild("Color Sheet", pickerRect, new Vector2(256f, 256f), new Vector2(18f, -35f));
            RectTransform square = CreateChild("Color Square", pickerRect, new Vector2(256f, 75f), new Vector2(18f, 112f));
            RectTransform mouse = CreateChild("Mouse", sheet, new Vector2(16f, 16f), Vector2.zero);

            Component picker = pickerObject.AddComponent(RuntimeAssembly.GetType(ColorPickerTypeName));
            RuntimeAssembly.SetField(picker, "HexInput", hex.gameObject);
            RuntimeAssembly.SetField(picker, "ColorSheet", sheet.gameObject);
            RuntimeAssembly.SetField(picker, "ColorSquare", square.gameObject);
            RuntimeAssembly.SetField(picker, "MouseIndicator", mouse.gameObject);

            try
            {
                RuntimeAssembly.Invoke(picker, "PrepareResponsiveGeometry");

                Assert.That(pickerRect.rect.width, Is.EqualTo(256f).Within(0.01f));
                Assert.That(pickerRect.rect.height, Is.EqualTo(355f).Within(0.01f));
                Assert.That(hex.rect.width, Is.EqualTo(256f).Within(0.01f));
                Assert.That(sheet.rect.width, Is.EqualTo(256f).Within(0.01f));
                Assert.That(square.rect.width, Is.EqualTo(256f).Within(0.01f));

                Bounds hexBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(pickerRect, hex);
                Bounds sheetBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(pickerRect, sheet);
                Bounds squareBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(pickerRect, square);
                Assert.That(hexBounds.max.y, Is.EqualTo(pickerRect.rect.yMax).Within(0.01f));
                Assert.That(hexBounds.min.y, Is.EqualTo(sheetBounds.max.y).Within(0.01f));
                Assert.That(sheetBounds.min.y, Is.EqualTo(squareBounds.max.y).Within(0.01f));
                Assert.That(squareBounds.min.y, Is.EqualTo(pickerRect.rect.yMin).Within(0.01f));
                Assert.That(hexBounds.min.x, Is.EqualTo(sheetBounds.min.x).Within(0.01f));
                Assert.That(sheetBounds.min.x, Is.EqualTo(squareBounds.min.x).Within(0.01f));
                Assert.That(hexBounds.max.x, Is.EqualTo(sheetBounds.max.x).Within(0.01f));
                Assert.That(sheetBounds.max.x, Is.EqualTo(squareBounds.max.x).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(pickerObject);
            }
        }

        [Test]
        public void DisplayScaleRefreshDoesNotCloseAnOpenPicker()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "UI Components",
                "ColorPicker.cs"));

            int methodStart = source.IndexOf("public void SetScreenScaleFactor()", System.StringComparison.Ordinal);
            int nextMethod = source.IndexOf("void Start()", methodStart, System.StringComparison.Ordinal);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(nextMethod, Is.GreaterThan(methodStart));

            string method = source.Substring(methodStart, nextMethod - methodStart);
            Assert.That(method, Does.Not.Contain("Toggle();"));
            Assert.That(method, Does.Contain("StartCoroutine(SetTexture())"));
        }

        private static RectTransform CreateChild(
            string name,
            RectTransform parent,
            Vector2 size,
            Vector2 position)
        {
            GameObject child = new GameObject(name, typeof(RectTransform));
            RectTransform rect = child.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            return rect;
        }
    }
}