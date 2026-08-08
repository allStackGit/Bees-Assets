using System;
using System.Collections;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace Bees.Tests.PlayMode
{
    [TestFixture]
    [Category("BeesHardwareQualification")]
    public class HardwareQualificationTests
    {
        [Test]
        public void RecordQualificationHardwareAndRuntimeEnvironment()
        {
            string record =
                $"QUALIFICATION hardware os=\"{SystemInfo.operatingSystem}\" " +
                $"cpu=\"{SystemInfo.processorType}\" cores={SystemInfo.processorCount} " +
                $"ramMB={SystemInfo.systemMemorySize} gpu=\"{SystemInfo.graphicsDeviceName}\" " +
                $"gpuVendor=\"{SystemInfo.graphicsDeviceVendor}\" gpuMB={SystemInfo.graphicsMemorySize} " +
                $"graphicsApi={SystemInfo.graphicsDeviceType} resolution={Screen.currentResolution.width}x{Screen.currentResolution.height} " +
                $"unity={Application.unityVersion} batch={Application.isBatchMode}";
            UnityEngine.Debug.Log(record);

            Assert.That(SystemInfo.processorCount, Is.GreaterThan(0));
            Assert.That(SystemInfo.systemMemorySize, Is.GreaterThan(0));
            Assert.That(Application.unityVersion, Is.Not.Empty);
        }

        [UnityTest]
        public IEnumerator RepeatedRuntimeResetDoesNotShowMonotonicManagedOrNativeMemoryGrowth()
        {
            GameObject levelObject = new GameObject(nameof(HardwareQualificationTests) + " Level");
            object level = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            object state = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            RuntimeAssembly.SetField(level, "State", state);
            RuntimeAssembly.SetField(state, "Level", level);

            try
            {
                const int warmup = 2000;
                const int sampleIterations = 10000;
                for (int index = 0; index < warmup; index++)
                {
                    RuntimeAssembly.Invoke(state, "ResetState");
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                yield return null;

                long managedBefore = GC.GetTotalMemory(true);
                long nativeBefore = Profiler.GetTotalAllocatedMemoryLong();
                Stopwatch timer = Stopwatch.StartNew();
                for (int index = 0; index < sampleIterations; index++)
                {
                    RuntimeAssembly.Invoke(state, "ResetState");
                    if ((index & 1023) == 0)
                    {
                        yield return null;
                    }
                }
                timer.Stop();

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                yield return null;
                long managedAfter = GC.GetTotalMemory(true);
                long nativeAfter = Profiler.GetTotalAllocatedMemoryLong();
                long managedGrowth = managedAfter - managedBefore;
                long nativeGrowth = nativeAfter - nativeBefore;

                UnityEngine.Debug.Log(
                    $"QUALIFICATION memory resetIterations={sampleIterations} elapsedMs={timer.Elapsed.TotalMilliseconds:F2} " +
                    $"managedBefore={managedBefore} managedAfter={managedAfter} managedGrowth={managedGrowth} " +
                    $"nativeBefore={nativeBefore} nativeAfter={nativeAfter} nativeGrowth={nativeGrowth}");

                // This is deliberately a leak/regression tripwire, not an exact allocation budget.
                Assert.That(managedGrowth, Is.LessThan(8L * 1024 * 1024),
                    "Managed memory grew by more than 8 MiB across a warmed repeated-reset workload.");
                Assert.That(nativeGrowth, Is.LessThan(32L * 1024 * 1024),
                    "Native allocated memory grew by more than 32 MiB across a warmed repeated-reset workload.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(levelObject);
            }
        }
    }
}
