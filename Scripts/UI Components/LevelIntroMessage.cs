using System.Collections;
using UnityEngine;

namespace Assets.Scripts.UIComponents
{
    public class LevelIntroMessage
    {
        public readonly string Name, Title, Message;
        public readonly int LevelId;
        public readonly Officer Officer;

        public  LevelIntroMessage(string name, string title, string message, int levelId, Officer officer)
        {
            this.Name = name;
            this.Title = title;
            this.Message = message;
            this.LevelId = levelId;
            this.Officer = officer;

        }
    }
}