namespace Assets.Scripts
{
    public static partial class ConfigData
    {
        /// <summary>
        /// When enabled, authored dialogue presentation is skipped while dialogue completion
        /// and break callbacks still execute. This is useful for development/testing without
        /// bypassing mission state that depends on dialogue progression.
        /// </summary>
        public const bool SkipDialogue = true;
    }
}
