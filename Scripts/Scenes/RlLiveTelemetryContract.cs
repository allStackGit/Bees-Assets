internal static class RlLiveTelemetryContract
{
    internal const int SchemaVersion = 1;
    internal const int ObservationSchemaVersion = 12;
    internal const int ActionSchemaVersion = 9;
    internal const int RewardSchemaVersion = 4;
    internal const int ScenarioSchemaVersion = 1;

    internal static void ValidateOrThrow()
    {
        RlPolicySchema.ValidateOrThrow();
        if (RlPolicySchema.Version != 21 ||
            RlPolicySchema.ExpectedObservationSize != 7614 ||
            RlPolicySchema.ExpectedContinuousActions != 16)
        {
            throw new System.InvalidOperationException(
                "Live telemetry contract metadata no longer matches the frozen RL policy ABI.");
        }
    }
}
