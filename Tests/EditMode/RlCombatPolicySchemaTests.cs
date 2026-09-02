using System;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlCombatPolicySchemaTests
    {
        [Test]
        public void FinalCombatSchemaHasFixedFullScaleCapacity()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");

            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedAllies"), Is.EqualTo(15));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxObservedEnemies"), Is.EqualTo(16));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "MaxWeaponSlots"), Is.EqualTo(8));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "ObservationSize"), Is.EqualTo(719));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "ContinuousActionCount"), Is.EqualTo(4));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "WeaponCommandBranchSize"), Is.EqualTo(17));
            Assert.That(RuntimeAssembly.GetStaticField(agentType, "SpecialActionBranchSize"), Is.EqualTo(2));
        }

        [Test]
        public void EnumIdentityEncodingHasCapacityForEveryCurrentShipAndWeaponType()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            int shipBits = (int)RuntimeAssembly.GetStaticField(agentType, "ShipTypeBitCount");
            int weaponBits = (int)RuntimeAssembly.GetStaticField(agentType, "WeaponTypeBitCount");
            int shipTypeCount = Enum.GetValues(RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes")).Length;
            int weaponTypeCount = Enum.GetValues(RuntimeAssembly.GetType("Assets.Scripts.ConfigData+WeaponTypes")).Length;

            Assert.That(1 << shipBits, Is.GreaterThanOrEqualTo(shipTypeCount));
            Assert.That(1 << weaponBits, Is.GreaterThanOrEqualTo(weaponTypeCount));
        }

        [Test]
        public void TacticalDistanceEncodingIsSignedBoundedAndIndependentOfArenaSize()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            float positive = (float)RuntimeAssembly.InvokeStatic(agentType, "SquashSignedDistance", 10f);
            float negative = (float)RuntimeAssembly.InvokeStatic(agentType, "SquashSignedDistance", -10f);
            float far = (float)RuntimeAssembly.InvokeStatic(agentType, "SquashSignedDistance", 10000f);

            Assert.That(positive, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(negative, Is.EqualTo(-0.2f).Within(0.0001f));
            Assert.That(far, Is.GreaterThan(0f).And.LessThan(1f));
        }
    }
}
