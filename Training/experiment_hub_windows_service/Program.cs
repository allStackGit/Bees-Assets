// OS-owned process recovery. Coordinator/agent children keep their existing
// owners; independent evaluators use a CPU/RAM-bounded whole-process-tree job.
using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record Configuration(string Name,string Role,string NodePath,
    string TrainingDirectory,string? EnvironmentFile,string StateDirectory,
    int? Cpus=null,int? RamMb=null);

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
        using(var child=new NodeProcess(config,entrypoint,!service&&config.Role=="evaluator"))
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
            if(OperatingSystem.IsWindows()&&args.Length==3&&args[0]=="--probe-evaluator")
                return NodeProcess.ProbeEvaluator(args[1],uint.Parse(args[2]));
            if(!OperatingSystem.IsWindows()||args.Length!=2||args[0] is not ("--service" or "--run-once"))
                throw new ArgumentException("Usage: --service or --run-once <absolute-local-config.json>");
            string file=Local(args[1]);
            if(new FileInfo(file).Length>16384)throw new ArgumentException("Configuration is oversized.");
            config=JsonSerializer.Deserialize<Configuration>(File.ReadAllText(file),
                new JsonSerializerOptions {UnmappedMemberHandling=
                    System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow})
                ??throw new ArgumentException("Configuration is missing.");
            if(!Regex.IsMatch(config.Name??"",@"^Bees-ExperimentHub-[A-Za-z0-9-]{1,64}$")||
                config.Role is not ("agent" or "coordinator" or "evaluator"))
                throw new ArgumentException("Invalid service identity or role.");
            config=config with {NodePath=Local(config.NodePath),
                TrainingDirectory=Local(config.TrainingDirectory,true),
                StateDirectory=Local(config.StateDirectory,true),
                EnvironmentFile=config.EnvironmentFile is null?null:Local(config.EnvironmentFile)};
            if((config.Role is "agent" or "evaluator")&&config.EnvironmentFile is null)
                throw new ArgumentException("Agent/evaluator service requires a private environment file.");
            if(config.Role=="evaluator")
            {
                if(config.Cpus is not (>=1 and <=64)||config.Cpus>Environment.ProcessorCount||
                    config.RamMb is not (>=512 and <=1048576)||
                    config.RamMb>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes/1048576)
                    throw new ArgumentException("Evaluator requires available CPU and RAM budgets.");
            }
            else if(config.Cpus is not null||config.RamMb is not null)
                throw new ArgumentException("Service resource budgets apply only to independent evaluators.");
            entrypoint=Local(Path.Combine(config.TrainingDirectory,
                config.Role=="agent"?"experiment_hub_agent.js":config.Role=="evaluator"
                    ?"experiment_hub_evaluator_windows.js":"bees_experiments.js"));
            _=Local(Path.Combine(config.TrainingDirectory,"experiment_hub_service_console.js"));
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
    public NodeProcess(Configuration config,string entry,bool once=false)
    {
        Native.ProcessInfo info=default;
        try
        {
            bool evaluator=config.Role=="evaluator";
            job=Native.CreateJobObject(IntPtr.Zero,evaluator?EvaluatorJobName(config.Name):null);
            if(job==IntPtr.Zero)throw Native.Error("Cannot create direct-process ownership job");
            if(evaluator&&Marshal.GetLastWin32Error()==183)
                throw new InvalidOperationException("Evaluator service already owns its native job.");
            // Silent breakaway applies to descendants, not the assigned Node.
            var limits=new Native.ExtendedLimits();
            limits.Basic.Flags=evaluator?0x2000u|0x200u:0x2000u|0x1000u;
            if(evaluator)limits.JobMemory=(UIntPtr)checked((ulong)config.RamMb!.Value*1048576UL);
            if(!Native.SetInformationJobObject(job,9,ref limits,
                (uint)Marshal.SizeOf<Native.ExtendedLimits>()))
                throw Native.Error("Cannot configure direct-process ownership");
            var observed=new Native.ExtendedLimits();
            if(!Native.QueryInformationJobObject(job,9,ref observed,
                (uint)Marshal.SizeOf<Native.ExtendedLimits>(),out _)||
                observed.Basic.Flags!=limits.Basic.Flags||observed.JobMemory!=limits.JobMemory)
                throw new InvalidOperationException("Direct-process ownership settings mismatch");
            if(evaluator)
            {
                uint cores=Native.GetActiveProcessorCount(0xffff);
                if(cores==0)throw Native.Error("Cannot determine total system logical processors");
                var cpu=new Native.CpuLimits {Flags=5,
                    Rate=(uint)Math.Max(1,Math.Floor(10000.0*config.Cpus!.Value/cores))};
                if(!Native.SetInformationJobObject(job,15,ref cpu,(uint)Marshal.SizeOf<Native.CpuLimits>())||
                    !Native.QueryInformationJobObject(job,15,out Native.CpuLimits checkedCpu,
                        (uint)Marshal.SizeOf<Native.CpuLimits>(),out _)||
                    checkedCpu.Flags!=cpu.Flags||checkedCpu.Rate!=cpu.Rate)
                    throw Native.Error("Evaluator CPU hard cap not verified");
            }
            var args=new List<string>{config.NodePath};
            if(config.EnvironmentFile is not null)args.Add("--env-file="+config.EnvironmentFile);
            args.Add("--require");
            args.Add(Path.Combine(config.TrainingDirectory,"experiment_hub_service_console.js"));
            args.Add(entry);
            if(once)args.Add("--once");
            var variables=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(DictionaryEntry pair in Environment.GetEnvironmentVariables())
            {
                string key=(string)pair.Key;
                if(!key.StartsWith("=")&&
                    !(config.EnvironmentFile is not null&&
                    key.StartsWith("EXPERIMENT_HUB_",StringComparison.OrdinalIgnoreCase)))
                    variables[key]=(string?)pair.Value??"";
            }
            // These destinations come from the reviewed service configuration,
            // not an environment file or an experiment's parameters.
            variables["EXPERIMENT_HUB_SERVICE_LOG_ROOT"]=config.StateDirectory;
            variables["EXPERIMENT_HUB_SERVICE_NAME"]=config.Name;
            if(evaluator)
            {
                variables["EXPERIMENT_HUB_SERVICE_EXECUTABLE"]=Environment.ProcessPath!;
                variables["EXPERIMENT_HUB_EVALUATOR_CPUS"]=config.Cpus!.Value.ToString();
                variables["EXPERIMENT_HUB_EVALUATOR_RAM_MB"]=config.RamMb!.Value.ToString();
                variables["CUDA_VISIBLE_DEVICES"]="";
                variables["HIP_VISIBLE_DEVICES"]="";
                variables["ROCR_VISIBLE_DEVICES"]="";
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
    static string EvaluatorJobName(string name)=>"Local\\bees-hub-evaluator-"+name.ToLowerInvariant();
    internal static int ProbeEvaluator(string name,uint pid)
    {
        if(!Regex.IsMatch(name,@"^Bees-ExperimentHub-[A-Za-z0-9-]{1,64}$")||pid==0)
            throw new ArgumentException("Invalid evaluator identity.");
        IntPtr owned=Native.OpenJobObject(4,false,EvaluatorJobName(name));
        if(owned==IntPtr.Zero)throw Native.Error("No native evaluator job");
        IntPtr caller=IntPtr.Zero;
        try
        {
            caller=Native.OpenProcess(0x1000,false,pid);
            if(caller==IntPtr.Zero||!Native.IsProcessInJob(caller,owned,out bool member)||!member||
                !Native.IsProcessInJob(Native.GetCurrentProcess(),owned,out bool probeMember)||!probeMember)
                throw Native.Error("Evaluator controller and probe must belong to the native job");
            var limits=new Native.ExtendedLimits();
            if(!Native.QueryInformationJobObject(owned,9,ref limits,
                    (uint)Marshal.SizeOf<Native.ExtendedLimits>(),out _)||limits.Basic.Flags!=0x2200||
                !Native.QueryInformationJobObject(owned,15,out Native.CpuLimits cpu,
                    (uint)Marshal.SizeOf<Native.CpuLimits>(),out _)||cpu.Flags!=5||
                !Native.QueryInformationJobObject(owned,1,out Native.Accounting accounting,
                    (uint)Marshal.SizeOf<Native.Accounting>(),out _))
                throw Native.Error("Native evaluator resource ownership not verified");
            long ticks=accounting.UserTime+accounting.KernelTime;
            var members=EvaluatorProcesses(owned);
            Console.WriteLine(JsonSerializer.Serialize(new {
                verified=true,cpu_rate_units=cpu.Rate,
                host_logical_processors=Native.GetActiveProcessorCount(0xffff),
                ram_limit_mb=limits.JobMemory.ToUInt64()/1048576,
                process_tasks=accounting.ActiveProcesses,
                application_process_tasks=members.Count(member=>!member.system_console_host),
                processes=members,
                cpu_time_ns=ticks>=0&&ticks<=90071992547409L?(long?)ticks*100:null,
                peak_job_memory_bytes=limits.PeakJob.ToUInt64()
            }));
            return 0;
        }
        finally {if(caller!=IntPtr.Zero)Native.CloseHandle(caller);Native.CloseHandle(owned);}
    }
    sealed record EvaluatorMember(uint pid,string image,bool system_console_host);
    static EvaluatorMember[] EvaluatorProcesses(IntPtr owned)
    {
        const int length=8+4096*8;
        IntPtr buffer=Marshal.AllocHGlobal(length);
        try
        {
            if(!Native.QueryInformationJobObject(owned,3,buffer,length,out _))
                throw Native.Error("Cannot enumerate evaluator job members");
            int count=Marshal.ReadInt32(buffer,4);
            if(count<0||count>4096)throw new InvalidOperationException("Invalid evaluator process count");
            var members=new List<EvaluatorMember>();
            for(int index=0;index<count;index++)
            {
                uint id=checked((uint)Marshal.ReadIntPtr(buffer,8+index*IntPtr.Size).ToInt64());
                IntPtr process=Native.OpenProcess(0x1000,false,id);
                if(process==IntPtr.Zero)
                {
                    if(Marshal.GetLastWin32Error()==87)continue; // Already exited.
                    throw Native.Error("Cannot inspect evaluator process identity");
                }
                try
                {
                    var image=new StringBuilder(4096);uint size=4096;
                    if(!Native.QueryFullProcessImageName(process,0,image,ref size))
                        throw Native.Error("Cannot verify evaluator process image");
                    bool console=string.Equals(image.ToString(),
                        Path.Combine(Environment.SystemDirectory,"conhost.exe"),
                        StringComparison.OrdinalIgnoreCase);
                    members.Add(new EvaluatorMember(id,Path.GetFileName(image.ToString()),console));
                }
                finally {Native.CloseHandle(process);}
            }
            return members.ToArray();
        }
        finally {Marshal.FreeHGlobal(buffer);}
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
    [StructLayout(LayoutKind.Sequential)]
    internal struct CpuLimits {public uint Flags,Rate;}
    [StructLayout(LayoutKind.Sequential)]
    internal struct Accounting {
        public long UserTime,KernelTime,PeriodUserTime,PeriodKernelTime;
        public uint PageFaults,Processes,ActiveProcesses,TerminatedProcesses;
    }
    [DllImport("kernel32.dll",EntryPoint="CreateJobObjectW",CharSet=CharSet.Unicode,SetLastError=true)]
    internal static extern IntPtr CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",EntryPoint="OpenJobObjectW",CharSet=CharSet.Unicode,SetLastError=true)]
    internal static extern IntPtr OpenJobObject(uint access,bool inherit,string name);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",EntryPoint="QueryFullProcessImageNameW",CharSet=CharSet.Unicode,SetLastError=true)]
    internal static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint size);
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern bool SetInformationJobObject(IntPtr job,int kind,ref CpuLimits limits,uint length);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern bool QueryInformationJobObject(IntPtr job,int kind,
        out CpuLimits limits,uint length,out uint returned);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern bool QueryInformationJobObject(IntPtr job,int kind,
        out Accounting limits,uint length,out uint returned);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern bool QueryInformationJobObject(IntPtr job,int kind,
        IntPtr buffer,int length,out uint returned);
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
