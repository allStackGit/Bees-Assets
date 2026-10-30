using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Assets.Scripts.Entities.Ships;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.MLAgents.Sensors;

// Temporary, explicitly invoked characterization harness. Does not run in players or ordinary editor sessions.
[InitializeOnLoad]
public static class BeesHeadingPairInvestigation
{
    const string Flag = "--bees-heading-pair-output=";
    static bool done;
    static string Output => Environment.GetCommandLineArgs().FirstOrDefault(x => x.StartsWith(Flag))?.Substring(Flag.Length);
    static BeesHeadingPairInvestigation() { if (Output != null) EditorApplication.update += Tick; }
    public static void Begin()
    {
        if (Output == null) throw new Exception("Explicit output path required");
        EditorSceneManager.OpenScene("Assets/Scenes/RL 1v1 Training.unity", OpenSceneMode.Single);
        EditorApplication.isPlaying = true;
    }
    [Serializable] public class Row
    {
        public int q; public float distance;
        public Vector2 observerPosition, targetPosition, targetVelocity, policyVelocity;
        public float observerHeading, targetHeading;
        public float[] observations;
    }
    [Serializable] public class Payload { public string utc, source; public Row[] rows; public string limitation; }
    static void Tick()
    {
        if (done || !EditorApplication.isPlaying || EditorApplication.isCompiling) return;
        var ships = UnityEngine.Object.FindObjectsByType<Ship>(FindObjectsSortMode.None);
        var bee = ships.FirstOrDefault(x => x.ShipType.ToString() == "Bumblebee" && !x.IsDead && x.Level != null && x.Body != null);
        var gun = ships.FirstOrDefault(x => x.ShipType.ToString() == "Gunship" && !x.IsDead && x.Level == bee?.Level && x.Body != null);
        if (bee == null || gun == null || bee.Turrets.Count == 0 || gun.Turrets.Count == 0) return;
        done = true;
        try
        {
            Time.timeScale = 0;
            var asm = typeof(Ship).Assembly;
            var agent = asm.GetType("RlOneVsOneAgent", true);
            var perception = asm.GetType("RlCombatPerception", true);
            var collect = agent.GetMethod("CollectPolicyObservations", BindingFlags.Static | BindingFlags.NonPublic);
            var get = typeof(VectorSensor).GetMethod("GetObservations", BindingFlags.Instance | BindingFlags.NonPublic);
            var rows = new List<Row>();
            Vector2 center = new Vector2((bee.Level.MinX + bee.Level.MaxX)/2, (bee.Level.MinY + bee.Level.MaxY)/2);
            foreach(float distance in new float[]{40,100})
            foreach(int q in new int[]{0,1})
            {
                // The production builder receives the explicit frame argument used by the policy adapter.
                // Q1(x,y)=(-y,x); hence world-relative positions invert that rotation.
                Vector2 relative = q == 0 ? new Vector2(0,distance) : new Vector2(distance,0);
                bee.Body.position = center; bee.Transform.position = center;
                gun.Body.position = center + relative; gun.Transform.position = center + relative;
                bee.Rotation = q == 0 ? 0 : 90; gun.Rotation = q == 0 ? 90 : 180;
                bee.Transform.eulerAngles = new Vector3(0,0,bee.Rotation);
                gun.Transform.eulerAngles = new Vector3(0,0,gun.Rotation);
                bee.Body.linearVelocity = Vector2.zero;
                gun.SetCurrentSpeed(18.75f);
                gun.Body.linearVelocity = new Vector2(-Mathf.Sin(gun.Rotation*Mathf.Deg2Rad), Mathf.Cos(gun.Rotation*Mathf.Deg2Rad))*18.75f;
                Physics2D.SyncTransforms();
                var sensor = new VectorSensor(7743);
                collect.Invoke(null,new object[]{Activator.CreateInstance(perception,true),bee,bee.Side,sensor,q,0});
                float[] obs = ((IEnumerable<float>)get.Invoke(sensor,null)).ToArray();
                Vector2 velocity = gun.Body.linearVelocity;
                rows.Add(new Row { q=q,distance=distance, observerPosition=bee.GetPosition(),targetPosition=gun.GetPosition(),
                    targetVelocity=velocity,policyVelocity=q==0?velocity:new Vector2(-velocity.y,velocity.x),
                    observerHeading=bee.Rotation,targetHeading=gun.Rotation,observations=obs });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Output));
            File.WriteAllText(Output,JsonUtility.ToJson(new Payload {utc=DateTime.UtcNow.ToString("O"),source="ae6aaec56dc2c9fdc86683be4938dbddbe7e1032",
                rows=rows.ToArray(),limitation="Production observation builder invoked on actual instantiated ships with explicit diagnostic poses/frame; background exploration state was not overwritten. No projectile episode or decision-time policy inference performed."},true));
            Debug.Log("Heading pair observations saved: "+Output);
            EditorApplication.Exit(0);
        }
        catch(Exception e) { File.WriteAllText(Output+".error",e.ToString()); Debug.LogException(e); EditorApplication.Exit(4); }
    }
}
