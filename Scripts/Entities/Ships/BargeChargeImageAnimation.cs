using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BargeChargeImageAnimation : MonoBehaviour
{
    public Barge Barge;
    public Transform Transform;
    public Vector2 OriginalPosition;
    public Vector3 ShiftAmount;

    private ScaledTimer _timer = new ScaledTimer();
    public void StartCharge()
    {
        _timer.Reuse(.25f, ShiftDown, true);
        Barge.Level.AddTimer(_timer);
    }

    public void CancelTimer()
    {
        Barge.Level.CancelTimer(_timer);
    }
    public void ShiftDown()
    {
        Transform.localPosition += ShiftAmount;
    }
    public void Kill()
    {
        gameObject.SetActive(false);
        Transform.localPosition = OriginalPosition;
        CancelTimer();
    }
}
