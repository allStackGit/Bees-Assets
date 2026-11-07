// OS-owned process recovery. The service owns only its Node process;
// children break away so Bees and native training keep their existing owners.
using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record Configuration(string Name,string Role,string NodePath,
    string TrainingDirectory,string? EnvironmentFile,string StateDirectory);

internal static class HubService
{
    [StructLayout(LayoutKind.Sequential)]
    struct Status { public uint Type,State,Accepted,Error,Specific,Checkpoint,WaitHint; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void ServiceMain(uint argc,IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate uint Handler(uint control,uint eventType,IntPtr eventData,IntPtr context);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct Entry { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; public ServiceMain? Main; }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern bool StartServiceCtrlDispatcher(Entry[] table);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern IntPtr RegisterServiceCtrlHandlerEx(string name,Handler handler,IntPtr context);
    [DllImport("advapi32.dll",SetLastError=true)]
    static extern bool SetServiceStatus(IntPtr handle,ref Status status);

    static readonly ManualResetEvent Stop=new(false);
    static readonly Handler Control=OnControl;
    static readonly ServiceMain MainCallback=Run;
    static readonly object StatusLock=new();
    static IntPtr statusHandle;
    static Configuration config=null!;
    static bool terminal;
    static uint childPid;
    static string entrypoint="";

    static string Local(string value,bool directory=false)
    {
        if(!Path.IsPathFullyQualified(value)||value.StartsWith(@"\\")||
            value.IndexOfAny(new[]{'\0','\r','\n','"'})>=0)
            throw new ArgumentException("Expected an absolute local path.");
        string full=Path.GetFullPath(value);
        if(directory?!Directory.Exists(full):!File.Exists(full))
            throw new ArgumentException("Configured local path is unavailable: "+full);
        return full;
    }
    static void Report(uint state,uint error=0)
    {
        lock(StatusLock)
        {
            if(terminal)return;
            var status=new Status {Type=0x10,State=state,Accepted=state==4?5u:0u,
                Error=error,WaitHint=state is 2 or 3?10000u:0u,
                Checkpoint=state is 2 or 3?1u:0u};
            if(!SetServiceStatus(statusHandle,ref status))throw Native.Error("Cannot report service state");
            terminal=state==1;
        }
    }
    static uint OnControl(uint control,uint eventType,IntPtr eventData,IntPtr context)
    {
        if(control is 1 or 5)
        {
            // Only ServiceMain reports the final stopped state.
            Stop.Set();
        }
        return 0;
    }
    static void Record(string state,uint? exit=null,string? failure=null)
    {
        var file=Path.Combine(config.StateDirectory,config.Name+".json");
        var pending=file+".pending";
        File.WriteAllText(pending,JsonSerializer.Serialize(new {
            schema_version=1,service=config.Name,role=config.Role,state,
            observed_at=DateTime.UtcNow.ToString("O"),node_pid=childPid,
            child_exit_code=exit,failure
        })+"\n",new UTF8Encoding(false));
        File.Move(pending,file,true);
    }
    static void Run(uint argc,IntPtr argv)
    {
        statusHandle=RegisterServiceCtrlHandlerEx(config.Name,Control,IntPtr.Zero);
        if(statusHandle==IntPtr.Zero)Environment.Exit(1);
        uint result=0;
        try
        {
            Report(2);
            result=RunChild(true);
        }
        catch(Exception error)
        {
            result=1;
            try { Record("failed",null,error.Message[..Math.Min(error.Message.Length,1024)]); }
            catch(Exception) { /* SCM still receives the service failure. */ }
        }
        if(result!=0)
        {
            // A child failure is a service failure, after closing the direct
            // child ownership handle. SCM, not a retry loop here, restarts us.
            Environment.Exit(1);
        }
        Report(1);
    }
    static uint RunChild(bool service)
    {
        uint result=0;
        using(var child=new NodeProcess(config,entrypoint))
            {
                childPid=child.Pid;
                Record("running");
                if(service)Report(4);
                var handles=new[]{child.Handle,Stop.SafeWaitHandle.DangerousGetHandle()};
                uint waited=Native.WaitForMultipleObjects(2,handles,false,uint.MaxValue);
                if(waited==1||Stop.WaitOne(0))
                {
                    if(service)Report(3);
                    child.Terminate();
                    Record("stopped");
                }
                else if(waited==0)
                {
                    result=child.ExitCode();
                    Record(result==0?"completed":"failed",result);
                }
                else throw Native.Error("Service process wait failed");
            }
        return result;
    }
    static int Main(string[] args)
    {
        try
        {
            if(!OperatingSystem.IsWindows()||args.Length!=2||args[0] is not ("--service" or "--run-once"))
                throw new ArgumentException("Usage: --service or --run-once <absolute-local-config.json>");
            string file=Local(args[1]);
            if(new FileInfo(file).Length>16384)throw new ArgumentException("Configuration is oversized.");
            config=JsonSerializer.Deserialize<Configuration>(File.ReadAllText(file),
                new JsonSerializerOptions {UnmappedMemberHandling=
                    System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow})
                ??throw new ArgumentException("Configuration is missing.");
            if(!Regex.IsMatch(config.Name??"",@"^Bees-ExperimentHub-[A-Za-z0-9-]{1,64}$")||
                config.Role is not ("agent" or "coordinator"))
                throw new ArgumentException("Invalid service identity or role.");
            config=config with {NodePath=Local(config.NodePath),
                TrainingDirectory=Local(config.TrainingDirectory,true),
                StateDirectory=Local(config.StateDirectory,true),
                EnvironmentFile=config.EnvironmentFile is null?null:Local(config.EnvironmentFile)};
            if(config.Role=="agent"&&config.EnvironmentFile is null)
                throw new ArgumentException("Agent service requires a private environment file.");
            entrypoint=Local(Path.Combine(config.TrainingDirectory,
                config.Role=="agent"?"experiment_hub_agent.js":"bees_experiments.js"));
            if(args[0]=="--run-once")
            {
                Console.CancelKeyPress+=(_,eventArgs)=>{eventArgs.Cancel=true;Stop.Set();};
                return unchecked((int)RunChild(false));
            }
            if(!StartServiceCtrlDispatcher(new[]{
                new Entry {Name=config.Name,Main=MainCallback},new Entry()}))
                throw Native.Error("Cannot connect to Windows Service Control Manager");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
}

internal sealed class NodeProcess : IDisposable
{
    IntPtr job,process;
    public IntPtr Handle=>process;
    public uint Pid {get;private set;}
    public NodeProcess(Configuration config,string entry)
    {
        Native.ProcessInfo info=default;
        try
        {
            job=Native.CreateJobObject(IntPtr.Zero,null);
            if(job==IntPtr.Zero)throw Native.Error("Cannot create direct-process ownership job");
            // Silent breakaway applies to descendants, not the assigned Node.
            var limits=new Native.ExtendedLimits();
            limits.Basic.Flags=0x2000|0x1000; // KILL_ON_JOB_CLOSE | SILENT_BREAKAWAY_OK.
            if(!Native.SetInformationJobObject(job,9,ref limits,
                (uint)Marshal.SizeOf<Native.ExtendedLimits>()))
                throw Native.Error("Cannot configure direct-process ownership");
            var observed=new Native.ExtendedLimits();
            if(!Native.QueryInformationJobObject(job,9,ref observed,
                (uint)Marshal.SizeOf<Native.ExtendedLimits>(),out _)||
                observed.Basic.Flags!=limits.Basic.Flags)
                throw new InvalidOperationException("Direct-process ownership settings mismatch");
            var args=new List<string>{config.NodePath};
            if(config.EnvironmentFile is not null)args.Add("--env-file="+config.EnvironmentFile);
            args.Add(entry);
            var variables=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(DictionaryEntry pair in Environment.GetEnvironmentVariables())
            {
                string key=(string)pair.Key;
                if(!key.StartsWith("=")&&
                    !(config.EnvironmentFile is not null&&
                    key.StartsWith("EXPERIMENT_HUB_",StringComparison.OrdinalIgnoreCase)))
                    variables[key]=(string?)pair.Value??"";
            }
            var block=string.Join("\0",variables.Select(x=>x.Key+"="+x.Value))+"\0\0";
            IntPtr environment=Marshal.StringToHGlobalUni(block);
            try
            {
                var startup=new Native.StartupInfo {Size=Marshal.SizeOf<Native.StartupInfo>()};
                if(!Native.CreateProcess(config.NodePath,
                    new StringBuilder(string.Join(" ",args.Select(Quote))),
                    IntPtr.Zero,IntPtr.Zero,false,0x4|0x400|0x08000000,
                    environment,config.TrainingDirectory,ref startup,out info))
                    throw Native.Error("Cannot create suspended Node process");
            }
            finally {Marshal.FreeHGlobal(environment);}
            process=info.Process;Pid=info.ProcessId;
            if(!Native.AssignProcessToJobObject(job,process)||
                !Native.IsProcessInJob(process,job,out bool assigned)||!assigned)
                throw Native.Error("Cannot verify direct-process ownership");
            if(Native.ResumeThread(info.Thread)==uint.MaxValue)
                throw Native.Error("Cannot resume Node process");
        }
        catch
        {
            if(process!=IntPtr.Zero)Native.TerminateProcess(process,1);
            Dispose();throw;
        }
        finally {if(info.Thread!=IntPtr.Zero)Native.CloseHandle(info.Thread);}
    }
    static string Quote(string input)
    {
        if(input.IndexOfAny(new[]{'\0','\r','\n','"'})>=0||input.EndsWith('\\'))
            throw new ArgumentException("Unsafe file argument.");
        return "\""+input+"\"";
    }
    public uint ExitCode()
    {
        if(!Native.GetExitCodeProcess(process,out uint code))throw Native.Error("Cannot read Node exit code");
        return code;
    }
    public void Terminate()
    {
        if(Native.WaitForSingleObject(process,0)==0)return;
        if(!Native.TerminateProcess(process,0))throw Native.Error("Cannot stop the owned Node process");
        if(Native.WaitForSingleObject(process,10000)!=0)
            throw Native.Error("Owned Node process did not stop");
    }
    public void Dispose()
    {
        if(job!=IntPtr.Zero){Native.CloseHandle(job);job=IntPtr.Zero;}
        if(process!=IntPtr.Zero){Native.CloseHandle(process);process=IntPtr.Zero;}
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    internal struct StartupInfo {
        public int Size; public string? Reserved,Desktop,Title;
        public uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags;
        public short Show,ReservedSize;public IntPtr ReservedPointer,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInfo {public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimits {
        public long ProcessTime,JobTime; public uint Flags;
        public UIntPtr MinWorking,MaxWorking;public uint ActiveLimit;
        public UIntPtr Affinity;public uint Priority,Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters {public ulong Read,Write,Other,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimits {
        public BasicLimits Basic;public IoCounters Io;
        public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;
    }
    [DllImport("kernel32.dll",EntryPoint="CreateJobObjectW",CharSet=CharSet.Unicode,SetLastError=true)]
    internal static extern IntPtr CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern bool SetInformationJobObject(IntPtr job,int kind,ref ExtendedLimits limits,uint length);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern bool QueryInformationJobObject(IntPtr job,int kind,
        ref ExtendedLimits limits,uint length,out uint returned);
    [DllImport("kernel32.dll",EntryPoint="CreateProcessW",CharSet=CharSet.Unicode,SetLastError=true)]
    internal static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,
        IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string cwd,
        ref StartupInfo startup,out ProcessInfo info);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool assigned);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern uint WaitForMultipleObjects(uint count,IntPtr[] handles,bool all,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(IntPtr handle);
    internal static Exception Error(string message)=>new Win32Exception(Marshal.GetLastWin32Error(),message);
}
