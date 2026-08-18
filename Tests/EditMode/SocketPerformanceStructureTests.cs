using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SocketPerformanceStructureTests
    {
        [Test]
        public void StandingRequestSnapshotsReuseOwnedStorage()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "Socket.cs"));

            StringAssert.Contains("private List<ServerRequest> SnapshotStandingRequests()", source);
            StringAssert.Contains("_standingRequests.Clear();", source);
            StringAssert.Contains("_standingRequests.AddRange(StandingRequests);", source);
            StringAssert.DoesNotContain("StandingRequests.ToList()", source);
            StringAssert.Contains("_waitableRequestSnapshot.Clear();", source);
            StringAssert.Contains("_waitableRequestSnapshot.AddRange(_waitableRequests);", source);
        }

        [Test]
        public void StandingRequestLookupUsesIndexedSetLookup()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "Socket.cs"));

            int start = source.IndexOf("public ServerRequest GetStandingRequest(long hash)");
            int end = source.IndexOf("private bool TryClaimResponse", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string method = source.Substring(start, end - start);

            StringAssert.Contains("StandingRequests.TryGetByHash(hash, out ServerRequest request)", method);
            StringAssert.DoesNotContain("FirstOrDefault", method);
            StringAssert.DoesNotContain("foreach", method);
        }

        [Test]
        public void ServerRequestTrackingAvoidsGenericHashSetComparers()
        {
            string standingSetSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "StandingRequestSet.cs"));
            string socketSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "Socket.cs"));
            string configSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "ConfigData.cs"));
            string resetSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Reset.cs"));

            StringAssert.Contains("Dictionary<long, ServerRequest> _requestsByHash", standingSetSource);
            StringAssert.Contains("ServerRequestSet _waitableRequests", socketSource);
            StringAssert.Contains("ServerRequestSet __PastServerRequests", configSource);
            StringAssert.DoesNotContain("HashSet<ServerRequest>", standingSetSource);
            StringAssert.DoesNotContain("HashSet<ServerRequest>", socketSource);
            StringAssert.DoesNotContain("HashSet<ServerRequest>", configSource);
            StringAssert.DoesNotContain("HashSet<ServerRequest>", resetSource);
            StringAssert.DoesNotContain(".ToHashSet()", resetSource);
        }

        [Test]
        public void StrategicTargetDiscoveryAvoidsLinqMaterialization()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "Socket.cs"));

            StringAssert.Contains("float closestWarpGateDistance = float.MaxValue;", source);
            StringAssert.Contains("if (!ship.IsWarpGate)", source);
            StringAssert.Contains("if (distance < closestWarpGateDistance)", source);
            StringAssert.DoesNotContain(".Where((s) => s.IsWarpGate)", source);

            StringAssert.Contains("private List<float> _beehiveDistances;", source);
            StringAssert.Contains("_beehives.Clear();", source);
            StringAssert.Contains("_beehiveDistances.Clear();", source);
            StringAssert.Contains("_beehives.Insert(insertionIndex, beehive);", source);
            StringAssert.Contains("_beehiveDistances.Insert(insertionIndex, distance);", source);
            StringAssert.DoesNotContain(".Select((s) => (Beehive)s)", source);
        }
    }
}
