using Assets.Scripts;
using Assets.Scripts.Scenes;
using Assets.Scripts.Settings;
using Assets.Scripts.UI_Components;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

public class Controls : MonoBehaviour
{

    public void View()
    {
        //Debug.Log("Viewing codex");
        UIAudioController.Instance.PlayButtonSound();
        gameObject.SetActive(true);
    }
    public void Exit()
    {
        UIAudioController.Instance.PlayButtonSound();
        gameObject.SetActive(false);
    }
}
