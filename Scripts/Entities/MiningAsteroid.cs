using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
using System.Collections.Generic;
using Unity.Mathematics;
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
            _collidingThing = collider.gameObject;
            if (_collidingThing.CompareTag("Ship"))
            {
                _miningShip = _collidingThing.GetComponent<Ship>();
                if (_miningShip.IsMiningShip && _miningShip.Squad.HasCommand && _miningShip.Squad.GetCommand().CommandType == ConfigData.CommandTypes.Mining)
                {
                    _command = ((Mining)_miningShip.Squad.GetCommand());

                    if (!_command.ShipsCurrentlyMining.Contains(_miningShip) && _command.TargetAstroid == this)
                    {
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
                    while (SquadsMining.Count > 0)
                    {
                        int lastIndex = SquadsMining.Count - 1;
                        Squad squad = SquadsMining[lastIndex];
                        if (!squad.IsDead && squad.HasCommand && squad.GetCommand().CommandType == ConfigData.CommandTypes.Mining)
                        {
                            int previousCount = SquadsMining.Count;
                            squad.GetCommand().SetFinalize("Mining asteroid was destroyed");
                            if (SquadsMining.Count == previousCount)
                            {
                                SquadsMining.RemoveAt(lastIndex);
                            }
                        }
                        else
                        {
                            SquadsMining.RemoveAt(lastIndex);
                        }
                    }
                }
                Level.State.ForgetHiveMindMiningAsteroid(this);
                Level.State.RemoveObstacle(this);
                Level.State.MiningAsteroids.Remove(this);
                Level.State.MiningAsteroidsToRelease.Add(this);
                gameObject.SetActive(false);
            }
            else
            {
                Debug.LogError($"Tried to kill already dead mining asteroid {Name}");
            }
        }
    }
}
