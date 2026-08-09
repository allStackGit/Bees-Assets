using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class MiningAccountingTests
    {
        [Test]
        public void MiningDistributesIntegerRemainderInsteadOfDroppingResources()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Mining.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("_baseAmountPerShip = _amountMined / ShipsCurrentlyMining.Count", source);
            StringAssert.Contains("_miningRemainder = _amountMined % ShipsCurrentlyMining.Count", source);
            StringAssert.Contains("(i < _miningRemainder ? 1 : 0)", source);
        }

        [TestCase(10, 3)]
        [TestCase(7, 4)]
        [TestCase(2, 5)]
        [TestCase(100, 6)]
        public void RemainderAllocationConservesTotal(int amount, int minerCount)
        {
            int baseAmount = amount / minerCount;
            int remainder = amount % minerCount;
            int allocated = 0;

            for (int i = 0; i < minerCount; i++)
            {
                allocated += baseAmount + (i < remainder ? 1 : 0);
            }

            Assert.That(allocated, Is.EqualTo(amount));
        }
    }
}
