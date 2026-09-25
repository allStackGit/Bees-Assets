internal static class RlLiveTelemetryContract
{
    internal const int SchemaVersion = 1;
    internal const int ObservationSchemaVersion = 11;
    internal const int ActionSchemaVersion = 8;
    internal const int RewardSchemaVersion = 3;
    internal const int ScenarioSchemaVersion = 1;

    internal static void ValidateOrThrow()
    {
        RlPolicySchema.ValidateOrThrow();
        if (RlPolicySchema.Version != 19 ||
            RlPolicySchema.ExpectedObservationSize != 7614 ||
            RlPolicySchema.ExpectedContinuousActions != 16)
        {
            throw new System.InvalidOperationException(
                "Live telemetry contract metadata no longer matches the frozen RL policy ABI.");
        }
    }
}
