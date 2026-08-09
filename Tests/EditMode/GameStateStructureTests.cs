using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class GameStateStructureTests
    {
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Application.dataPath, "Scripts", "Levels");
        }

        [Test]
        public void GameStateResponsibilitiesLiveInFocusedPartials()
        {
            string core = File.ReadAllText(Path.Combine(_folder, "GameState.cs"));
            string registry = File.ReadAllText(Path.Combine(_folder, "GameState.Registry.cs"));
            string queries = File.ReadAllText(Path.Combine(_folder, "GameState.Queries.cs"));
            string selection = File.ReadAllText(Path.Combine(_folder, "GameState.Selection.cs"));
            string commands = File.ReadAllText(Path.Combine(_folder, "GameState.Commands.cs"));

            StringAssert.Contains("public partial class GameState : MonoBehaviour", core);
            StringAssert.Contains("public void Release()", registry);
            StringAssert.Contains("public List<Ship> GetShips", queries);
            StringAssert.Contains("public void SelectSquad", selection);
            StringAssert.Contains("public void StoreCommands()", commands);
        }

        [Test]
        public void DeadShooterIsNotPoolableWhileItsProjectileIsStillActive()
        {
            string registry = File.ReadAllText(Path.Combine(_folder, "GameState.Registry.cs"));

            StringAssert.Contains("projectile.IsDead || projectile.Shooter != ship", registry);
            StringAssert.Contains("ship.ProjectilesInFlight.Count == 0", registry);
            StringAssert.Contains("ShipsToRelease.RemoveAll", registry);
        }
    }
}
