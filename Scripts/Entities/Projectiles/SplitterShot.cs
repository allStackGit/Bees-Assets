

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
                Explosion.transform.parent = Level.Map.transform;
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
                Explosion.transform.parent = Level.Map.transform;
                Explosion.transform.localPosition = GetPosition();
                Explosion.SetActive(true);
            }
            Split(null);
            Kill();
        }



        public void Split(Ship target) // [projectile-method] [note]
        {
            //Debug.Log($"Splitting into {SplitCount} more shots");

            for (int shotNumber = 0; shotNumber < SplitCount; shotNumber++)
            {
                float localAngle = Angle * Mathf.Rad2Deg; 
                if (shotNumber == 0)
                {
                    localAngle += 45;
                }else if (shotNumber == 1)
                {
                    localAngle -= 45;
                }
                else if (shotNumber == 2)
                {
                    localAngle += 90;
                }
                else if (shotNumber == 3)
                {
                    localAngle -= 90;
                }
                float radians = localAngle * Mathf.Deg2Rad;
                //Debug.Log($"Split shot #{shotNumber} is at localAngle: {localAngle}, coming from eulerAngle: {transform.localEulerAngles.z}, and now at world" +
                //    $"angle: {worldAngle} (rad) : {radians}");


                Vector3 startingPosition = GetPosition();
                Projectile projectile = Stage.Pool.GetProjectileFromPool(ConfigData.ProjectileTypes.BeeSmall);
                projectile.Setup(Level, Weapon, Shooter, Target, startingPosition, radians, Weapon.Range, (int)(Weapon.Power / 1.5f));
                projectile.ShipsToIgnore.Add(target);
                Shooter.ProjectilesInFlight.Add(projectile);


                //GameObject shot =  Instantiate(SplitLaserPrefab, startingPosition, Quaternion.identity);
                //shot.transform.parent = Level.Map.transform;
                //LaserShot projectile = (LaserShot)shot.GetComponent(typeof(LaserShot));
                //projectile.Setup(Level, Level.State.GetId(), Weapon, Shooter, Target, startingPosition, radians, Weapon.Range, (int) (Weapon.Power / 1.5f));
                //projectile.ShipsToIgnore.Add(target);
                //Shooter.ProjectilesInFlight.Add(projectile);

            }

        }
    }
}