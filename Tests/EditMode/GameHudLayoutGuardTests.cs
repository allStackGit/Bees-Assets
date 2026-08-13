using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class GameHudLayoutGuardTests
    {
        private static string ReadGuardSource()
        {
            return File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "GameHudLayoutGuard.cs"));
        }

        [Test]
        public void VisibleMissionClockMovesGameSpeedButtonBesideIt()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("_menus.Clock.activeInHierarchy"));
            Assert.That(source, Does.Contain("_clockRect.rect.width + _speedRect.rect.width"));
            Assert.That(source, Does.Contain("ControlGap"));
            Assert.That(source, Does.Contain("_speedRect.anchoredPosition = new Vector2(x, y);"));
            Assert.That(source, Does.Contain("_speedRect.anchoredPosition = _normalSpeedPosition;"));
        }

        [Test]
        public void VisiblePlutoShieldAlignsGameSpeedButtonWithEvacuationCounterTop()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("_menus.PlutoShield.activeInHierarchy"));
            Assert.That(source, Does.Contain("_menus.Counter.activeInHierarchy"));
            Assert.That(source, Does.Contain("_counterRect.anchoredPosition.y"));
            Assert.That(source, Does.Contain("_counterRect.rect.height - _speedRect.rect.height"));
            Assert.That(source, Does.Contain("((_counterRect.rect.height - _speedRect.rect.height) * 0.5f)"));
        }

        [Test]
        public void ShieldOnlyLayoutStillPlacesGameSpeedButtonBelowShield()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("_plutoShieldRect.anchoredPosition.y"));
            Assert.That(source, Does.Contain("_plutoShieldRect.rect.height + _speedRect.rect.height"));
            Assert.That(source, Does.Contain("((_plutoShieldRect.rect.height + _speedRect.rect.height) * 0.5f) - ControlGap"));
        }
    }
}
