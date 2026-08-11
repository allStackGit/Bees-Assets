using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ProjectileCommandAttributionTests
    {
        private static string Read(params string[] parts)
        {
            string path = Application.dataPath;
            foreach (string part in parts) path = Path.Combine(path, part);
            return File.ReadAllText(path);
        }

        [Test]
        public void ProjectileSnapshotsAndUsesOriginatingCommandOutcome()
        {
            string source = Read("Scripts", "Entities", "Projectiles", "Projectile.cs");

            StringAssert.Contains("public long CommandOutcomeId;", source);
            StringAssert.Contains("Command firingCommand = shooter.Squad?.GetCommand();", source);
            StringAssert.Contains("firingCommand.IsHiveMindCommand", source);
            StringAssert.Contains("CommandOutcomeId = 0;", source);
            StringAssert.Contains("ship, CommandOutcomeId);", source);
        }

        [Test]
        public void DelayedDamageCreditsMatchingActiveOrStoredOutcome()
        {
            string combat = Read("Scripts", "Entities", "Ships", "Ship.Combat.cs");
            string state = Read("Scripts", "Levels", "GameState.Commands.cs");

            StringAssert.Contains("activeCommand.OutcomeId == attackerCommandOutcomeId", combat);
            StringAssert.Contains("AddTsvToStoredCommand(attackerCommandOutcomeId, tsvDelta)", combat);
            StringAssert.Contains("public bool AddTsvToStoredCommand(long outcomeId, long tsvDelta)", state);
            StringAssert.Contains("storedCommand.Tsv += tsvDelta;", state);
        }

        [TestCase("Rocket.cs", "RocketExplosion.InheritCommandAttributionFrom(this);")]
        [TestCase("SplitterShot.cs", "_projectile.InheritCommandAttributionFrom(this);")]
        public void ProjectileChildrenInheritParentCommandOutcome(string filename, string expected)
        {
            string source = Read("Scripts", "Entities", "Projectiles", filename);
            StringAssert.Contains(expected, source);
        }

        [Test]
        public void FireTankExplosionInheritsLastHitProjectileCommandOutcome()
        {
            string source = Read("Scripts", "Entities", "CanisterBomb.cs");
            StringAssert.Contains("Explosion.InheritCommandAttributionFrom(LastHitProjectile);", source);
        }

        [Test]
        public void ExplosionAndDelayedStrikerDamagePassSnapshottedOutcome()
        {
            string explosion = Read("Scripts", "Entities", "Projectiles", "RocketExplosion.cs");
            string strikerBomb = Read("Scripts", "Entities", "Projectiles", "StrikerBomb.cs");

            StringAssert.Contains("ship, CommandOutcomeId);", explosion);
            StringAssert.Contains("ContactedShip, CommandOutcomeId);", strikerBomb);
        }
    }
}
