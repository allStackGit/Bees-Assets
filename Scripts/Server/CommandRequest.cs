

using Assets.Scripts.Levels;

namespace Assets.Scripts.Server
{
    public class CommandRequest : ServerRequest
    {
        public readonly Squad Squad;
        private readonly Squad _enemy;
        public readonly Level Level;
        public readonly string Matchup;
        public readonly int SquadId;
        public readonly int EnemyId;
        public Squad Enemy => _enemy != null && !_enemy.IsDead && _enemy.ItemId == EnemyId ? _enemy : null;

        public new GetStrategy Request = null;
        public CommandResponse Response = null;
        public CommandRequest(GetStrategy request, Squad squad, Squad enemy, Level level, string matchup, int maxTimeOnQueue) : base(maxTimeOnQueue)
        {
            Type = ConfigData.RequestTypes.GetStrategy;
            Request = request;
            Squad = squad;
            _enemy = enemy;
            Level = level;
            Matchup = matchup;
            Squad.Status = $"Requesting full command";
            request.Type = Utilities.ConvertRequestTypeToName[Type];
            request.Hash = Hash;
            SquadId = Squad.ItemId;
            EnemyId = enemy != null ? enemy.ItemId : 0;

            // Strategic Matchup intentionally contains nearby allied ships in its first segment.
            // Shooting learning must identify only the acting squad and enemies; reuse the enemy
            // segment already selected by MakeMatchupAndGetCommand while rebuilding the friendly
            // segment from this request's acting squad alone.
            string[] matchupSegments = (matchup ?? string.Empty).Split('|');
            string enemySegment = matchupSegments.Length > 1 ? matchupSegments[1] : string.Empty;
            request.ShootingMatchup = $"{Squad.AddToMatchup(Squad.GetShipsForMatchup())}|{enemySegment}";
        }
        public bool HasSameSquad()
        {
            return Squad != null && SquadId == Squad.ItemId && !Squad.IsDead;
        }
    }
}
