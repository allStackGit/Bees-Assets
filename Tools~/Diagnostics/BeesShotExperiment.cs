// Isolated characterization tool. Copy into an Editor folder only for an explicitly approved run.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Linq.Expressions;
using System.Diagnostics;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Levels;
using Unity.MLAgents.Sensors;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

[InitializeOnLoad]
public static class BeesShotExperiment
{
    const string Flag = "--bees-shot-experiment=";
    static bool installed;
    internal static string ConfigPath => Environment.GetCommandLineArgs().FirstOrDefault(x => x.StartsWith(Flag))?.Substring(Flag.Length);
    static BeesShotExperiment() { if(ConfigPath != null) EditorApplication.update += Install; }
    public static void Begin()
    {
        if(ConfigPath == null || !Path.IsPathRooted(ConfigPath)) throw new Exception("Explicit absolute diagnostic config required");
        EditorSceneManager.OpenScene("Assets/Scenes/RL 1v1 Training.unity",OpenSceneMode.Single);
        EditorApplication.isPlaying = true;
    }
    static void Install()
    {
        if(installed || !EditorApplication.isPlaying || EditorApplication.isCompiling) return;
        var ships = UnityEngine.Object.FindObjectsByType<Ship>(FindObjectsSortMode.None);
        if(!ships.Any(x => !x.IsDead && x.Level != null && x.ShipType.ToString()=="Bumblebee")) return;
        installed = true;
        var go = new GameObject("Isolated shot experiment");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<BeesShotExperimentDriver>().Initialize(ConfigPath);
    }
}

// Executes before coordinator/ship updates, preserving the production weapon and projectile lifecycle.
[DefaultExecutionOrder(-10000)]
public sealed class BeesShotExperimentDriver : MonoBehaviour
{
    [Serializable] public class Config
    {
        public string root, python, server_script, model, deps, model_sha256;
        public double deadline_epoch;
        public int seed = 13791;
    }
    [Serializable] public class Request
    {
        public long sequence;
        public float[] observations, action_masks;
    }
    [Serializable] public class Reply
    {
        public bool ready;
        public long sequence;
        public float[] continuous;
        public int[] discrete;
    }
    [Serializable] public class Scenario
    {
        public int trial, q;
        public float bearing, distance, speed;
        public string arm;
    }
    [Serializable] public class Entry
    {
        public string kind, reason;
        public Scenario scenario;
        public long sequence, root_id, projectile_id;
        public int episode, step, shots, hits, damage, health, completed;
        public bool ready, aligned, timed_out, fire;
        public float simulated_time, turret_heading, min_sampled_center_distance;
        public Vector2 observer_position, target_position, target_velocity, aim, launch_position, launch_velocity;
        public float[] observations, continuous;
        public int[] discrete;
    }

