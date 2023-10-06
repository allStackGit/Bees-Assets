using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UIComponents
{
    public class Officer
    {
        public string Name, Rank;
        public Sprite Portrait;
        public Officer(string name, string rank, Sprite portrait) {
            this.Name = name;
            this.Rank = rank;
            Portrait = portrait;
        }

    }
}