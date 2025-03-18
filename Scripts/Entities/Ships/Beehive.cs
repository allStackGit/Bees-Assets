
using Assets.Scripts.Data;
using Assets.Scripts.Levels.Commands;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Beehive : Ship
    {
        public HashSet<Ship> ShipsHealingHere = new HashSet<Ship>();
        public Collider2D HealCollider;
        /// <summary>
        /// The Beehive has the shrinking and warping animation as it's "explosion" animation and the end of that animation triggers an actual explosion animation
        /// </summary>
        public GameObject ShrinkingAnimation;

        private GameObject _collidingThing;
        private Ship _collidingShip;
        private Heal _command;

        protected override void OnTriggerEnter2D(Collider2D collider)
        {
            _collidingThing = collider.gameObject;

            if (_collidingThing.CompareTag("Ship") && HealCollider.IsTouching(collider))
            {
                _collidingShip = collider.GetComponent<Ship>();
                if (ShipsHealingHere.Contains(_collidingShip))
                {
                    _command = (Heal)_collidingShip.Squad.GetCommand();
                    _command.ShipsHealing.Add(_collidingShip);
                    //_command.ShipsWaitingToHeal.Remove(_collidingShip);

                    if (!_command.IsHealing)
                    {
                        _command.StartHealingTimer();
                    }
                }
            }
            else if (IsUserControlled && _collidingThing.name == "Selection Box")
            {
                Stage.Selector.SelectShip(this);
            }
        }
        public override void ClearData()
        {
            base.ClearData();
            ShipsHealingHere.Clear();
        }
        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            if (Level.State.GetBeeShips().Where((s) => s.IsBeehive).Count() == 1) // check if this is the last beehive
            {
                Level.State.HasBeehives = false;
            }
            ShipsHealingHere.ToList().ForEach((s) =>
            {
                s.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
            });
            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }
        protected override void DropExplosionAnimation()
        {
            if (!Stage.IsTraining)
            {
                ShrinkingAnimation.transform.SetParent(Level.Map.transform);
                ShrinkingAnimation.transform.localPosition = GetPosition();
                ShrinkingAnimation.SetActive(true);
            }
        }

        public void FinalExplosion()
        {
            ShipExplosion.transform.parent = Level.Map.transform;
            ShipExplosion.transform.localPosition = GetPosition();
            ShipExplosion.SetActive(true);

            if (Level.Stage.ActivateAudio && HasShipExplosionSoundEffect)
            {
                ShipExplosionSoundEffect.Play();
            }

        }

    }
}