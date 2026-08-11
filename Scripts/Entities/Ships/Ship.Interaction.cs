using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship
    {
        private static int _lastEnemyRightClickFrame = -1;
        private static int _lastEnemyRightClickSquadItemId = int.MinValue;

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

        public void Clicked(int mouseButton, bool isCtrlClick = false)
        {
            if (!IsUserControlled && mouseButton == LevelInputManager.RightClick)
            {
                // LevelInputManager can resolve the same right-click twice in one frame: once
                // through its proximity fallback and again through the normal clicked-ship path.
                // The two resolutions can even use different ships from the same enemy squad, so
                // dedupe by target squad rather than by Ship instance.
                int targetSquadItemId = Squad != null ? Squad.ItemId : int.MinValue;
                if (_lastEnemyRightClickFrame == Time.frameCount &&
                    _lastEnemyRightClickSquadItemId == targetSquadItemId)
                {
                    return;
                }

                _lastEnemyRightClickFrame = Time.frameCount;
                _lastEnemyRightClickSquadItemId = targetSquadItemId;
                Level.State.GetSelectedSquads().ForEach(selectedSquad => selectedSquad.UserAggressive(Squad));
            }
            else if (IsUserControlled && mouseButton == LevelInputManager.LeftClick && !Squad.IsImmobile)
            {
                if (isCtrlClick) Level.State.AddSelectedSquad(Squad);
                else Level.State.SelectSquad(Squad);
            }
        }
    }
}
