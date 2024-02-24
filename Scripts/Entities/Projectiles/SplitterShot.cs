

using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Level;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Projectiles
{
    public class SplitterShot : LaserShot
    {
        public int SplitCount;
        public GameObject SplitLaserPrefab;
        public override void ContactTarget(Ship target)
        {
            //Debugger.Log($"Split shot hit {target.name}");
            if (HasExplosion)
            {
                Explosion =  Instantiate(ExplosionAnimationPrefab, GetPosition() + Level.GetPosition(), Quaternion.identity);
                Explosion.transform.parent = Level.Map.transform;
            }
            Split(target);
            Kill();
        }

        public void Split(Ship target) // [projectile-method] [note]
        {
            //Debugger.Log($"Splitting into {SplitCount} more shots");

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
                //Debugger.Log($"Split shot #{shotNumber} is at localAngle: {localAngle}, coming from eulerAngle: {transform.localEulerAngles.z}, and now at world" +
                //    $"angle: {worldAngle} (rad) : {radians}");


                Vector3 startingPosition = GetPosition();
                GameObject shot =  Instantiate(SplitLaserPrefab, startingPosition, Quaternion.identity);
                shot.transform.parent = Level.Map.transform;
                LaserShot projectile = (LaserShot)shot.GetComponent(typeof(LaserShot));
                GameState state = Level.GetState();
                projectile.Setup(Level, Shooter.Side, state.AddEntity(), Weapon, Shooter, Target, startingPosition, radians, Weapon.Range/4, Weapon.Power/2);
                state.AddProjectile(projectile);
                projectile.ShipsToIgnore.Add(target);

            }

        }
    }
}