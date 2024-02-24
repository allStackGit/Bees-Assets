using Assets.Scripts.Entities;
using Assets.Scripts.Level;
using System.Linq;
using Assets.Scripts.Entities.Ships.Weapons;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class YellowJacket : Ship
    {

        public bool HasCompletedRun;
        public Ship ContactedShip, TouchingShip;

        public Weapon Bomb => Weapons.First();

        protected override void OnTriggerEnter2D(Collider2D collider) // projectile collision
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.name == ("Selection Box"))
            {
                if (IsUserControlled)
                {
                    Level.Selector.SelectShip(this);
                }
            }
            else if (collidingThing.CompareTag("Ship"))
            {
                TouchingShip = collidingThing.GetComponent<Ship>();
                //Debugger.Log($"Striker collided with a ship!" +
                //    $"{ship}, " +
                //    $"{Squad}, " +
                //    $"{TargetShip}");

                if (TouchingShip != null && TouchingShip.Side != Side && Squad.HasCommand && HasTargetShips && TargetShips.Contains(TouchingShip))
                {
                    //Debugger.Log("Collided with our target ship!");
                    ContactedShip = TouchingShip;
                    Detonate();

                }
            }
        }

        protected override void OnTriggerExit2D(Collider2D collider)
        {
            GameObject collidingThing = collider.gameObject;
            if (TouchingShip != null && collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                if (ship != null && ship.Equals(TouchingShip))
                {
                    TouchingShip = null;
                }
            }
            else if (collidingThing.name == ("Selection Box") && IsUserControlled)
            {
                Level.Selector.DeselectShip(this);
            }
        }
        public void TryToDetonate()
        {
            //Debugger.Log($"Trying to detonate with {Name}");
            if (TouchingShip != null && TouchingShip.Side != Side)
            {
                ContactedShip = TouchingShip;
                Detonate();
                return;
            }
            //Debugger.Log($"Failed trying to detonate with {Name}: TouchingShip: [{TouchingShip}]");

        }
        private void Detonate()
        {
            //Debugger.Log($"Yellow Jacket #{Id} is detonating against {ContactedShip.Name}");
            HasCompletedRun = true;

            // do damage and stats
            LogDetonationDamage(Bomb.Power, this, ContactedShip);
            LogDetonationDamage(Bomb.Power, ContactedShip, this);
            Squad targetShipSquad = ContactedShip.Squad;

            if (ContactedShip.Health <= 0)
            {
                ContactedShip.Kill(this); // kill the target ship if needed, yellow jacket gets credit
                SuicideKill(targetShipSquad); // kill the yellow jacket and give credit to the target ship squad
            }
            else
            {
                Kill(ContactedShip); // kill standard
            }

           
        }

        public void SuicideKill(Squad squad) // [kill-method] [note]
        {
            if (!IsDead)
            {
                died = true;
                DropExplosionAnimation();

                Level.GetState().RemoveShip(this);
                Squad.RemoveShip(this);

                FleetShip.IsDead = true;
                Squad.SavedSquad.Stats.ShipsLost++;

                squad.SavedSquad.Stats.Kills++;
                //FleetShip.BattlesFought++;

                if (Squad.GetShips().Count <= 0)
                {
                    //Squad.SavedSquad.Stats.BattlesFought++;
                    Squad.Kill();
                }
                else
                {
                    Squad.SetOffsets();
                }
                Destroy(gameObject);
            }

        }

        private void LogDetonationDamage(int power, Ship shooter, Ship target) // [damage-method] [note]
        {
            int targetOldTSV = target.Tsv;
            target.Health -= power;

            if (target.Health < 0)
            {
                target.Health = 0;
            }

            int targetTSVChange = target.Tsv - targetOldTSV; // this is a negative number since being hit by a projectile should induce a loss of TSV
            //Debugger.Log($"Yellow Jacket #{Id} Detonation: {targetTSVChange} tsv inflicted on {target.Name}");
            LogHitStats(shooter, shooter.Squad, target, target.Squad, targetTSVChange);

            // each hit, add the negative TSV to the target's command and subtract the negative TSV from the shooter's command

            if (shooter.Squad.Command != null)
            {
                shooter.Squad.Command.Tsv += -1 * targetTSVChange; // add the TSV to the shooter
            }
            if (target.Squad.Command != null)
            {
                target.Squad.Command.Tsv += targetTSVChange; // subtract the TSV from the target
            }
            target.UpdateHealthBar();


        }
    }
}