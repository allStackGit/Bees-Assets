using System;
using System.Reflection;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    /// <summary>
    /// EditMode regression tests must not inherit --rl-* arguments from the Unity Editor process.
    /// Tests that exercise option parsing pass their own explicit argument arrays directly; bootstrap
    /// consumers use the stable no-argument proof configuration unless a test deliberately overrides it.
    /// </summary>
    [SetUpFixture]
    public sealed class RlTrainingEnvironmentIsolation
    {
        private Type _bootstrapType;
        private object _previousRuntimeOptions;

        [OneTimeSetUp]
        public void InstallDefaultRuntimeOptions()
        {
            _bootstrapType = RuntimeAssembly.GetType("RlOneVsOneTrainingBootstrap");
            _previousRuntimeOptions = RuntimeAssembly.GetStaticField(_bootstrapType, "_runtimeOptions");

            Type optionsType = RuntimeAssembly.GetType("RlOneVsOneTrainingOptions");
            MethodInfo parse = optionsType.GetMethod(
                "Parse",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(parse, Is.Not.Null);

            object defaultOptions = parse.Invoke(null, new object[] { Array.Empty<string>() });
            RuntimeAssembly.SetStaticField(_bootstrapType, "_runtimeOptions", defaultOptions);
        }

        [OneTimeTearDown]
        public void RestoreRuntimeOptions()
        {
            if (_bootstrapType != null)
            {
                RuntimeAssembly.SetStaticField(_bootstrapType, "_runtimeOptions", _previousRuntimeOptions);
            }
        }
    }
}
