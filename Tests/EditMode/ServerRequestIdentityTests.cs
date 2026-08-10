using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ServerRequestIdentityTests
    {
        [Test]
        public void CommandRequestRejectsDeadOrRecycledEnemyWrapper()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Server", "CommandRequest.cs"));

            Assert.That(source, Does.Contain("public readonly int EnemyId;"));
            Assert.That(source, Does.Contain("EnemyId = enemy != null ? enemy.ItemId : 0;"));
            Assert.That(source, Does.Contain("public Squad Enemy => _enemy != null && !_enemy.IsDead && _enemy.ItemId == EnemyId ? _enemy : null;"));
        }
    }
}
