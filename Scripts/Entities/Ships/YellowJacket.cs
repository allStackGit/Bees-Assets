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

        public Weapon Bomb => Weapons.First();

        protected override void OnTriggerEnter2D(Collider2D collider) // projectile collision
        {
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.name == ("Selection Box"))
            {
                if (IsUserControlled)
                {
                    Stage.Selector.SelectShip(this);
                }
            }
            else if (collidingThing.CompareTag("Ship") && Collider.IsTouching(collider))
            {
                TouchingShip = collidingThing.GetComponent<Ship>();
                //Debug.Log($"Striker collided with a ship!" +
                //    $"{ship}, " +
                //    $"{Squad}, " +
                //    $"{TargetShip}");

                if (TouchingShip != null && TouchingShip.Side != Side && Squad.HasCommand && HasWeaponsTargetShips && WeaponsTargetShips.Contains(TouchingShip))
                {
                    //Debug.Log("Collided with our target ship!");
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
                if (ship.Equals(TouchingShip))
                {
                    TouchingShip = null;
                }
            }
            else if (collidingThing.name == ("Selection Box") && IsUserControlled)
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

        public void SuicideKill(Squad killerSquad) // [kill-method] [stats-method] [note]
        {
            DropExplosionAnimation();

            Level.State.RemoveShip(this);
            Squad.RemoveShip(this);

            if (Stage.ReplaceDeadShips && Squad.SavedSquad.HasBeenSavedToStorage)
            {
                FleetShip.IsDead = true;
            }
            Squad.SavedSquad.Stats.ShipsLost++;

            killerSquad.SavedSquad.Stats.Kills++;
            //FleetShip.BattlesFought++;

            if (Squad.GetShips().Count == 0)
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

        private void LogDetonationDamage(int power, Ship attacker, Ship target) // [damage-method] [note]
        {
            int targetOldTSV = target.Tsv;
            target.Health -= power;

            if (target.Health < 0)
            {
                target.Health = 0;
            }

            int targetTSVChange = target.Tsv - targetOldTSV; // this is a negative number since being hit by a projectile should induce a loss of TSV
            //Debug.Log($"Yellow Jacket #{Id} Detonation: {targetTSVChange} tsv inflicted on {target.Name}");
            LogHitStats(attacker, attacker.FleetShip, attacker.Squad.SavedSquad, target, target.Squad, targetTSVChange);

            // each hit, add the negative TSV to the target's command and subtract the negative TSV from the shooter's command

            if (attacker.Squad.HasCommand)
            {
                attacker.Squad.Command.Tsv += -1 * targetTSVChange; // add the TSV to the shooter
            }
            if (target.Squad.HasCommand)
            {
                target.Squad.Command.Tsv += targetTSVChange; // subtract the TSV from the target
            }
            target.UpdateHealthBar();


        }
    }
}