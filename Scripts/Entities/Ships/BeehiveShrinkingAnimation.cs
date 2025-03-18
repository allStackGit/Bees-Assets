using Assets.Scripts.Entities.Ships;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BeehiveShrinkingAnimation : ShipExplosionAnimation
{
    public Beehive Beehive;
    public override void Kill()
    {
        base.Kill();
        Beehive.FinalExplosion();

    }

}
