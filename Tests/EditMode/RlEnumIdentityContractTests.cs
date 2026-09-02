using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlEnumIdentityContractTests
    {
        [Test]
        public void PolicySchemaFreezesExistingShipAndWeaponIds()
        {
            string schema = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "RlPolicySchema.cs"));

            Assert.That(schema, Does.Contain("CheckEnum(errors, ConfigData.ShipTypes.Barge, 0, \"ship\");"));
            Assert.That(schema, Does.Contain("CheckEnum(errors, ConfigData.ShipTypes.HumanTarget, 23, \"ship\");"));
            Assert.That(schema, Does.Contain("CheckEnum(errors, ConfigData.WeaponTypes.Bomb, 0, \"weapon\");"));
            Assert.That(schema, Does.Contain("CheckEnum(errors, ConfigData.WeaponTypes.SplitShot, 9, \"weapon\");"));
            Assert.That(schema, Does.Contain("ValidateFrozenEnumMappings(errors);"));
        }
    }
}
