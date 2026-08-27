using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UIComponents
{
    public class DragIcon
    {
        public Vector2 Position => _icon.transform.position;
        public Vector2 WorkspaceOffset => _workspaceOffset;
        public bool HasWorkspaceOffset => _hasWorkspaceOffset;
        public int Id;

        private readonly GameObject _icon;
        private readonly FleetShip _fleetShip;
        private readonly SquadMaker _scene;
        private GameObject _deadShipBox;
        private int[] _changeablePixels;
        private Vector2 _workspaceOffset;
        private bool _hasWorkspaceOffset;

        public bool HasDeadShipBox => _deadShipBox != null;

        public DragIcon(SquadMaker scene, GameObject icon, FleetShip fleetShip, string gameObjectName, int id)
        {
            _scene = scene;
            _icon = icon;
            _fleetShip = fleetShip;
            icon.name = gameObjectName;
            Id = id;
            if (fleetShip.Side == ConfigData.Configuration.HumanSide)
            {
                SetChangablePixels(ConfigData.ChangeableShipColors.GetValueOrDefault(fleetShip.Type));
            }
        }

        public FleetShip GetFleetShip()
        {
            return _fleetShip;
        }

        public GameObject GetIcon()
        {
            return _icon;
        }

        public GameObject GetDeadShipBox()
        {
            return _deadShipBox;
        }

        public void SetPosition(Vector2 position)
        {
            _icon.transform.position = position;
        }

        public void SetWorkspaceOffset(Vector2 offset)
        {
            _workspaceOffset = offset;
            _hasWorkspaceOffset = true;
        }

        public void SetChangablePixels(Color[] colors)
        {
            Image image = _icon.GetComponent<Image>();
            _changeablePixels = Utilities.GetChangablePixelsForImage(colors, image.sprite);
        }

        public void SetColor(Color color)
        {
            Image image = _icon.GetComponent<Image>();
            if (color.Equals(ConfigData.UnsetColor))
            {
                CarrierDeckVariants.SetUiDeckVariant(image, null);
                return;
            }

            image.sprite = Utilities.SetImageColor(color, image.sprite, _changeablePixels);
            Sprite deckSprite = _fleetShip != null && _fleetShip.Type == ConfigData.ShipTypes.Carrier
                ? CarrierDeckVariants.GetDeckSprite(color)
                : null;
            CarrierDeckVariants.SetUiDeckVariant(image, deckSprite);
        }

        public void SetDeadShipBox(GameObject deadShipBox)
        {
            _deadShipBox = deadShipBox;
        }

        /// <summary>
        /// Compatibility entry point used by SquadMaker load/resize code. Saved ships already carry
        /// the canonical world offset. Resize repair must reuse an icon's canonical offset rather
        /// than converting its current screen pixels back into gameplay coordinates.
        /// </summary>
        public void Reposition(Vector2 position, SquadShip ship)
        {
            Dropper dropper = _scene.GetDropper();
            dropper.SetCurrentDragIcon(this);
            SquadShip self = ship ?? GetCurrentSquadShip();

            if (ship != null)
            {
                dropper.PlaceShipAtWorldOffset(ship.Offset, self);
            }
            else if (_hasWorkspaceOffset)
            {
                dropper.PlaceShipAtWorldOffset(_workspaceOffset, self);
            }
            else
            {
                dropper.PlaceShipAtPosition(position, self);
            }

            _scene.FleetDragEnd();
        }

        public void RepositionWorldOffset(Vector2 worldOffset, SquadShip ship)
        {
            Dropper dropper = _scene.GetDropper();
            dropper.SetCurrentDragIcon(this);
            dropper.PlaceShipAtWorldOffset(worldOffset, ship ?? GetCurrentSquadShip());
            _scene.FleetDragEnd();
        }

        public void RemoveDragIcon()
        {
            SavedSquad currentSquad = _scene.GetCurrentSquad();
            FleetShip fleetShip = GetFleetShip();
            if (currentSquad != null)
            {
                SquadShip squadShip = currentSquad.GetShip(fleetShip.Id);
                if (squadShip != null)
                {
                    currentSquad.RemoveShipFromSquad(squadShip, true);
                    _scene.UpdateShipCounter(fleetShip.Type);
                }
            }

            _scene.GetDropper().RemoveDragIcon(this);
        }

        public bool Equals(DragIcon dragIcon)
        {
            return dragIcon != null && dragIcon.Id == Id;
        }

        private SquadShip GetCurrentSquadShip()
        {
            SavedSquad currentSquad = _scene != null ? _scene.GetCurrentSquad() : null;
            return currentSquad != null && _fleetShip != null
                ? currentSquad.GetShip(_fleetShip.Id)
                : null;
        }
    }
}
