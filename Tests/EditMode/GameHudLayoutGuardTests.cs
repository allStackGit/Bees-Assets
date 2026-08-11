using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class GameHudLayoutGuardTests
    {
        [Test]
        public void VisibleMissionClockMovesGameSpeedButtonBesideIt()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "GameHudLayoutGuard.cs"));

            Assert.That(source, Does.Contain("_menus.Clock.activeInHierarchy"));
            Assert.That(source, Does.Contain("_clockRect.rect.width + _speedRect.rect.width"));
            Assert.That(source, Does.Contain("ControlGap"));
            Assert.That(source, Does.Contain("_speedRect.anchoredPosition = new Vector2(x, _clockRect.anchoredPosition.y);"));
            Assert.That(source, Does.Contain("_speedRect.anchoredPosition = _normalSpeedPosition;"));
        }
    }
}
