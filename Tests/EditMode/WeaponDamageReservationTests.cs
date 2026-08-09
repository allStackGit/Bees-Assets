using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class WeaponDamageReservationTests
    {
        [Test]
        public void ExactlyLethalReservedDamagePreventsAdditionalTargeting()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons", "Weapon.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("_shipDamageStatus.TotalDamageSentToShip < _shipDamageStatus.Health", source);
            StringAssert.DoesNotContain("_shipDamageStatus.TotalDamageSentToShip <= _shipDamageStatus.Health", source);
        }

        [Test]
        public void DualCannonReservesExactlyItsAggregateProjectileDamage()
        {
            string dualPath = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons", "DualCannon.cs");
            string levelPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.cs");
            string dualSource = File.ReadAllText(dualPath);
            string levelSource = File.ReadAllText(levelPath);

            StringAssert.Contains("_projectile_power /= 2", levelSource);
            StringAssert.Contains("TotalDamageSentToShip += Power;", dualSource);
            StringAssert.DoesNotContain("TotalDamageSentToShip += Power * 2", dualSource);
        }
    }
}