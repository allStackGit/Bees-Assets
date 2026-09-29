using System;
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
        public void SecondaryArenaConfinementCapsVelocityBeforeTheNextPhysicsStep()
        {
            Type bootstrapType = RuntimeAssembly.GetType("RlOneVsOneMultiArenaBootstrap");
            MethodInfo clamp = bootstrapType.GetMethod(
                "ClampProjectedVelocityToArena",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(clamp, Is.Not.Null);

            Vector2 position = new Vector2(9.5f, 0f);
            Vector2 velocity = new Vector2(10f, 0f);
            Vector2 constrained = (Vector2)clamp.Invoke(
                null,
                new object[] { position, velocity, -10f, 10f, -10f, 10f, 0.1f });

            Assert.That(constrained.x, Is.EqualTo(5f).Within(0.0001f),
                "Secondary arenas must stop at the boundary instead of overshooting and snapping back next tick.");
            Assert.That(constrained.y, Is.Zero);
            Assert.That(position.x + constrained.x * 0.1f, Is.EqualTo(10f).Within(0.0001f));
        }

    }
}