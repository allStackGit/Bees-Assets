using Assets.Scripts.Scenes;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities
{
    public class ObstacleDebrisPool : MonoBehaviour
    {
        private readonly Stack<ObstacleDebrisPiece> _inactive = new Stack<ObstacleDebrisPiece>();

        public static ObstacleDebrisPool GetOrCreate(Stage stage)
        {
            ObstacleDebrisPool pool = stage.GetComponent<ObstacleDebrisPool>();
            if (pool == null)
            {
                pool = stage.gameObject.AddComponent<ObstacleDebrisPool>();
            }
            return pool;
        }

        public ObstacleDebrisPiece Get()
        {
            ObstacleDebrisPiece piece = null;
            while (_inactive.Count > 0 && piece == null)
            {
                piece = _inactive.Pop();
            }

            if (piece == null)
            {
                GameObject debrisObject = new GameObject("Obstacle Debris");
                debrisObject.transform.SetParent(transform, false);
                piece = debrisObject.AddComponent<ObstacleDebrisPiece>();
                debrisObject.SetActive(false);
            }

            piece.transform.SetParent(transform, true);
            piece.gameObject.SetActive(true);
            return piece;
        }

        public void Release(ObstacleDebrisPiece piece)
        {
            if (piece == null || !piece.gameObject.activeSelf)
            {
                return;
            }

            piece.gameObject.SetActive(false);
            piece.transform.SetParent(transform, false);
            _inactive.Push(piece);
        }
    }
}
