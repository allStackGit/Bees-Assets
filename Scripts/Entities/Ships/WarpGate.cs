using Assets.Scripts.Data;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Levels;
using System.Collections.Generic;

using UnityEngine;
using static Assets.Scripts.ConfigData;

namespace Assets.Scripts.Entities.Ships
{
    public class WarpGate : Ship
    {
        public HashSet<long> ShipsWarpingHere = new HashSet<long>();
        public Collider2D WarpCollider;
        public AudioSource EnteringWarpGateSound, WarpGateStartingSound, WarpGateLoopingSound;
        public bool IsAudioLoaded;
        public override void ClearData()
        {
            base.ClearData();
            ShipsWarpingHere.Clear();

            // ClearData runs on every pooled Setup. These AudioSources are children of
            // the pooled Warp Gate and survive deactivation, so create them only once.
            if (Stage.ActivateAudio && !IsAudioLoaded)
            {
                EnteringWarpGateSound = Instantiate(Stage.Audio.EnteringWarpGateSound);
                EnteringWarpGateSound.transform.parent = transform;
                EnteringWarpGateSound.transform.localPosition = Vector2.zero;

                WarpGateStartingSound = Instantiate(Stage.Audio.WarpGateStartingSound);
                WarpGateStartingSound.transform.parent = transform;
                WarpGateStartingSound.transform.localPosition = Vector2.zero;

                WarpGateLoopingSound = Instantiate(Stage.Audio.WarpGateLoopingSound);
                WarpGateLoopingSound.transform.parent = transform;
                WarpGateLoopingSound.transform.localPosition = Vector2.zero;

                IsAudioLoaded = true;
            }

            ShipAnimationController.Setup();
        }

        private GameObject _collidingThing;
        private Ship _collidingShip;
        private FullRetreat _command;
        public override void Setup(Level level, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter)
        {
            base.Setup(level, fleetShip, squad, offsetFromCenter);

            // Warp Gates are mobile ships. Keep the shared capability flag explicit so generic
            // controller discovery (including the RL policy adapter) never mistakes one for a
            // stationary object because of stale or incomplete authored ship data.
            IsMobile = true;
            ShipAnimationController.Deactivate();
            
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
                        // FullRetreat owns per-command participant identity and deduplicates
                        // repeated collider entries before the gate is ready to warp.
                        _command.QueueShipForWarp(_collidingShip);
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
            int liveWarpGates = 0;
            List<Ship> levelShips = Level.State.Ships;
            for (int i = 0; i < levelShips.Count; i++)
            {
                Ship ship = levelShips[i];
                if (!ship.IsDead && ship.Side == ConfigData.Configuration.HumanSide && ship.IsWarpGate)
                {
                    liveWarpGates++;
                    if (liveWarpGates > 1)
                    {
                        break;
                    }
                }
            }
            if (liveWarpGates == 1)
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