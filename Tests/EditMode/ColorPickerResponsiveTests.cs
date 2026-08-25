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
    }
}