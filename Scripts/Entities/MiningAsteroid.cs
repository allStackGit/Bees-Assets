using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class MiningAsteroid : Obstacle
    {
        public List<Squad> SquadsMining = new List<Squad>();
        public override void ClearData()
        {
            base.ClearData();
            SquadsMining.Clear();
        }
        protected override void OnTriggerEnter2D(Collider2D collider)
        {
            MiningCollision(collider);
        }

        public void MiningCollision(Collider2D collider)
        {
            //Debug.Log($"{Name} collided");
            GameObject collidingThing = collider.gameObject;
            if (collidingThing.CompareTag("Ship"))
            {
                Ship ship = collidingThing.GetComponent<Ship>();
                if (ship.IsMiningShip && ship.Squad?.Command?.Strategy.CommandType == ConfigData.CommandTypes.Mining)
                {
                    Mining command = ((Mining)ship.Squad?.Command);

                    if (!command.ShipsMining.Contains(ship) && command.TargetAstroid == this)
                    {
                        //Debug.Log($"{ship.Name} is mining {Name}");
                        if (!SquadsMining.Contains(ship.Squad))
                        {
                            SquadsMining.Add(ship.Squad);

                        }
                        command.FoundAsteroid(ship);

                    }

                }
            }
        }

        public override void Kill()
        {
            if (!IsDead)
            {
                IsDead = true;
                SquadsMining.ForEach((squad) =>
                {
                    if (squad != null && squad.HasCommand)
                    {
                        squad.Command.SetFinalize("Mining asteroid was destroyed");

                    }
                });
                Level.State.RemoveObstacle(this);
                Level.State.MiningAsteroids.Remove(this);
                Stage.Pool.ReturnMiningAsteroidToPool(this);
            }

        }
    }
}