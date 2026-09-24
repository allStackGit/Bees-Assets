internal static class RlLiveTelemetryContract
{
    internal const int SchemaVersion = 1;
    internal const int ObservationSchemaVersion = 10;
    internal const int ActionSchemaVersion = 8;
    internal const int RewardSchemaVersion = 3;
    internal const int ScenarioSchemaVersion = 1;

    internal static void ValidateOrThrow()
    {
        RlPolicySchema.ValidateOrThrow();
        if (RlPolicySchema.Version != 18 ||
            RlPolicySchema.ExpectedObservationSize != 7342 ||
            RlPolicySchema.ExpectedContinuousActions != 16)
        {
            throw new System.InvalidOperationException(
                "Live telemetry contract metadata no longer matches the frozen RL policy ABI.");
        }
    }
}
