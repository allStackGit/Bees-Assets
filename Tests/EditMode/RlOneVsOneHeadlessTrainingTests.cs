using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlOneVsOneHeadlessTrainingTests
    {
        private Type _bootstrapType;
        private MethodInfo _shouldRenderTraining;

        [SetUp]
        public void SetUp()
        {
            _bootstrapType = RuntimeAssembly.GetType("RlOneVsOneTrainingBootstrap");
            _shouldRenderTraining = _bootstrapType.GetMethod(
                "ShouldRenderTraining",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(_shouldRenderTraining, Is.Not.Null);
        }

        [Test]
        public void InteractiveEditorAndStandalonePlayersKeepTrainingRenderingEnabled()
        {
            Assert.That(ShouldRender(true, true, GraphicsDeviceType.Null), Is.True,
                "Editor training should remain visually inspectable even when tests launch the Editor headlessly.");
            Assert.That(ShouldRender(false, false, GraphicsDeviceType.Direct3D11), Is.True,
                "An ordinary graphical standalone player should preserve the visible training scene.");
        }

        [Test]
        public void StandaloneBatchOrNoGraphicsPlayersDisableTrainingRendering()
        {
            Assert.That(ShouldRender(false, true, GraphicsDeviceType.Direct3D11), Is.False,
                "Standalone batch-mode training should skip presentation work.");
            Assert.That(ShouldRender(false, false, GraphicsDeviceType.Null), Is.False,
                "ML-Agents --no-graphics training should skip presentation work when Unity exposes a null graphics device.");
        }

        [Test]
        public void BootstrapUsesRuntimeHeadlessSignalsForStageRendering()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Scenes",
                "RlOneVsOneTrainingBootstrap.cs"));

            Assert.That(source, Does.Contain(
                "stage.IsRendering = ShouldRenderTraining(Application.isEditor, Application.isBatchMode, SystemInfo.graphicsDeviceType);"));
        }

        private bool ShouldRender(bool isEditor, bool isBatchMode, GraphicsDeviceType graphicsDeviceType)
        {
            return (bool)_shouldRenderTraining.Invoke(
                null,
                new object[] { isEditor, isBatchMode, graphicsDeviceType });
        }
    }
}
