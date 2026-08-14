namespace Assets.Scripts
{
    public static partial class ConfigData
    {
        /// <summary>
        /// Development-only switch that suppresses authored dialogue presentation while preserving
        /// dialogue completion and break callbacks. Player-facing campaign builds keep this disabled
        /// so all authored intro and in-level dialogue is shown normally.
        /// </summary>
        public const bool SkipDialogue = false;
    }
}
