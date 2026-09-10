using System;
using System.Reflection;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
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

            // EditMode tests must not inherit --rl-* command-line arguments from the Unity
            // process that happens to be hosting the Test Runner. Tests that exercise parsing
            // pass their own argument arrays directly and therefore remain unaffected.
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
