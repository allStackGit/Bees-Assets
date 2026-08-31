using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class MissionStatusUiPolishTests
    {
        private const string GuardTypeName = "Assets.Scripts.UIComponents.GameUiPolishGuard";

        [Test]
        public void MissionStatusBannerGrowsWithLargerTextAndStartsFlushAtTop()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "UI Components",
                "GameUiPolishGuard.cs"));

            Assert.That(source, Does.Contain("MissionStatusFontScale = 1.25f"));
            Assert.That(source, Does.Contain("MissionStatusMinHeight = 24f"));
            Assert.That(source, Does.Contain("statusAnchorMin.y = 1f;"));
            Assert.That(source, Does.Contain("statusAnchorMax.y = 1f;"));
            Assert.That(source, Does.Contain("statusPivot.y = 1f;"));
            Assert.That(source, Does.Contain("statusPosition.y = 0f;"));
            Assert.That(source, Does.Contain("statusSize.y = statusHeight;"));
            Assert.That(source, Does.Contain("StretchVertically(textRect, MissionStatusVerticalPadding);"));
        }

        [Test]
        public void MissionStatusHeightKeepsMinimumAndTextPadding()
        {
            System.Type guardType = RuntimeAssembly.GetType(GuardTypeName);

            float minimum = (float)RuntimeAssembly.InvokeStatic(
                guardType,
                "CalculateMissionStatusHeight",
                10f);
            float expanded = (float)RuntimeAssembly.InvokeStatic(
                guardType,
                "CalculateMissionStatusHeight",
                30f);

            Assert.That(minimum, Is.EqualTo(24f).Within(0.001f));
            Assert.That(expanded, Is.EqualTo(36f).Within(0.001f));
        }
    }
}
