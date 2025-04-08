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
            transform.parent = Level.Map.Transform;
            transform.localPosition = Utilities.RandomCoordinate(Level, Vector2.zero, Level.MiningAsteroidSpawnDistance, Vector2.zero);
            Level.State.AddObstacle(this);
            Level.State.MiningAsteroids.Add(this);
        }
        public override void ClearData()
        {
            base.ClearData();
            SquadsMining.Clear();
        }
        protected void OnTriggerEnter2D(Collider2D collider)
        {
            MiningCollision(collider);
        }
        private GameObject _collidingThing;
        private Ship _miningShip;
        private Mining _command;
        public void MiningCollision(Collider2D collider)
        {
            //Debug.Log($"{Name} collided");
            _collidingThing = collider.gameObject;
            if (_collidingThing.CompareTag("Ship"))
            {
                _miningShip = _collidingThing.GetComponent<Ship>();
                if (_miningShip.IsMiningShip && _miningShip.Squad.HasCommand && _miningShip.Squad.GetCommand().CommandType == ConfigData.CommandTypes.Mining)
                {
                    _command = ((Mining)_miningShip.Squad.GetCommand());

                    if (!_command.ShipsCurrentlyMining.Contains(_miningShip.Id) && _command.TargetAstroid == this)
                    {
                        //Debug.Log($"{_miningShip.Name} is mining {Name}");
                        if (!SquadsMining.Contains(_miningShip.Squad))
                        {
                            SquadsMining.Add(_miningShip.Squad);

                        }
                        _command.FoundAsteroid(_miningShip);

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