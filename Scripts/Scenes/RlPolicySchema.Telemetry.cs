/// <summary>
/// Frozen compatibility metadata used by live telemetry and its authenticated server contract.
/// Keep these values tied to the same policy ABI rather than duplicating anonymous literals at
/// capture/upload call sites.
/// </summary>
internal static partial class RlPolicySchema
{
    internal const int ObservationSize = ExpectedObservationSize;
    internal const int ObservationSchemaVersion = RlLiveTelemetryContract.ObservationSchemaVersion;
    internal const int ActionSchemaVersion = RlLiveTelemetryContract.ActionSchemaVersion;
    internal const int RewardSchemaVersion = RlLiveTelemetryContract.RewardSchemaVersion;
    internal const int ScenarioSchemaVersion = RlLiveTelemetryContract.ScenarioSchemaVersion;
}
