using Assets.Scripts.Entities;
using Assets.Scripts.Level;
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
                    Level.Selector.SelectShip(this);
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
                Level.Selector.DeselectShip(this);
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

            Bomb.BaseProjectile.Target = ContactedShip;

            GameObject instance = Instantiate(new GameObject(), new Vector2(0, 0), Quaternion.identity);
            Projectile contactedShipProjectile = (Projectile)instance.gameObject.AddComponent(typeof(Projectile));
            contactedShipProjectile.Setup(Level, ContactedShip.Side, Level.GetState().GetId(), null, ContactedShip, this, ContactedShip.GetPosition(), 0, 0, Bomb.Power);

            // do damage and stats
            LogDetonationDamage(Bomb.BaseProjectile, ContactedShip);
            LogDetonationDamage(contactedShipProjectile, this);

            if (ContactedShip.Health <= 0)
            {
                ContactedShip.Kill(Bomb.BaseProjectile); // kill the target ship if needed, yellow jacket gets credit
            }


            Kill(contactedShipProjectile); // kill the Yellow Jacket either way, giving credit to the contacted ship 

        }

        public void SuicideKill(Squad killerSquad) // [kill-method] [stats-method] [note]
        {
            DropExplosionAnimation();

            Level.GetState().RemoveShip(this);
            Squad.RemoveShip(this);

            if (Level.ReplaceDeadShips && Squad.SavedSquad.HasBeenSavedToStorage)
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

        private void LogDetonationDamage(Projectile projectile, Ship target) // [damage-method] [note]
        {
            int targetOldTSV = target.Tsv;
            target.Health -= projectile.Power;

            if (target.Health < 0)
            {
                target.Health = 0;
            }

            int targetTSVChange = target.Tsv - targetOldTSV; // this is a negative number since being hit by a projectile should induce a loss of TSV
            //Debug.Log($"Yellow Jacket #{Id} Detonation: {targetTSVChange} tsv inflicted on {target.Name}");
            LogHitStats(projectile, target, target.Squad, targetTSVChange);

            // each hit, add the negative TSV to the target's command and subtract the negative TSV from the shooter's command

            if (projectile.Shooter.Squad.Command != null)
            {
                projectile.Shooter.Squad.Command.Tsv += -1 * targetTSVChange; // add the TSV to the shooter
            }
            if (target.Squad.Command != null)
            {
                target.Squad.Command.Tsv += targetTSVChange; // subtract the TSV from the target
            }
            target.UpdateHealthBar();


        }
    }
}