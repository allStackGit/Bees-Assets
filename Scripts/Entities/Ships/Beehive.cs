
using Assets.Scripts.Data;
using Assets.Scripts.Levels.Commands;
using System.Collections.Generic;
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

        private readonly List<HealingCross> _inactiveHealingCrosses = new List<HealingCross>();
        private readonly List<Ship> _shipsHealingSnapshot = new List<Ship>();
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

        private void ReleaseActiveHealingCrosses()
        {
            while (HealingCrosses.Count > 0)
            {
                HealingCross cross = HealingCrosses[HealingCrosses.Count - 1];
                if (cross == null)
                {
                    HealingCrosses.RemoveAt(HealingCrosses.Count - 1);
                    continue;
                }
                cross.BeehiveKill();
            }
        }

        public void RecycleHealingCross(HealingCross healingCross)
        {
            if (healingCross == null)
            {
                return;
            }
            healingCross.gameObject.SetActive(false);
            _inactiveHealingCrosses.Add(healingCross);
        }

        public override void ClearData()
        {
            ReleaseActiveHealingCrosses();
            base.ClearData();
            ShipsHealingHere.Clear();
            _isDeathAnimationPending = false;
        }

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            ReleaseActiveHealingCrosses();

            int liveBeehives = 0;
            List<Ship> levelShips = Level.State.Ships;
            for (int i = 0; i < levelShips.Count; i++)
            {
                Ship ship = levelShips[i];
                if (!ship.IsDead && ship.IsBeehive)
                {
                    liveBeehives++;
                    if (liveBeehives > 1)
                    {
                        break;
                    }
                }
            }
            if (liveBeehives == 1)
            {
                Level.State.HasBeehives = false;
            }

            _shipsHealingSnapshot.Clear();
            foreach (Ship ship in ShipsHealingHere)
            {
                _shipsHealingSnapshot.Add(ship);
            }
            for (int i = 0; i < _shipsHealingSnapshot.Count; i++)
            {
                Ship ship = _shipsHealingSnapshot[i];
                if (ship != null && ship.Squad.GetCommand() is Heal healCommand && healCommand.IsShipActivelyHealing(ship))
                {
                    ship.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
                }
            }
            _shipsHealingSnapshot.Clear();
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
            ReleaseActiveHealingCrosses();
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

            _isDeathAnimationPending = false;
        }

        public void SpawnHealingCross()
        {
            HealingCross healingCross;
            if (_inactiveHealingCrosses.Count > 0)
            {
                int lastIndex = _inactiveHealingCrosses.Count - 1;
                healingCross = _inactiveHealingCrosses[lastIndex];
                _inactiveHealingCrosses.RemoveAt(lastIndex);
                healingCross.transform.SetParent(Level.Map.Transform);
                healingCross.transform.position = transform.position;
                healingCross.gameObject.SetActive(true);
            }
            else
            {
                GameObject healingCrossObj = Instantiate(HealingCrossPrefab, transform.position, Quaternion.identity);
                healingCrossObj.transform.SetParent(Level.Map.Transform);
                healingCross = healingCrossObj.GetComponent<HealingCross>();
            }

            healingCross.transform.localPosition = Utilities.RandomCoordinate(Level, GetPosition(), new Vector2(16, 16), Vector2.zero);
            healingCross.Setup(this);
            HealingCrosses.Add(healingCross);
        }
    }
}
