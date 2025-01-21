using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Assets.Scripts
{
    public class Debugger
    {
        public static void Log(dynamic obj, bool debugProperties = false)
        {
            if (obj == null)
            {
                Debug.Log(null);
            }
            else if (obj.GetType() == typeof(string))
            {
                Debug.Log(obj);
            }
            
            else if (debugProperties)
            {
                int i = 0;
                foreach (var prop in obj.GetType().GetProperties())
                {
                    i++;
                    Debug.Log($"{i}. {prop.Name} ({obj.GetType()}): []\n");
                    Debug.Log(prop.GetValue(obj));
                }
            }
            else if (obj.GetType() == typeof(List<dynamic>))
            {
                string output = "";
                List<dynamic> list = obj;
                list.ForEach((item) =>
                {
                    output += JsonUtility.ToJson(item, true);
                });

                Debug.Log(output);
            }
            else
            {
                Debug.Log(obj);
            }
        }
        public static void Error(object obj)
        {
            Debug.LogError(obj);
        }
        public static void Exception(Exception e)
        {
            Debug.LogException(e);
        }
        public static void Exception(string e)
        {
            //Log(e);
            Debug.LogException(new Exception(e));
        }
        public static void LogSquads(List<Squad> squads)
        {
            string output = $"\nLog Squads: {squads.Count}\n";
            int index = 0;
            squads.ForEach((squad) =>
            {
                output += $"[{index}] {squad.ToString()}\n";
                index++;
                squad.GetShips().ForEach((ship) =>
                {
                    output += $"    {ship.ToString()}\n";
                });
            });
            Debug.Log(output);
        }

        public static void PrintList<T>(List<T> list)
        {
            string printOutput = "";
            int index = 0;
            list.ForEach((item) =>
            {
                printOutput += $"{index++}: {item}, ";
               
            });
            Log($"List: {printOutput}");
        }
    }
}