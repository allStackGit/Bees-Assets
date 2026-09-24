using NUnit.Framework;

public class RlTrainingControlRuntimeTests
{
    [TestCase(true, "training", false)]
    [TestCase(true, "inference", true)]
    [TestCase(true, "stopped", true)]
    [TestCase(false, "training", true)]
    public void ShouldForceInferenceMatchesLeaseAndDesiredMode(
        bool online,
        string desiredMode,
        bool expected)
    {
        Assert.That(
            RlTrainingControlRuntime.ShouldForceInference(online, desiredMode),
            Is.EqualTo(expected));
    }

    [Test]
    public void TryParseStateAcceptsManagedTrainingState()
    {
        bool parsed = RlTrainingControlRuntime.TryParseState(
            "{\"online\":true,\"desired_mode\":\"training\"}",
            out bool forceInference);

        Assert.That(parsed, Is.True);
        Assert.That(forceInference, Is.False);
    }

    [Test]
    public void TryParseStateFailsClosedForMalformedOrUnknownState()
    {
        Assert.That(
            RlTrainingControlRuntime.TryParseState(
                "{\"online\":true,\"desired_mode\":\"unknown\"}",
                out bool forceInference),
            Is.False);
        Assert.That(forceInference, Is.True);

        Assert.That(
            RlTrainingControlRuntime.TryParseState("not-json", out forceInference),
            Is.False);
        Assert.That(forceInference, Is.True);
    }
}
