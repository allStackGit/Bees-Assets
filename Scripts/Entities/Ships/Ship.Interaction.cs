using Assets.Scripts.Levels;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship
    {
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
