using Assets.Scripts.Entities;
using Assets.Scripts.Levels;
using System.Linq;
using Assets.Scripts.Entities.Ships.Weapons;
using UnityEngine;
using Assets.Scripts.Entities.Projectiles;

namespace Assets.Scripts.Entities.Ships
{
    public class YellowJacket : Ship
    {

        public bool HasCompletedRun;
        public Ship ContactedShip, TouchingShip;

        public Weapon Bomb;
        public override void Create(Stage stage)
        {
            base.Create(stage);
            Bomb = Weapons.First();
            Destroy(Bomb.Piece);
        }
        public override void ClearData()
        {
            base.ClearData();
            ContactedShip = null;
            TouchingShip = null;
            HasCompletedRun = false;
        }
        private GameObject _collidingThing;
        private Ship _collidingShip;
        protected override void OnTriggerEnter2D(Collider2D collider) // projectile collision
        {
            _collidingThing = collider.gameObject;
            if (_collidingThing.name == ("Selection Box"))
            {
                if (IsUserControlled)
                {
                    Stage.Selector.SelectShip(this);
                }
            }
            else if (_collidingThing.CompareTag("Ship") && Collider.IsTouching(collider))
            {
                TouchingShip = _collidingThing.GetComponent<Ship>();
                //Debug.Log($"Striker collided with a ship!" +
                //    $"{ship}, " +
                //    $"{Squad}, " +
                //    $"{TargetShip}");

                if (TouchingShip.Side != Side && Squad.HasCommand && Bomb.TargetShip == TouchingShip)
                {
                    //Debug.Log("Collided with our target ship!");
                    ContactedShip = TouchingShip;
                    Detonate();

                }
            }
        }

        protected override void OnTriggerExit2D(Collider2D collider)
        {
            _collidingThing = collider.gameObject;
            if (TouchingShip != null && _collidingThing.CompareTag("Ship"))
            {
                _collidingShip = _collidingThing.GetComponent<Ship>();
                if (_collidingShip == TouchingShip)
                {
                    TouchingShip = null;
                }
            }
            else if (_collidingThing.name == ("Selection Box") && IsUserControlled)
            {
                Stage.Selector.DeselectShip(this);
            }
        }
        public void TryToDetonate()
        {
            //Debug.Log($"Trying to detonate with {Name}");
            if (TouchingShip != null && TouchingShip.Side != Side)
            {
                ContactedShip = TouchingShip;
                Detonate();
                return;
            }
            //Debug.Log($"Failed trying to detonate with {Name}: TouchingShip: [{TouchingShip}]");

        }
        private void Detonate()
        {
            //Debug.Log($"Yellow Jacket #{Id} is detonating against {ContactedShip.Name}");
            HasCompletedRun = true;


            // do damage and stats
            LogDetonationDamage(Bomb.Power, this, ContactedShip);
            LogDetonationDamage(Bomb.Power, ContactedShip, this);

            if (ContactedShip.Health <= 0)
            {
                ContactedShip.Kill(this, FleetShip, Squad.SavedSquad); // kill the target ship if needed, yellow jacket gets credit
            }


            Kill(ContactedShip, ContactedShip.FleetShip, ContactedShip.Squad.SavedSquad); // kill the Yellow Jacket either way, giving credit to the contacted ship 

        }

        private int _targetOldTSV, _targetTSVChange;
        private void LogDetonationDamage(int power, Ship attacker, Ship target) // [damage-method] [note]
        {
            _targetOldTSV = target.Tsv;
            target.Health -= power;

            if (target.Health < 0)
            {
                target.Health = 0;
            }

            _targetTSVChange = target.Tsv - _targetOldTSV; // this is a negative number since being hit by a projectile should induce a loss of TSV
            //Debug.Log($"Yellow Jacket #{Id} Detonation: {targetTSVChange} tsv inflicted on {target.Name}");
            LogHitStats(attacker, attacker.FleetShip, attacker.Squad.SavedSquad, target, target.Squad, _targetTSVChange);

            // each hit, add the negative TSV to the target's command and subtract the negative TSV from the shooter's command

            if (attacker.Squad.HasCommand)
            {
                attacker.Squad.GetCommand().Tsv += -_targetTSVChange; // add the TSV to the shooter
            }
            if (target.Squad.HasCommand)
            {
                target.Squad.GetCommand().Tsv += _targetTSVChange; // subtract the TSV from the target
            }
            target.UpdateHealthBar();


        }
    }
}