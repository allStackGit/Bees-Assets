using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Level;
using Assets.Scripts.Level.Commands;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class MiningAsteroid : Obstacle
    {
        public List<Squad> SquadsMining = new List<Squad>();
        private void OnTriggerEnter2D(Collider2D collider)
        {
            //Debug.Log($"{Name} collided");
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                if (ship.IsMiningShip && ship.Squad?.Command?.Strategy.Name == "Mining" && ((Mining)ship.Squad?.Command).TargetAstroid == this)
                {
                    Debug.Log($"{ship.Name} is mining {Name}");
                    if (!SquadsMining.Contains(ship.Squad))
                    {
                        SquadsMining.Add(ship.Squad);

                    }
                    ((Mining)ship.Squad?.Command).FoundAsteroid(ship);
                }
            }
            
        }

        public override void Kill()
        {
            IsDead = true;
            SquadsMining.ForEach((squad) =>
            {
                if (squad != null)
                {
                    squad?.Command.SetFinalize("Mining asteroid was destroyed");

                }
            });
            Level.GetState().RemoveObstacle(this);
            Destroy(gameObject);
        }
    }
}