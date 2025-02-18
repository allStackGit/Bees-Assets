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
        public override void Setup(Level level)
        {
            base.Setup(level);
            transform.parent = Level.Map.transform;
            transform.localPosition = Utilities.RandomCoordinate(Level, Vector2.zero, Level.MiningAsteroidSpawnDistance, Vector2.zero);
            Level.State.AddObstacle(this);
            Level.State.MiningAsteroids.Add(this);
        }
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
                if (ship.IsMiningShip && ship.Squad?.GetCommand()?.CommandType == ConfigData.CommandTypes.Mining)
                {
                    Mining command = ((Mining)ship.Squad?.GetCommand());

                    if (!command.ShipsCurrentlyMining.Contains(ship.Id) && command.TargetAstroid == this)
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

        public void Kill(bool endKill)
        {
            if (!IsDead)
            {
                IsDead = true;
                if (!endKill)
                {
                    SquadsMining.ForEach((squad) =>
                    {
                        if (!squad.IsDead && squad.HasCommand)
                        {
                            squad.GetCommand().SetFinalize("Mining asteroid was destroyed");

                        }
                    });
                }
                //Debug.Log($"Killing mining asteroid {Name} and returning to pool. endkill: {endKill}");
                Level.State.RemoveObstacle(this);
                Level.State.MiningAsteroids.Remove(this);
                Level.State.MiningAsteroidsToRelease.Add(this);
                //Stage.Pool.ReturnMiningAsteroidToPool(this);
                gameObject.SetActive(false);
            }
            else
            {
                Debug.LogError($"Tried to kill already dead mining asteroid {Name}");
            }

        }
    }
}