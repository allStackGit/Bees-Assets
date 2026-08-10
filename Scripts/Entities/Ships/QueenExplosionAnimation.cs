using Assets.Scripts.Entities.Ships;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class QueenExplosionAnimation : ShipExplosionAnimation
{
    public GameObject Container;
    public ShipRemains Remains;
    public Queen Queen;
    public override void Kill()
    {
        Container.SetActive(false);

        Remains.transform.localPosition = Queen.GetPosition();
        Remains.transform.eulerAngles = Vector3.forward * Queen.Rotation;
        Remains.gameObject.SetActive(true);
        Queen.Level.State.AddDeadBody(Remains);
        Queen.CompleteDeathAnimation();
    }
}