    Config config;
    Process server;
    Type agentType, perceptionType, coordinatorType;
    MethodInfo collect, move, weapon, sensorValues;
    EventInfo endedEvent;
    Delegate endedHandler;
    object perception, coordinator;
    Ship bee, target;
    Level level;
    Scenario scenario;
    List<Scenario> scenarios = new List<Scenario>();
    Dictionary<long,Entry> projectiles = new Dictionary<long,Entry>();
    int index, lastEpisode=-1, episode, step;
    long sequence;
    bool active, finishing, initialized;
    Vector2 heldAim;
    float beganAt;
    const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    public void Initialize(string path)
    {
        try
        {
            config = JsonUtility.FromJson<Config>(File.ReadAllText(path));
            if(!Path.IsPathRooted(config.root) || !Path.GetFullPath(path).StartsWith(Path.GetFullPath(config.root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new Exception("Config must be inside its isolated output directory");
            if(DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= config.deadline_epoch) throw new Exception("Approved deadline expired");
            Directory.CreateDirectory(config.root);
            var asm = typeof(Ship).Assembly;
            agentType = asm.GetType("RlOneVsOneAgent",true);
            perceptionType = asm.GetType("RlCombatPerception",true);
            coordinatorType = asm.GetType("RlOneVsOneEpisodeCoordinator",true);
            collect = agentType.GetMethod("CollectPolicyObservations",Hidden);
            move = agentType.GetMethod("ApplyMovementCommand",Hidden);
            weapon = agentType.GetMethod("ApplyWeaponCommand",Hidden);
            sensorValues = typeof(VectorSensor).GetMethod("GetObservations",Hidden);
            perception = Activator.CreateInstance(perceptionType,true);
            endedEvent = coordinatorType.GetEvent("EpisodeEnded",Hidden);
            var parameters = endedEvent.EventHandlerType.GetMethod("Invoke").GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
            endedHandler = Expression.Lambda(endedEvent.EventHandlerType,
                Expression.Call(Expression.Constant(this),GetType().GetMethod("EpisodeEnded"),parameters.Select(p=>Expression.Convert(p,typeof(object)))),parameters).Compile();
            endedEvent.GetAddMethod(true).Invoke(null,new object[]{endedHandler});
            foreach(float bearing in new float[]{0,90})
            foreach(float distance in new float[]{40,100})
            foreach(float speed in new float[]{0,18.75f})
            foreach(int q in new int[]{0,1})
            foreach(string arm in new string[]{"policy","oracle"})
                scenarios.Add(new Scenario{trial=scenarios.Count,q=q,bearing=bearing,distance=distance,speed=speed,arm=arm});
            if(scenarios.Count != 32) throw new Exception("Unexpected scenario count");
            UnityEngine.Random.InitState(config.seed);
            Time.captureFramerate = 50;
            Time.timeScale = 1;
            Time.fixedDeltaTime = .02f;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            server = new Process { StartInfo = new ProcessStartInfo {
                FileName=config.python,Arguments=Quote(config.server_script)+" "+Quote(path),
                UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
                CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=config.root
            }};
            server.ErrorDataReceived += (s,e) => {if(e.Data != null) File.AppendAllText(Path.Combine(config.root,"inference-errors.log"),e.Data+Environment.NewLine);};
            server.Start(); server.BeginErrorReadLine();
            if(!ReadReply().ready) throw new Exception("CPU inference did not initialize");
            initialized = true;
            Write(new Entry{kind="started",reason="32 fixed scenarios; deterministic checkpoint; known target; fixed observer; actual timers and collision"});
        }
        catch(Exception e) { Fail(e); }
    }

    static string Quote(string path)
    {
        if(path == null || path.Contains("\"")) throw new Exception("Invalid diagnostic path");
        return "\""+path+"\"";
    }
    Reply ReadReply()
    {
        var task = server.StandardOutput.ReadLineAsync();
        if(!task.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Inference pipe exceeded 10 seconds");
        if(task.Result == null) throw new Exception("Inference process exited");
        return JsonUtility.FromJson<Reply>(task.Result);
    }
    object Field(object obj,string name) => obj.GetType().GetField(name,Hidden).GetValue(obj);
    int Int(object obj,string name) => Convert.ToInt32(Field(obj,name));
    bool Bool(object obj,string name) => Convert.ToBoolean(Field(obj,name));
    Vector2 ToWorld(Vector2 v) => scenario.q == 0 ? v : new Vector2(v.y,-v.x);
    void Write(Entry entry)
    {
        entry.scenario = entry.scenario ?? scenario;
        entry.episode = episode; entry.step=step;
        entry.simulated_time=active ? Time.fixedTime-beganAt : 0;
        File.AppendAllText(Path.Combine(config.root,"trace.jsonl"),JsonUtility.ToJson(entry)+"\n");
    }
    void FixedUpdate()
    {
        if(!initialized || finishing) return;
        try
        {
            if(DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= config.deadline_epoch) throw new Exception("Approved deadline reached");
            // Disable ordinary policy adapters in this isolated process, preventing heuristic or model action races.
            foreach(var b in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                if(b != null && b.GetType()==agentType && b.enabled) b.enabled=false;
            if(!active)
            {
                if(index>=scenarios.Count) { Finish(); return; }
                TryPrepare();
                if(!active) return;
            }
            ObserveProjectiles();
            if(step>=250)
            {
                typeof(Level).GetMethod("LevelTimeOut",Hidden).Invoke(level,null);
                return;
            }
            if(step%5==0) Decide();
            step++;
        }
        catch(Exception e) { Fail(e); }
    }
    void TryPrepare()
    {
        var ships=UnityEngine.Object.FindObjectsByType<Ship>(FindObjectsSortMode.None);
        bee=ships.FirstOrDefault(s=>!s.IsDead && s.Level!=null && s.ShipType.ToString()=="Bumblebee" && s.Body!=null);
        if(bee==null) return;
        target=ships.FirstOrDefault(s=>!s.IsDead && s.Level==bee.Level && s.ShipType.ToString()=="Gunship" && s.Body!=null);
        if(target==null || bee.Weapons.Count!=1 || bee.Turrets.Count!=1 || bee.Level.IsRestarting || bee.Level.State.GameOver) return;
        level=bee.Level;
        coordinator=level.GetComponent(coordinatorType);
        if(coordinator==null || !Bool(coordinator,"_episodeActive")) return;
        episode=Int(coordinator,"_episodeNumber");
        if(episode==lastEpisode) return;
        if(level.State.Projectiles.Count!=0) throw new Exception("New scenario contains surviving projectiles");
        scenario=scenarios[index]; step=0; projectiles.Clear();
        Vector2 center=new Vector2((level.MinX+level.MaxX)/2,(level.MinY+level.MaxY)/2);
        Vector2 relative=new Vector2(Mathf.Cos(scenario.bearing*Mathf.Deg2Rad),Mathf.Sin(scenario.bearing*Mathf.Deg2Rad))*scenario.distance;
        Position(bee,center,scenario.q*90);
        Position(target,center+ToWorld(relative),scenario.bearing+scenario.q*90);
        bee.HasTargetCoordinates=false; target.HasTargetCoordinates=false;
        bee.HasTargetDirection=false; target.HasTargetDirection=false;
        bee.IsCeaseFire=false; target.IsCeaseFire=true;
        bee.SetCurrentSpeed(bee.Speed);
        target.SetCurrentSpeed(scenario.speed);
        move.Invoke(null,new object[]{bee,Vector2.zero});
        // Obtain the commanded target velocity through the same primitive movement interface.
        Vector2 targetDirection=new Vector2(-Mathf.Sin(target.Rotation*Mathf.Deg2Rad),Mathf.Cos(target.Rotation*Mathf.Deg2Rad));
        move.Invoke(null,new object[]{target,scenario.speed==0?Vector2.zero:targetDirection});
        typeof(Ship).GetMethod("RlDirectionalMovement",Hidden).Invoke(target,null);
        bee.Body.linearVelocity=Vector2.zero;
        var turret=bee.Turrets[0];
        turret.Rotation=scenario.q*90; turret.PieceTransform.eulerAngles=new Vector3(0,0,turret.Rotation);
        turret.ReadyToFire=false; turret.TargetingPasses=0; turret.IsAlignedWithTargetPoint=false;
        heldAim=new Vector2(-Mathf.Sin(turret.Rotation*Mathf.Deg2Rad),Mathf.Cos(turret.Rotation*Mathf.Deg2Rad));
        // Match the initial cooldown phase without bypassing the production targeting clock.
        var timer=typeof(Assets.Scripts.Entities.Ships.Weapons.Turret).GetField("_targetingSequenceTimer",Hidden).GetValue(turret);
        timer.GetType().GetField("Elapsed",Hidden).SetValue(timer,0f);
        // Remove exploration left by the randomized pre-intervention spawn.
        foreach(string field in new[]{"_beeExplorationGrid","_humanExplorationGrid"})
        {
            var grid=Field(coordinator,field);
            grid.GetType().GetMethod("Reset",Hidden).Invoke(grid,null);
        }
        coordinatorType.GetMethod("RefreshExplorationGrids",Hidden).Invoke(coordinator,new object[]{level,true});
        // This explicitly defines the known-from-start condition; no live training cache is involved.
        level.State.VisionCache[bee.Side-1].Add(target);
        level.State.VisionCache[target.Side-1].Add(bee);
        Physics2D.SyncTransforms();
        active=true; beganAt=Time.fixedTime;
        Write(new Entry{kind="scenario_start",observer_position=bee.GetPosition(),target_position=target.GetPosition(),target_velocity=target.Body.linearVelocity,health=target.Health});
    }
    static void Position(Ship ship,Vector2 local,float heading)
    {
        ship.Transform.localPosition=local;
        ship.Body.position=ship.Level.Map.Transform.TransformPoint(local);
        ship.Rotation=heading;
        ship.Transform.eulerAngles=new Vector3(0,0,heading);
        ship.Body.linearVelocity=Vector2.zero;
        ship.Body.angularVelocity=0;
    }
    void Decide()
    {
        var sensor=new VectorSensor(7743);
        collect.Invoke(null,new object[]{perception,bee,bee.Side,sensor,scenario.q,0});
        float[] observations=((IEnumerable<float>)sensorValues.Invoke(sensor,null)).ToArray();
        if(observations.Length!=7743) throw new Exception("Observation ABI mismatch");
        float[] mask={1,1,1,0,1,0,1,0,1,0,1,0,0,0,0};
        long id=++sequence;
        server.StandardInput.WriteLine(JsonUtility.ToJson(new Request{sequence=id,observations=observations,action_masks=mask}));
        server.StandardInput.Flush();
        var reply=ReadReply();
        if(reply.sequence!=id || reply.continuous==null || reply.continuous.Length<4 || reply.discrete==null || reply.discrete.Length!=6)
            throw new Exception("Inference reply ABI mismatch");
        Vector2 aim=new Vector2(reply.continuous[2],reply.continuous[3]);
        if(aim.sqrMagnitude>=.01f) heldAim=ToWorld(aim.normalized);
        bool fire=reply.discrete[0]==1;
        if(scenario.arm=="oracle") fire=Intercept(out heldAim);
        move.Invoke(null,new object[]{bee,Vector2.zero});
        weapon.Invoke(null,new object[]{bee,0,heldAim,fire});
        var turret=bee.Turrets[0];
        Write(new Entry{kind="decision",sequence=id,observations=observations,continuous=reply.continuous,discrete=reply.discrete,
            observer_position=bee.GetPosition(),target_position=target.GetPosition(),target_velocity=target.Body.linearVelocity,
            aim=heldAim,fire=fire,ready=turret.ReadyToFire,aligned=turret.IsAlignedWithTargetPoint,turret_heading=turret.Rotation});
    }
    bool Intercept(out Vector2 direction)
    {
        Vector2 r=target.GetPosition()-bee.Turrets[0].GetPosition(),v=target.Body.linearVelocity;
        double speed=bee.Stage.Prefabs.BumblebeeShotPrefab.GetComponent<Projectile>().Speed;
        double a=Vector2.Dot(v,v)-speed*speed,b=2*Vector2.Dot(r,v),c=Vector2.Dot(r,r);
        double discriminant=b*b-4*a*c;
        direction=r.normalized;
        if(discriminant<0 || Math.Abs(a)<1e-9) return false;
        double t1=(-b-Math.Sqrt(discriminant))/(2*a),t2=(-b+Math.Sqrt(discriminant))/(2*a);
        double t=t1>0?t1:t2;
        if(t<=0) return false;
        direction=(r+v*(float)t).normalized;
        return speed*t<=bee.Weapons[0].Range;
    }
    void ObserveProjectiles()
    {
        foreach(var p in level.State.Projectiles)
        {
            if(p==null || p.IsDead || p.Shooter!=bee) continue;
            if(!projectiles.TryGetValue(p.Id,out var entry))
            {
                entry=new Entry{kind="launch",root_id=p.RlRootProjectileId,projectile_id=p.Id,
                    launch_position=p.StartingPosition,launch_velocity=p.Body!=null?p.Body.linearVelocity:Vector2.zero,
                    min_sampled_center_distance=float.MaxValue};
                projectiles.Add(p.Id,entry); Write(entry);
            }
            if(target!=null && !target.IsDead)
                entry.min_sampled_center_distance=Mathf.Min(entry.min_sampled_center_distance,Vector2.Distance(p.GetPosition(),target.GetPosition()));
        }
    }
    public void EpisodeEnded(object endedLevel,object result)
    {
        if(!active || !ReferenceEquals(endedLevel,level)) return;
        try
        {
            ObserveProjectiles();
            Write(new Entry{kind="result",shots=Int(result,"BeeShotsFired"),hits=Int(result,"BeeShotsHit"),
                damage=Int(result,"BeeDamageDealt"),timed_out=Bool(result,"TimedOut"),completed=index+1});
            foreach(var p in projectiles.Values) {p.kind="sampled_closest_approach";Write(p);}
            lastEpisode=episode; active=false; index++;
        }
        catch(Exception e) { Fail(e); }
    }
    void Finish()
    {
        if(finishing) return; finishing=true;
        Write(new Entry{kind="complete",completed=index,reason="Authoritative episode outcomes recorded"});
        File.WriteAllText(Path.Combine(config.root,"complete.json"),JsonUtility.ToJson(new Entry{kind="complete",completed=index}));
        Shutdown(0);
    }
    void Fail(Exception error)
    {
        Debug.LogException(error);
        if(config!=null && Directory.Exists(config.root)) File.AppendAllText(Path.Combine(config.root,"failure.log"),error+"\n");
        finishing=true; Shutdown(4);
    }
    void Shutdown(int status)
    {
        if(endedHandler!=null) endedEvent.GetRemoveMethod(true).Invoke(null,new object[]{endedHandler});
        if(server!=null && !server.HasExited) {server.StandardInput.Close();if(!server.WaitForExit(1500))server.Kill();}
        // Academy writes its timer file using the active scene name on disposal. Avoid the ordinary scene's timer filename.
        var outputScene=SceneManager.CreateScene("BeesShotInvestigation");
        SceneManager.SetActiveScene(outputScene);
        EditorApplication.Exit(status);
    }
}
