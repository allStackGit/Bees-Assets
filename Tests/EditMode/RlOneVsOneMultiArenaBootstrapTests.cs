using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlOneVsOneMultiArenaBootstrapTests
    {
        [Test]
        public void ConfinementConvertsArenaLocalPositionToWorldSpaceForOffsetArena()
        {
            GameObject arena = new GameObject("RL arena");
            GameObject ship = new GameObject("RL ship");

            try
            {
                arena.transform.position = new Vector3(384f, -192f, 0f);
                ship.transform.SetParent(arena.transform, false);

                Type bootstrapType = RuntimeAssembly.GetType("RlOneVsOneMultiArenaBootstrap");
                MethodInfo convertPosition = bootstrapType.GetMethod(
                    "GetWorldPositionForLocalShipPosition",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(convertPosition, Is.Not.Null);

                Vector3 localPosition = new Vector3(23f, -17f, 0f);
                Vector2 actual = (Vector2)convertPosition.Invoke(
                    null,
                    new object[] { ship.transform, localPosition });
                Vector3 expected = arena.transform.TransformPoint(localPosition);

                Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
                Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
                Assert.That(actual, Is.Not.EqualTo((Vector2)localPosition),
                    "A non-primary arena must not write arena-local coordinates directly to Rigidbody2D.position.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(ship);
                UnityEngine.Object.DestroyImmediate(arena);
            }
        }

        [Test]
        public void MultiArenaBootstrapStaysEnabledAfterApplyingSoFixedUpdateCanConstrainShips()
        {
            string source = ReadSource("Scripts", "Scenes", "RlOneVsOneMultiArenaBootstrap.cs");
            int updateStart = source.IndexOf("private void Update()", StringComparison.Ordinal);
            int layoutStart = source.IndexOf("internal static Vector2[] BuildLayout", updateStart, StringComparison.Ordinal);
            Assert.That(updateStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(layoutStart, Is.GreaterThan(updateStart));

            string update = source.Substring(updateStart, layoutStart - updateStart);
            Assert.That(update, Does.Contain("_applied = true;"));
            Assert.That(update, Does.Not.Contain("enabled = false;"),
                "The multi-arena bootstrap must remain enabled because its FixedUpdate owns non-primary arena confinement.");
            Assert.That(source, Does.Contain("private void FixedUpdate()"));
            Assert.That(source, Does.Contain("for (int levelIndex = 1; levelIndex < levels.Count; levelIndex++)"));
        }

        [Test]
        public void TrainingCameraZoomIsDefaultedOnceAndNotForcedEveryLateUpdate()
        {
            string source = ReadSource("Scripts", "Scenes", "RlOneVsOneTrainingBootstrap.cs");
            int guardStart = source.IndexOf("internal sealed class RlOneVsOneTrainingRuntimeGuard", StringComparison.Ordinal);
            Assert.That(guardStart, Is.GreaterThanOrEqualTo(0));

            string guard = source.Substring(guardStart);
            const string assignment = "_stage.Camera.orthographicSize = RlOneVsOneTrainingBootstrap.CurrentCameraSize;";
            int initializationCheck = guard.IndexOf("if (!_cameraInitialized)", StringComparison.Ordinal);
            int zoomAssignment = guard.IndexOf(assignment, StringComparison.Ordinal);
            int initialized = guard.IndexOf("_cameraInitialized = true;", StringComparison.Ordinal);

            Assert.That(guard, Does.Contain("private bool _cameraInitialized;"));
            Assert.That(initializationCheck, Is.GreaterThanOrEqualTo(0));
            Assert.That(zoomAssignment, Is.GreaterThan(initializationCheck));
            Assert.That(initialized, Is.GreaterThan(zoomAssignment));
            Assert.That(guard.LastIndexOf(assignment, StringComparison.Ordinal), Is.EqualTo(zoomAssignment),
                "The runtime guard should establish the training zoom once, not overwrite manual camera zoom changes every frame.");
        }

        private static string ReadSource(params string[] pathParts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < pathParts.Length; i++)
            {
                path = Path.Combine(path, pathParts[i]);
            }
            return File.ReadAllText(path);
        }
    }
}