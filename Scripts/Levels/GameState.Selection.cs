using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Entities.Ships;

namespace Assets.Scripts.Levels
{
    public partial class GameState
    {
        private readonly Dictionary<int, List<Squad>> _selectedSquadsByNonPrimaryPlayer =
            new Dictionary<int, List<Squad>>();

        public int GetPrimaryInputPlayerId()
        {
            MatchSession matchSession = Level != null && Level.Stage != null
                ? Level.Stage.MatchSession
                : null;
            return matchSession == null || matchSession.PrimaryLocalPlayerId == MatchSession.UnownedPlayerId
                ? MatchSession.LegacyLocalPlayerId
                : matchSession.PrimaryLocalPlayerId;
        }

        public bool IsKnownInputPlayer(int playerId)
        {
            if (playerId <= MatchSession.UnownedPlayerId)
            {
                return false;
            }

            MatchSession matchSession = Level != null && Level.Stage != null
                ? Level.Stage.MatchSession
                : null;
            return matchSession == null
                ? playerId == MatchSession.LegacyLocalPlayerId
                : matchSession.HasPlayer(playerId);
        }

        private bool IsPrimaryInputPlayer(int playerId)
        {
            return playerId == GetPrimaryInputPlayerId();
        }

        private List<Squad> GetSelectionRegistry(int playerId)
        {
            if (IsPrimaryInputPlayer(playerId))
            {
                return SelectedSquads;
            }

            if (!_selectedSquadsByNonPrimaryPlayer.TryGetValue(playerId, out List<Squad> selectedSquads))
            {
                selectedSquads = new List<Squad>();
                _selectedSquadsByNonPrimaryPlayer.Add(playerId, selectedSquads);
            }
            return selectedSquads;
        }

        public bool IsSquadSelectedByPlayer(Squad squad, int playerId)
        {
            return IsKnownInputPlayer(playerId) && squad != null &&
                GetSelectionRegistry(playerId).Contains(squad);
        }

        public List<Squad> GetSelectedSquads()
        {
            return GetSelectedSquadsForPlayer(GetPrimaryInputPlayerId());
        }

        public List<Squad> GetSelectedSquadsForPlayer(int playerId)
        {
            if (!IsKnownInputPlayer(playerId))
            {
                return new List<Squad>();
            }

            return GetSelectionRegistry(playerId)
                .Where(squad => squad != null && !squad.IsDead && squad.CanAcceptInputFrom(playerId))
                .ToList();
        }

        public void AddSelectedSquad(Squad squad)
        {
            AddSelectedSquadForPlayer(GetPrimaryInputPlayerId(), squad);
        }

        public void AddSelectedSquadForPlayer(int playerId, Squad squad)
        {
            if (!IsKnownInputPlayer(playerId) || squad == null || !squad.CanBeSelectedByPlayer(playerId))
            {
                return;
            }

            List<Squad> selection = GetSelectionRegistry(playerId);
            selection.Add(squad);
            if (!IsPrimaryInputPlayer(playerId))
            {
                return;
            }

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
            SelectSquadsForPlayer(GetPrimaryInputPlayerId(), squads);
        }

        public void SelectSquadsForPlayer(int playerId, List<Squad> squads)
        {
            if (!IsKnownInputPlayer(playerId))
            {
                return;
            }

            ClearSelectedSquadsForPlayer(playerId);
            if (squads == null)
            {
                return;
            }
            for (int i = 0; i < squads.Count; i++)
            {
                AddSelectedSquadForPlayer(playerId, squads[i]);
            }
        }

        public void SelectSquadsByShipType(ConfigData.ShipTypes type)
        {
            SelectSquadsByShipTypeForPlayer(GetPrimaryInputPlayerId(), type);
        }

        public void SelectSquadsByShipTypeForPlayer(int playerId, ConfigData.ShipTypes type)
        {
            if (!IsKnownInputPlayer(playerId))
            {
                return;
            }

            ClearSelectedSquadsForPlayer(playerId);
            MatchSession matchSession = Level != null && Level.Stage != null
                ? Level.Stage.MatchSession
                : null;
            int side = matchSession == null
                ? ConfigData.Configuration.UserSide
                : matchSession.GetPlayerSide(playerId);
            if (side != ConfigData.Configuration.BeeSide && side != ConfigData.Configuration.HumanSide)
            {
                return;
            }

            foreach (Squad squad in GetSquadsBySide(side)
                         .Where(squad => squad.IsOwnedByPlayer(playerId) &&
                             squad.GetShips().Any(ship => ship.ShipType == type)))
            {
                AddSelectedSquadForPlayer(playerId, squad);
            }
        }

        public void ClearSelectedSquads()
        {
            ClearSelectedSquadsForPlayer(GetPrimaryInputPlayerId());
        }

        public void ClearSelectedSquadsForPlayer(int playerId)
        {
            if (!IsKnownInputPlayer(playerId))
            {
                return;
            }

            List<Squad> selection = GetSelectionRegistry(playerId);
            while (selection.Count > 0)
            {
                DeselectSquadForPlayer(playerId, selection[0]);
            }
        }

        public void SelectSquad(Squad squad)
        {
            SelectSquadForPlayer(GetPrimaryInputPlayerId(), squad);
        }

        public void SelectSquadForPlayer(int playerId, Squad squad)
        {
            if (!IsKnownInputPlayer(playerId) || squad == null)
            {
                return;
            }
            ClearSelectedSquadsForPlayer(playerId);
            AddSelectedSquadForPlayer(playerId, squad);
        }

        public void DeselectSquad(Squad squad)
        {
            DeselectSquadForPlayer(GetPrimaryInputPlayerId(), squad);
        }

        public void DeselectSquadForPlayer(int playerId, Squad squad)
        {
            if (!IsKnownInputPlayer(playerId))
            {
                return;
            }

            List<Squad> selection = GetSelectionRegistry(playerId);
            if (squad == null || !selection.Contains(squad))
            {
                return;
            }

            selection.Remove(squad);
            if (!IsPrimaryInputPlayer(playerId))
            {
                return;
            }

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

        public void ForgetSquadSelectionForRelease(Squad squad)
        {
            if (squad == null)
            {
                return;
            }

            SelectedSquads.Remove(squad);
            foreach (List<Squad> selectedSquads in _selectedSquadsByNonPrimaryPlayer.Values)
            {
                selectedSquads.Remove(squad);
            }
            squad.IsSelected = false;
            HasSelectedSquads = SelectedSquads.Count > 0;
        }

        private void ResetPlayerSelectionState()
        {
            _selectedSquadsByNonPrimaryPlayer.Clear();
        }
    }
}
