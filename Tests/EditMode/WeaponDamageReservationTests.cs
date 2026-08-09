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
    }
}
