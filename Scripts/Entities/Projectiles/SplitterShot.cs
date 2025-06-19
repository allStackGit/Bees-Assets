

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
            //Debug.Log($"Split shot hit {target.name}");
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
        public void Split(Ship target) // [projectile-method] [note]
        {
            //Debug.Log($"Splitting into {SplitCount} more shots");
            if (DistanceToPoint(StartingPosition) >= (Range - 5))
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
                    //Debug.Log($"Split shot #{shotNumber} is at localAngle: {localAngle}, coming from eulerAngle: {transform.localEulerAngles.z}, and now at world" +
                    //    $"angle: {worldAngle} (rad) : {radians}");


                    _projectile = Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.BeeSmall);
                    _projectile.Setup(Level, Weapon, Shooter, Target, GetPosition(), _localAngle * Mathf.Deg2Rad, Weapon.Range, (int)(Weapon.Power / 1.5f));
                    _projectile.ShipsToIgnore.Add(target);
                    if (!Shooter.IsDead)
                    {
                        Shooter.ProjectilesInFlight.Add(_projectile);
                    }
                    else
                    {
                        _projectile.ShipIsDead = true;
                    }




                    //GameObject shot =  Instantiate(SplitLaserPrefab, startingPosition, Quaternion.identity);
                    //shot.transform.parent = Level.Map.Transform;
                    //LaserShot projectile = (LaserShot)shot.GetComponent(typeof(LaserShot));
                    //projectile.Setup(Level, Level.State.GetId(), Weapon, Shooter, Target, startingPosition, radians, Weapon.Range, (int) (Weapon.Power / 1.5f));
                    //projectile.ShipsToIgnore.Add(target);
                    //Shooter.ProjectilesInFlight.Add(projectile);

                }
            }
            


        }
    }
}