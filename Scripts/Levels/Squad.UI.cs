using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Squad
    {
        public void MoveSquadBox()
        {
            if (!IsSelected || Stage.IsTraining || HasMovedBox || GetShips().Count == 0)
            {
                return;
            }

            SquadBox.SetActive(true);
            SquadBox.transform.localPosition = GetCenterPoint();
            SquadBox.transform.localScale = new Vector3(GetWidth() + 1, GetHeight() + 1, 0);
            if (HasCustomColor)
            {
                Utilities.SetUIColor(SquadBox, SquadBoxColor);
            }
            SquadBox.transform.eulerAngles = GetShips().Count == 1
                ? Vector3.forward * GetShips()[0].Rotation
                : Vector3.zero;
            HasMovedBox = true;
        }

        public void DeactivateSquadBox()
        {
            if (HasSquadBox && SquadBox != null)
            {
                SquadBox.SetActive(false);
            }
        }

        public void ShowSquadRanges()
        {
            GetShips().ForEach(ship => ship.ShowWeaponRanges());
            IsShowingRanges = true;
        }

        public void HideSquadRanges()
        {
            GetShips().ForEach(ship => ship.HideWeaponRanges());
            IsShowingRanges = false;
        }
    }
}