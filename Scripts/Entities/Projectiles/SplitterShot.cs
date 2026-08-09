

using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using System.Collections;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Assets.Scripts.Entities.Projectiles
{
    public class SplitterShot : LaserShot
    {
        public int SplitCount;
        public GameObject SplitLaserPrefab;
        public override void ContactTarget(Ship target)
        {
            KillSequence(target);
        }

        public void KillSequence(Ship target)
        {
            if (HasExplosion)
            {
                Explosion.transform.parent = Level.Map.Transform;
                Explosion.transform.localPosition = GetPosition();
                Explosion.SetActive(true);
            }
            Split(target);
            Kill();
        }

        public override void KillSequence()
        {
            if (HasExplosion)
            {
                Explosion.transform.parent = Level.Map.Transform;
                Explosion.transform.localPosition = GetPosition();
                Explosion.SetActive(true);
            }
            Split(null);
            Kill();
        }

        private float _localAngle;
        private int _shotNumber;
        private Projectile _projectile;
        public void Split(Ship target)
        {
            if (DistanceToPoint(StartingPosition) <= (Range - 5))
            {
                for (_shotNumber = 0; _shotNumber < SplitCount; _shotNumber++)
                {
                    _localAngle = Angle * Mathf.Rad2Deg;
                    if (_shotNumber == 0)
                    {
                        _localAngle += 30;
                    }
                    else if (_shotNumber == 1)
                    {
                        _localAngle -= 30;
                    }
                    else if (_shotNumber == 2)
                    {
                        _localAngle += 45;
                    }
                    else if (_shotNumber == 3)
                    {
                        _localAngle -= 45;
                    }

                    _projectile = Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.BeeSmall);
                    // Split children are untargeted ballistic fragments. They bypass
                    // Weapon.SendProjectile(), so they do not own a target damage reservation.
                    _projectile.Setup(Level, Weapon, Shooter, null, GetPosition(), _localAngle * Mathf.Deg2Rad, Weapon.Range, (int)(Weapon.Power / 1.5f));
                    _projectile.ShipsToIgnore.Add(target);

                    // Child shots retain the shooter reference even after its death. Always
                    // register that dependency so GameState cannot recycle the shooter wrapper
                    // until every split child has finished.
                    Shooter.ProjectilesInFlight.Add(_projectile);
                    if (Shooter.IsDead)
                    {
                        _projectile.ShipIsDead = true;
                    }
                }
            }
        }
    }
}