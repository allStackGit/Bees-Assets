using System;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ProjectileDamagePolicyTests
    {
        private Type _policy;
        private Type _shipTypes;
        private Type _projectileTypes;

        [SetUp]
        public void SetUp()
        {
            _policy = RuntimeAssembly.GetType("Assets.Scripts.Entities.Projectiles.ProjectileDamagePolicy");
            _shipTypes = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes");
            _projectileTypes = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ProjectileTypes");
        }

        [TestCase(1, "Honeybee", 2, false, true)]
        [TestCase(1, "Honeybee", 1, false, false)]
        [TestCase(1, "Honeybee", 2, true, false)]
        [TestCase(1, "FireBarge", 1, false, true)]
        public void BasicProjectileMatrix(
            int shooterSide, string shooterType, int targetSide, bool ignored, bool expected)
        {
            bool actual = (bool)RuntimeAssembly.InvokeStatic(
                _policy,
                "CanBasicProjectileDamage",
                shooterSide,
                Enum.Parse(_shipTypes, shooterType),
                targetSide,
                ignored);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [TestCase(1, "Honeybee", 2, "RocketExplosion", false, false, false, true)]
        [TestCase(1, "Honeybee", 1, "RocketExplosion", false, false, false, false)]
        [TestCase(1, "FireBarge", 1, "FireBargeExplosion", false, false, false, true)]
        [TestCase(1, "Honeybee", 1, "FireTankExplosion", false, false, false, true)]
        [TestCase(1, "Honeybee", 2, "RocketExplosion", true, false, false, false)]
        [TestCase(1, "Honeybee", 2, "RocketExplosion", false, true, false, false)]
        [TestCase(1, "Honeybee", 2, "RocketExplosion", false, false, true, false)]
        public void ExplosionMatrix(
            int shooterSide,
            string shooterType,
            int targetSide,
            string projectileType,
            bool targetDead,
            bool harmless,
            bool alreadyHit,
            bool expected)
        {
            bool actual = (bool)RuntimeAssembly.InvokeStatic(
                _policy,
                "CanExplosionDamage",
                shooterSide,
                Enum.Parse(_shipTypes, shooterType),
                targetSide,
                Enum.Parse(_projectileTypes, projectileType),
                targetDead,
                harmless,
                alreadyHit);
            Assert.That(actual, Is.EqualTo(expected));
        }
    }
}
