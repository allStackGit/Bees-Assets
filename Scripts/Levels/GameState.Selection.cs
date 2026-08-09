using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Entities.Ships;

namespace Assets.Scripts.Levels
{
    public partial class GameState
    {
        public List<Squad> GetSelectedSquads()
        {
            return SelectedSquads.Where(squad => squad != null).ToList();
        }

        public void AddSelectedSquad(Squad squad)
        {
            if (!squad.CanBeSelected())
            {
                return;
            }

            SelectedSquads.Add(squad);
            squad.IsSelected = true;
            squad.MoveSquadBox();
            if (Level.Stage.Menus.HasSquadActionBox)
            {
                Stage.Menus.ActionBox.SetupForSquad();
            }
            squad.GetShips().ForEach(ship =>
            {
                if (ship.HasTargetCoordinates)
                {
                    ship.MovementMarker.SetActive(true);
                }
            });
            if (squad.HasSquadTab)
            {
                squad.SquadTab.ShowSelected();
            }
            HasSelectedSquads = true;
        }

        public void SelectSquads(List<Squad> squads)
        {
            ClearSelectedSquads();
            squads.ForEach(AddSelectedSquad);
        }

        public void SelectSquadsByShipType(ConfigData.ShipTypes type)
        {
            ClearSelectedSquads();
            foreach (Squad squad in GetSquadsBySide(ConfigData.Configuration.UserSide)
                         .Where(squad => squad.GetShips().Any(ship => ship.ShipType == type)))
            {
                AddSelectedSquad(squad);
            }
        }

        public void ClearSelectedSquads()
        {
            while (SelectedSquads.Count > 0)
            {
                DeselectSquad(SelectedSquads[0]);
            }
        }

        public void SelectSquad(Squad squad)
        {
            if (squad == null)
            {
                return;
            }
            ClearSelectedSquads();
            AddSelectedSquad(squad);
        }

        public void DeselectSquad(Squad squad)
        {
            squad.DeactivateSquadBox();
            squad.IsSelected = false;
            squad.GetShips().ForEach(ship =>
            {
                if (ship.IsMobile)
                {
                    ship.MovementMarker.SetActive(false);
                }
            });
            if (squad.HasSquadTab)
            {
                squad.SquadTab.HideSelected();
            }
            SelectedSquads.Remove(squad);

            if (SelectedSquads.Count == 0)
            {
                HasSelectedSquads = false;
                if (Level.Stage.Menus.HasSquadActionBox)
                {
                    Stage.Menus.ActionBox.Hide();
                }
            }
            else if (Level.Stage.Menus.HasSquadActionBox)
            {
                Stage.Menus.ActionBox.SetupForSquad();
            }
        }
    }
}
