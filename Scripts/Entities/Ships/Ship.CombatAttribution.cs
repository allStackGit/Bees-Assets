namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship
    {
        /// <summary>
        /// Stable Hive Mind outcome belonging to the external killer of an explosive ship.
        /// Fire Barge explosions can damage friendly ships after the killer has already
        /// finalized or changed commands, so chain-reaction reward must not consult the
        /// killer's current command at impact time.
        /// </summary>
        public long KillerCommandOutcomeId;
    }
}
