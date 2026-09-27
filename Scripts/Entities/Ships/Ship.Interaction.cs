using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship
    {
        private static int _lastEnemyRightClickFrame = -1;
        private static int _lastEnemyRightClickSquadItemId = int.MinValue;
        private static int _lastEnemyRightClickPlayerId = MatchSession.UnownedPlayerId;

        protected virtual void OnTriggerEnter2D(Collider2D collider)
        {
            _tempCollidingThing = collider.gameObject;
            if (_tempCollidingThing.name == "Selection Box" && IsUserControlled)
            {
                Stage.Selector.SelectShip(this);
            }
        }

        protected virtual void OnTriggerExit2D(Collider2D collider)
        {
            _tempCollidingThing = collider.gameObject;
            if (_tempCollidingThing.name == "Selection Box" && IsUserControlled)
            {
                Stage.Selector.DeselectShip(this);
            }
        }

        public void Clicked(int mouseButton, bool isCtrlClick = false,
            int playerId = MatchSession.UnownedPlayerId)
        {
            if (playerId == MatchSession.UnownedPlayerId)
            {
                playerId = Level.State.GetPrimaryInputPlayerId();
            }
            int playerSide;
            if (Stage.MatchSession == null)
            {
                playerSide = ConfigData.Configuration.UserSide;
            }
            else
            {
                playerSide = Stage.MatchSession.GetPlayerSide(playerId);
                if (playerSide != ConfigData.Configuration.BeeSide &&
                    playerSide != ConfigData.Configuration.HumanSide)
                {
                    return;
                }
            }
            bool isFriendlyToPlayer = Side == playerSide;

            if (!isFriendlyToPlayer && mouseButton == LevelInputManager.RightClick)
            {
                // LevelInputManager can resolve the same right-click twice in one frame: once
                // through its proximity fallback and again through the normal clicked-ship path.
                // The two resolutions can even use different ships from the same enemy squad, so
                // dedupe by target squad rather than by Ship instance.
                int targetSquadItemId = Squad != null ? Squad.ItemId : int.MinValue;
                if (_lastEnemyRightClickFrame == Time.frameCount &&
                    _lastEnemyRightClickSquadItemId == targetSquadItemId &&
                    _lastEnemyRightClickPlayerId == playerId)
                {
                    return;
                }

                _lastEnemyRightClickFrame = Time.frameCount;
                _lastEnemyRightClickSquadItemId = targetSquadItemId;
                _lastEnemyRightClickPlayerId = playerId;

                // Do not perform a whole-map connectivity build here. Right-click is a main-thread
                // input path, and the previous reachability guard lazily flood-filled the complete
                // pathfinder grid on its first use, which could itself present as a hard freeze.
                // Composition-aware dispatch also lets Barge-only squads use their dedicated
                // Charge command instead of stopping in Aggressive's ranged positioning state.
                Level.State.GetSelectedSquadsForPlayer(playerId)
                    .ForEach(selectedSquad => Level.State.TryPlayerTargetEnemy(
                        playerId,
                        selectedSquad.CommandSquadId,
                        Squad.CommandSquadId));
            }
            else if (isFriendlyToPlayer && mouseButton == LevelInputManager.LeftClick && !Squad.IsImmobile)
            {
                if (isCtrlClick) Level.State.AddSelectedSquadForPlayer(playerId, Squad);
                else Level.State.SelectSquadForPlayer(playerId, Squad);
            }
        }
    }
}
