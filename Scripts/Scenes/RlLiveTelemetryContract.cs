internal static class RlLiveTelemetryContract
{
    internal const int SchemaVersion = 1;
    internal const int ObservationSchemaVersion = 13;
    internal const int ActionSchemaVersion = 9;
    internal const int RewardSchemaVersion = 5;
    internal const int ScenarioSchemaVersion = 1;

    internal static void ValidateOrThrow()
    {
        RlPolicySchema.ValidateOrThrow();
        if (RlPolicySchema.Version != 23 ||
            RlPolicySchema.ExpectedObservationSize != 7743 ||
            RlPolicySchema.ExpectedContinuousActions != 16)
        {
            throw new System.InvalidOperationException(
                "Live telemetry contract metadata no longer matches the frozen RL policy ABI.");
        }
    }
}
