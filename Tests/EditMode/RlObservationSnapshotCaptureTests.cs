using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlObservationSnapshotCaptureTests
    {
        private Type _captureType;
        private MethodInfo _tryParseCommandLine;

        [SetUp]
        public void SetUp()
        {
            _captureType = RuntimeAssembly.GetType("RlObservationSnapshotCapture");
            _tryParseCommandLine = _captureType.GetMethod(
                "TryParseCommandLine",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(_tryParseCommandLine, Is.Not.Null);
        }

        [Test]
        public void CaptureIsDisabledWhenFlagIsAbsent()
        {
            ParseResult result = Parse("player.exe", "--rl-map-size", "30");

            Assert.That(result.Requested, Is.False);
            Assert.That(result.OutputPath, Is.Null);
            Assert.That(result.Error, Is.Null);
        }

        [Test]
        public void CaptureFlagEnablesContinuousCaptureWithProcessSpecificDefaultFile()
        {
            ParseResult result = Parse("player.exe", "--bees-rl-observation-snapshot");

            Assert.That(result.Requested, Is.True);
            Assert.That(result.Error, Is.Null);
            Assert.That(Path.GetFileName(result.OutputPath), Does.StartWith("rl-observation-snapshot-"));
            Assert.That(Path.GetExtension(result.OutputPath), Is.EqualTo(".json"));
        }

        [Test]
        public void CaptureIntervalIsTwentyObservations()
        {
            FieldInfo interval = _captureType.GetField(
                "CaptureInterval",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.That(interval, Is.Not.Null);
            Assert.That(interval.GetRawConstantValue(), Is.EqualTo(20));
        }

        [Test]
        public void OutputFlagSupportsEqualsAndSeparateValueForms()
        {
            ParseResult equalsResult = Parse(
                "player.exe",
                "--bees-rl-observation-snapshot",
                "--bees-rl-observation-snapshot-output=snapshots/one.json");
            ParseResult separateResult = Parse(
                "player.exe",
                "--bees-rl-observation-snapshot-output",
                "snapshots/two.json");

            Assert.That(equalsResult.Requested, Is.True);
            Assert.That(equalsResult.OutputPath, Is.EqualTo("snapshots/one.json"));
            Assert.That(equalsResult.Error, Is.Null);
            Assert.That(separateResult.Requested, Is.True);
            Assert.That(separateResult.OutputPath, Is.EqualTo("snapshots/two.json"));
            Assert.That(separateResult.Error, Is.Null);
        }

        [Test]
        public void MissingOutputPathFailsClearly()
        {
            ParseResult result = Parse("player.exe", "--bees-rl-observation-snapshot-output");

            Assert.That(result.Requested, Is.False);
            Assert.That(result.Error, Does.Contain("requires a path argument"));
        }

        private ParseResult Parse(params string[] args)
        {
            object[] invocationArgs = { args, null, null };
            bool requested = (bool)_tryParseCommandLine.Invoke(null, invocationArgs);
            return new ParseResult(requested, invocationArgs[1] as string, invocationArgs[2] as string);
        }

        private readonly struct ParseResult
        {
            internal ParseResult(bool requested, string outputPath, string error)
            {
                Requested = requested;
                OutputPath = outputPath;
                Error = error;
            }

            internal bool Requested { get; }
            internal string OutputPath { get; }
            internal string Error { get; }
        }
    }
}
