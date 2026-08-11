using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class HiveMindTrainingBootstrapTests
    {
        private GameObject _stageObject;
        private Component _stage;
        private Type _bootstrapType;

        [SetUp]
        public void SetUp()
        {
            _stageObject = new GameObject(nameof(HiveMindTrainingBootstrapTests));
            _stage = _stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            ((Behaviour)_stage).enabled = false;
            _bootstrapType = RuntimeAssembly.GetType("HiveMindTrainingBootstrap");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_stageObject);
        }

        [Test]
        public void ApplyRestoresHistoricallyAuthoredTrainingConfiguration()
        {
            RuntimeAssembly.SetField(_stage, "IsTrainingHiveMind", false);
            RuntimeAssembly.SetField(_stage, "IsTrainingNueralNetwork", true);
            RuntimeAssembly.SetField(_stage, "ActivateHiveMind", false);
            RuntimeAssembly.SetField(_stage, "DoesUserHaveController", true);
            RuntimeAssembly.SetField(_stage, "UseFullyRandomSquads", false);
            RuntimeAssembly.SetField(_stage, "IsRendering", true);
            RuntimeAssembly.SetField(_stage, "LevelCount", 1);
            RuntimeAssembly.SetField(_stage, "TimeoutTime", 0);
            RuntimeAssembly.SetField(_stage, "InitialCommandDelay", 0);

            ApplyBootstrap();

            Assert.That(RuntimeAssembly.GetField(_stage, "IsTrainingHiveMind"), Is.True);
            Assert.That(RuntimeAssembly.GetField(_stage, "IsTrainingNueralNetwork"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "ActivateHiveMind"), Is.True);
            Assert.That(RuntimeAssembly.GetField(_stage, "DoesUserHaveController"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "UseFullyRandomSquads"), Is.True);
            Assert.That(RuntimeAssembly.GetField(_stage, "IsRendering"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "LevelCount"), Is.EqualTo(16));
            Assert.That(RuntimeAssembly.GetField(_stage, "TimeoutTime"), Is.EqualTo(420));
            Assert.That(RuntimeAssembly.GetField(_stage, "InitialCommandDelay"), Is.EqualTo(1));
        }

        [Test]
        public void ApplyUsesCompletePrimaryFleetRatherThanProfileUnlocks()
        {
            ApplyBootstrap();

            CollectionAssert.AreEquivalent(
                new[]
                {
                    "Beehive", "Bumblebee", "CarpenterBee", "Honeybee", "Hornet",
                    "Leafcutter", "Queen", "Wasp", "YellowJacket"
                },
                EnumNames(RuntimeAssembly.GetField(_stage, "OverrideBeeShipTypes")));

            CollectionAssert.AreEquivalent(
                new[]
                {
                    "Barge", "Carrier", "Cruiser", "Dreadnought", "Factory", "FireBarge",
                    "Flagship", "Frigate", "Gunship", "Scout", "WarpGate"
                },
                EnumNames(RuntimeAssembly.GetField(_stage, "OverrideHumanShipTypes")));
        }

        [Test]
        public void ApplyPreservesButDeactivatesLegacySetupUiAcrossEpisodes()
        {
            GameObject uiManager = new GameObject("Training UI Manager");
            GameObject actionBoxChild = new GameObject("Action Box Child");
            GameObject unrelatedUi = new GameObject("Unrelated Training UI");
            try
            {
                actionBoxChild.transform.SetParent(uiManager.transform);
                RuntimeAssembly.SetField(_stage, "UIManager", uiManager);
                RuntimeAssembly.SetField(_stage, "UIElements", new List<GameObject>
                {
                    uiManager,
                    actionBoxChild,
                    unrelatedUi
                });

                ApplyBootstrap();

                Assert.That(uiManager.activeSelf, Is.False,
                    "Legacy setup UI should survive but remain inactive during headless training.");
                List<GameObject> remainingUi = (List<GameObject>)RuntimeAssembly.GetField(_stage, "UIElements");
                CollectionAssert.AreEquivalent(new[] { unrelatedUi }, remainingUi,
                    "The training teardown list must not destroy the UI hierarchy reused by later SetupLevel calls.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(unrelatedUi);
                UnityEngine.Object.DestroyImmediate(actionBoxChild);
                UnityEngine.Object.DestroyImmediate(uiManager);
            }
        }

        private void ApplyBootstrap()
        {
            MethodInfo apply = _bootstrapType.GetMethod(
                "Apply",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(apply, Is.Not.Null);
            apply.Invoke(null, new object[] { _stage });
        }

        private static string[] EnumNames(object collection)
        {
            return ((IEnumerable)collection).Cast<object>().Select(value => value.ToString()).ToArray();
        }
    }
}
