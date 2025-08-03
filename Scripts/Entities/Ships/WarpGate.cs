using Assets.Scripts.Data;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;
using static Assets.Scripts.ConfigData;

namespace Assets.Scripts.Entities.Ships
{
    public class WarpGate : Ship
    {
        public HashSet<long> ShipsWarpingHere = new HashSet<long>();
        public Collider2D WarpCollider;
        public AudioSource EnteringWarpGateSound;
        public override void ClearData()
        {
            base.ClearData();
            ShipsWarpingHere.Clear();
        }

        private GameObject _collidingThing;
        private Ship _collidingShip;
        private FullRetreat _command;
        public override void Setup(Level level, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter)
        {
            base.Setup(level, fleetShip, squad, offsetFromCenter);
            ShipAnimationController.Deactivate();
            if (IsUserControlled && Stage.ActivateAudio)
            {
                EnteringWarpGateSound = Instantiate(Stage.Audio.EnteringWarpGateSound);
                EnteringWarpGateSound.transform.parent = transform;
                EnteringWarpGateSound.transform.localPosition = Vector2.zero;
            }
        }
        protected override void OnTriggerEnter2D(Collider2D collider)
        {
            _collidingThing = collider.gameObject;
            
            if (_collidingThing.CompareTag("Ship") && WarpCollider.IsTouching(collider))
            {
                _collidingShip = collider.GetComponent<Ship>();
                if (_collidingShip.Side == Side && _collidingShip.Squad?.GetCommand()?.CommandType == ConfigData.CommandTypes.FullRetreat && _collidingShip.ShipType != this.ShipType)
                {
                    _command = (FullRetreat)_collidingShip.Squad.GetCommand();
                    if (_command.TargetWarpGate == this)
                    {
                        //Debug.Log($"{ship.Name} hit {Name} and so we're warping it");
                        _command.ShipsWaitingToWarp.Add(_collidingShip);
                        _command.WaitToWarp();
                    }
                }
            }
            else if (IsUserControlled && _collidingThing.name == "Selection Box")
            {
                Stage.Selector.SelectShip(this);
            }
        }

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            if (Level.State.GetHumanShips().Where((s) => s.IsWarpGate).Count() == 1) // check if this is the last warp gate
            {
                Level.State.HasWarpGates = false;
            }
            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }
        public override void Activate()
        {
            ShipAnimationController.Activate();
            //WarpCollider.enabled = true;
            base.Activate();
        }
        public override void Deactivate()
        {
            ShipAnimationController.Deactivate();
            //WarpCollider.enabled = false;
            base.Deactivate();
        }
    }
}