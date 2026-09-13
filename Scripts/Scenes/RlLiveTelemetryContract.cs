internal static class RlLiveTelemetryContract
{
    internal const int SchemaVersion = 1;
    internal const int ObservationSchemaVersion = 8;
    internal const int ActionSchemaVersion = 6;
    internal const int RewardSchemaVersion = 2;
    internal const int ScenarioSchemaVersion = 1;

    internal static void ValidateOrThrow()
    {
        RlPolicySchema.ValidateOrThrow();
        if (RlPolicySchema.Version != 8 ||
            RlPolicySchema.ExpectedObservationSize != 4722 ||
            RlPolicySchema.ExpectedContinuousActions != 34)
        {
            throw new System.InvalidOperationException(
                "Live telemetry contract metadata no longer matches the frozen RL policy ABI.");
        }
    }
}
