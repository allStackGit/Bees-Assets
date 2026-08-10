
using Assets.Scripts.Data;
using Assets.Scripts.Levels.Commands;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Beehive : Ship
    {
        public HashSet<Ship> ShipsHealingHere = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
        public Collider2D HealCollider;
        /// <summary>
        /// The Beehive has the shrinking and warping animation as it's "explosion" animation and the end of that animation triggers an actual explosion animation
        /// </summary>
        public GameObject ShrinkingAnimation;
        public GameObject HealingCrossPrefab;
        public List<HealingCross> HealingCrosses = new List<HealingCross>();

        private GameObject _collidingThing;
        private Ship _collidingShip;
        private Heal _command;
        private bool _isDeathAnimationPending;

        protected override void OnTriggerEnter2D(Collider2D collider)
        {
            _collidingThing = collider.gameObject;

            if (_collidingThing.CompareTag("Ship") && HealCollider.IsTouching(collider))
            {
                _collidingShip = collider.GetComponent<Ship>();
                if (ShipsHealingHere.Contains(_collidingShip) && _collidingShip.Squad.GetCommand() is Heal healCommand)
                {
                    _command = healCommand;
                    _command.ShipReachedBeehive(_collidingShip);
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
            _isDeathAnimationPending = false;
        }
        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            HealingCrosses.ToList().ForEach((c) => c.BeehiveKill()); // the ToList() is needed to avoid modifying the collection while killing the crosses
            if (Level.State.GetBeeShips().Where((s) => s.IsBeehive).Count() == 1) // check if this is the last beehive
            {
                Level.State.HasBeehives = false;
            }
            ShipsHealingHere.ToList().ForEach((s) =>
            {
                if (s != null && s.Squad.GetCommand() is Heal healCommand && healCommand.IsShipActivelyHealing(s))
                {
                    s.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
                }
            });
            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }
        protected override void DropExplosionAnimation()
        {
            if (!Stage.IsTraining)
            {
                _isDeathAnimationPending = true;
                ShrinkingAnimation.transform.SetParent(Level.Map.Transform);
                ShrinkingAnimation.transform.localPosition = GetPosition();
                ShrinkingAnimation.SetActive(true);
            }
        }

        public override bool CanReturnToPool()
        {
            return !_isDeathAnimationPending && base.CanReturnToPool();
        }

        public override void PrepareForLevelTeardown()
        {
            if (!_isDeathAnimationPending)
            {
                return;
            }

            _isDeathAnimationPending = false;
            if (ShrinkingAnimation != null)
            {
                ShrinkingAnimation.SetActive(false);
            }
        }

        public void FinalExplosion()
        {
            ShipExplosion.transform.parent = Level.Map.Transform;
            ShipExplosion.transform.localPosition = GetPosition();
            ShipExplosion.SetActive(true);

            if (Level.Stage.ActivateAudio && HasShipExplosionSoundEffect)
            {
                ShipExplosionSoundEffect.Play();
            }

            // The shrinking animation's delayed callback has completed and no longer needs
            // this pooled Beehive wrapper. It is safe for GameState to release it now.
            _isDeathAnimationPending = false;
        }

        public void SpawnHealingCross()
        {
            //Debug.Log($"Spawning healing cross for {Name} at {GetPosition()}");
            GameObject healingCrossObj = Instantiate(HealingCrossPrefab, transform.position, Quaternion.identity);
            healingCrossObj.transform.SetParent(Level.Map.Transform);
            healingCrossObj.transform.localPosition = Utilities.RandomCoordinate(Level, GetPosition(), new Vector2(16, 16), Vector2.zero);
            HealingCross healingCross = healingCrossObj.GetComponent<HealingCross>();
            healingCross.Setup(this);
            HealingCrosses.Add(healingCross);
        }

    }
}
