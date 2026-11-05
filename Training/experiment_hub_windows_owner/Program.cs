// Machine-local Windows process owner. No Hub network socket, shell,
// privileged service, PowerShell, or server-supplied executable selection.
// Build locally with dotnet publish -c Release -r win-x64 --self-contained true.
// This executable must be installed and SHA-256 pinned by the host admin.
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal sealed class JobSpec
{
    public string JobId { get; set; } = "";
    public string Token { get; set; } = "";
    public string Executable { get; set; } = "";
    public string[] Args { get; set; } = Array.Empty<string>();
    public string Cwd { get; set; } = "";
    public int Cpus { get; set; }
    public int HostCpus { get; set; }
    public int RamMb { get; set; }
    public string ReceiptPath { get; set; } = "";
}
internal sealed class PipeRequest
{
    public string Token { get; set; } = "";
    public string Action { get; set; } = "";
}
internal static class Native
{
    internal const uint CREATE_SUSPENDED=0x00000004;
    internal const uint CREATE_NO_WINDOW=0x08000000;
    internal const uint CREATE_UNICODE_ENVIRONMENT=0x00000400;
    internal const uint WAIT_OBJECT_0=0;
    internal const uint WAIT_TIMEOUT=258;
    internal const uint JOB_OBJECT_LIMIT_JOB_MEMORY=0x00000200;
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE=0x00002000;
    internal const uint CPU_RATE_CONTROL_ENABLE=0x1;
    internal const uint CPU_RATE_CONTROL_HARD_CAP=0x4;
    internal const int JobObjectBasicAccountingInformation=1;
    internal const int JobObjectExtendedLimitInformation=9;
    internal const int JobObjectCpuRateControlInformation=15;

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        internal int cb;
        internal string? lpReserved,lpDesktop,lpTitle;
        internal uint dwX,dwY,dwXSize,dwYSize,dwXCountChars,dwYCountChars;
        internal uint dwFillAttribute,dwFlags;
        internal short wShowWindow,cbReserved2;
        internal IntPtr lpReserved2,hStdInput,hStdOutput,hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        internal IntPtr hProcess,hThread;
        internal uint dwProcessId,dwThreadId;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimits
    {
        internal long PerProcessUserTimeLimit,PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize,MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass,SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        internal ulong ReadOperationCount,WriteOperationCount,OtherOperationCount;
        internal ulong ReadTransferCount,WriteTransferCount,OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimits
    {
        internal BasicLimits BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit,JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed,PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CpuLimits
    {
        internal uint ControlFlags,CpuRate;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Accounting
    {
        internal long TotalUserTime,TotalKernelTime;
        internal long ThisPeriodTotalUserTime,ThisPeriodTotalKernelTime;
        internal uint TotalPageFaultCount,TotalProcesses,ActiveProcesses,TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll",EntryPoint="CreateJobObjectW",CharSet=CharSet.Unicode,SetLastError=true)]
    internal static extern IntPtr CreateJobObject(IntPtr securityAttributes,string name);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(IntPtr job,int infoClass,
        ref ExtendedLimits info,uint length);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(IntPtr job,int infoClass,
        ref CpuLimits info,uint length);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryInformationJobObject(IntPtr job,int infoClass,
        ref ExtendedLimits info,uint length,out uint returned);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryInformationJobObject(IntPtr job,int infoClass,
        ref CpuLimits info,uint length,out uint returned);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryInformationJobObject(IntPtr job,int infoClass,
        ref Accounting info,uint length,out uint returned);
    [DllImport("kernel32.dll",EntryPoint="CreateProcessW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(string applicationName,
        StringBuilder commandLine,IntPtr processAttributes,IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)]bool inheritHandles,uint flags,
        IntPtr environment,string? cwd,ref STARTUPINFO startup,
        out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessInJob(IntPtr process,IntPtr job,
        [MarshalAs(UnmanagedType.Bool)]out bool result);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(IntPtr job,uint exitCode);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(IntPtr process,uint exitCode);
    [DllImport("kernel32.dll",SetLastError=true)]
    internal static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    internal static Exception Error(string message)=>
        new Win32Exception(Marshal.GetLastWin32Error(),message);
}
internal sealed class OwnedJob : IDisposable
{
    private IntPtr job=IntPtr.Zero,process=IntPtr.Zero;
    private bool stopped=false,closed=false;
    private readonly JobSpec spec;
    private readonly uint cpuRate;
    private readonly string identity;

    internal OwnedJob(JobSpec input)
    {
        spec=input;
        identity="Local\\experiment-hub-"+spec.JobId.Replace("-","");
        cpuRate=(uint)Math.Clamp((int)Math.Round(10000.0*spec.Cpus/spec.HostCpus),1,10000);
        try
        {
            job=Native.CreateJobObject(IntPtr.Zero,identity);
            if(job==IntPtr.Zero)throw Native.Error("Cannot create Windows Job Object");
            if(Marshal.GetLastWin32Error()==183)
                throw new InvalidOperationException("Job identity is already owned; never create a duplicate");
            var ext=new Native.ExtendedLimits();
            ext.BasicLimitInformation.LimitFlags=
                Native.JOB_OBJECT_LIMIT_JOB_MEMORY|Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            ext.JobMemoryLimit=(UIntPtr)checked((ulong)spec.RamMb*1048576UL);
            if(!Native.SetInformationJobObject(job,
                Native.JobObjectExtendedLimitInformation,ref ext,
                (uint)Marshal.SizeOf<Native.ExtendedLimits>()))
                throw Native.Error("Unable to apply Job Object memory and kill-on-close limits");
            var cpu=new Native.CpuLimits{ControlFlags=
                Native.CPU_RATE_CONTROL_ENABLE|Native.CPU_RATE_CONTROL_HARD_CAP,
                CpuRate=cpuRate};
            if(!Native.SetInformationJobObject(job,
                Native.JobObjectCpuRateControlInformation,ref cpu,
                (uint)Marshal.SizeOf<Native.CpuLimits>()))
                throw Native.Error("Unable to apply Job Object CPU hard cap");
            VerifyLimits();
            LaunchSuspendedAndAssign();
        }
        catch{Dispose();throw;}
    }
    internal void VerifyLimits()
    {
        var ext=new Native.ExtendedLimits();
        if(!Native.QueryInformationJobObject(job,
            Native.JobObjectExtendedLimitInformation,ref ext,
            (uint)Marshal.SizeOf<Native.ExtendedLimits>(),out _))
            throw Native.Error("Cannot query Job Object memory limits");
        var flags=ext.BasicLimitInformation.LimitFlags;
        if((flags&(Native.JOB_OBJECT_LIMIT_JOB_MEMORY|
            Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE))!=
            (Native.JOB_OBJECT_LIMIT_JOB_MEMORY|Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)||
            ext.JobMemoryLimit.ToUInt64()!=(ulong)spec.RamMb*1048576UL)
            throw new InvalidOperationException("Job Object memory or kill-on-close limit mismatch");
        var cpu=new Native.CpuLimits();
        if(!Native.QueryInformationJobObject(job,
            Native.JobObjectCpuRateControlInformation,ref cpu,
            (uint)Marshal.SizeOf<Native.CpuLimits>(),out _))
            throw Native.Error("Cannot query Job Object CPU hard cap");
        if(cpu.ControlFlags!=(Native.CPU_RATE_CONTROL_ENABLE|
            Native.CPU_RATE_CONTROL_HARD_CAP)||cpu.CpuRate!=cpuRate)
            throw new InvalidOperationException("Job Object CPU quota mismatch");
    }
    private static string Quote(string value)
    {
        if(value.IndexOf('\0')>=0||value.IndexOf('\r')>=0||
            value.IndexOf('\n')>=0||value.Length>1024)
            throw new ArgumentException("Unsafe native argument");
        if(value.Length>0&&value.IndexOfAny(new[]{' ','\t','"'})<0)return value;
        var result=new StringBuilder("\"");
        int slashes=0;
        foreach(char c in value)
        {
            if(c=='\\'){slashes++;continue;}
            if(c=='"')
            {
                result.Append('\\',slashes*2+1);
                result.Append('"');
                slashes=0;
            }
            else
            {
                result.Append('\\',slashes);
                slashes=0;result.Append(c);
            }
        }
        result.Append('\\',slashes*2);
        result.Append('"');
        return result.ToString();
    }
    private void LaunchSuspendedAndAssign()
    {
        // The target cannot execute before successful Job Object assignment.
        // Environment masks GPUs on a best-effort basis, not device isolation.
        var env=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(DictionaryEntry pair in Environment.GetEnvironmentVariables())
        {
            var key=(string)pair.Key;
            if(!key.StartsWith("=")&&!key.Contains('\0'))
                env[key]=(string?)pair.Value??"";
        }
        foreach(var key in new[]{"CUDA_VISIBLE_DEVICES","HIP_VISIBLE_DEVICES",
            "ROCR_VISIBLE_DEVICES","GPU_DEVICE_ORDINAL"})
            env[key]="";
        var block=new StringBuilder();
        foreach(var pair in env)block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        block.Append('\0');
        IntPtr environment=Marshal.StringToHGlobalUni(block.ToString());
        var command=new StringBuilder(Quote(spec.Executable));
        foreach(var arg in spec.Args)command.Append(' ').Append(Quote(arg));
        var startup=new Native.STARTUPINFO{cb=Marshal.SizeOf<Native.STARTUPINFO>()};
        Native.PROCESS_INFORMATION child=default;
        try
        {
            if(!Native.CreateProcess(spec.Executable,command,IntPtr.Zero,
                IntPtr.Zero,false,Native.CREATE_SUSPENDED|
                Native.CREATE_NO_WINDOW|Native.CREATE_UNICODE_ENVIRONMENT,
                environment,spec.Cwd.Length>0?spec.Cwd:null,ref startup,out child))
                throw Native.Error("Cannot create suspended trusted workload");
            process=child.hProcess;
            if(!Native.AssignProcessToJobObject(job,process))
                throw Native.Error("Cannot assign suspended workload to Job Object");
            if(!Native.IsProcessInJob(process,job,out bool assigned)||!assigned)
                throw Native.Error("Suspended workload is not in the owning Job Object");
            VerifyLimits();
            if(Native.ResumeThread(child.hThread)==uint.MaxValue)
                throw Native.Error("Could not resume bounded workload");
        }
        catch
        {
            if(child.hProcess!=IntPtr.Zero)
                Native.TerminateProcess(child.hProcess,1);
            throw;
        }
        finally
        {
            if(child.hThread!=IntPtr.Zero)Native.CloseHandle(child.hThread);
            Marshal.FreeHGlobal(environment);
        }
    }
    private Native.Accounting ReadAccounting()
    {
        VerifyLimits();
        var accounting=new Native.Accounting();
        if(!Native.QueryInformationJobObject(job,
            Native.JobObjectBasicAccountingInformation,ref accounting,
            (uint)Marshal.SizeOf<Native.Accounting>(),out _))
            throw Native.Error("Job Object accounting unavailable");
        return accounting;
    }
    internal bool IsActive()=>ReadAccounting().ActiveProcesses>0;
    internal bool SuccessfulExit()
    {
        if(IsActive())return false;
        if(!Native.GetExitCodeProcess(process,out uint code))
            throw Native.Error("Native preflight exit code unavailable");
        return code==0;
    }
    internal object Status()
    {
        var accounting=ReadAccounting();
        var active=accounting.ActiveProcesses>0;
        uint code=uint.MaxValue;
        if(!active&&process!=IntPtr.Zero&&!Native.GetExitCodeProcess(process,out code))
            throw Native.Error("Cannot verify workload exit status");
        var state=active?(stopped?"stop_requested":"running"):
            stopped?"stopped":code==0?"finished":"failed";
        var cpuTime=accounting.TotalUserTime+accounting.TotalKernelTime;
        return new {
            job_id=spec.JobId,state,active,
            process_tasks=(long)accounting.ActiveProcesses,
            cpu_time_ns=cpuTime>=0&&cpuTime<=90071992547409L?
                (long?)cpuTime*100:null,
            memory_current_mb=(long?)null,
            cpu_rate_units=cpuRate,ram_limit_mb=spec.RamMb,
            owner_verified=true,
            exit_code=active?(uint?)null:code
        };
    }
    internal void Stop()
    {
        if(stopped)return;
        // Windows currently provides only a force-terminating owner. No
        // checkpoint-preserving console interrupt semantics are claimed.
        stopped=true;
        if(!Native.TerminateJobObject(job,130))
            throw Native.Error("Could not terminate owned Windows process tree");
    }
    internal void WriteTerminalReceipt(object status)
    {
        using var parsed=JsonDocument.Parse(JsonSerializer.Serialize(status));
        var result=parsed.RootElement;
        var state=result.GetProperty("state").GetString()!;
        var code=result.GetProperty("exit_code").GetUInt32();
        if(!new[]{"finished","failed","stopped"}.Contains(state))
            throw new InvalidOperationException("Cannot certify a nonterminal Job Object state");
        var payload=spec.JobId+"\n"+state+"\n"+code.ToString();
        var mac=Convert.ToHexString(HMACSHA256.HashData(
            Convert.FromHexString(spec.Token),Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
        var raw=JsonSerializer.Serialize(new {
            job_id=spec.JobId,state,exit_code=code,receipt_hmac=mac
        });
        var temp=spec.ReceiptPath+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(temp,raw+"\n",new UTF8Encoding(false));
            File.Move(temp,spec.ReceiptPath,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public void Dispose()
    {
        if(closed)return;
        closed=true;
        // KILL_ON_JOB_CLOSE closes the workload tree if its owner crashes.
        if(job!=IntPtr.Zero){Native.CloseHandle(job);job=IntPtr.Zero;}
        if(process!=IntPtr.Zero){Native.CloseHandle(process);process=IntPtr.Zero;}
    }
}
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions=
        new(){PropertyNameCaseInsensitive=true};
    private static JobSpec Load(string file)
    {
        if(!Path.IsPathFullyQualified(file)||!File.Exists(file)||
            new FileInfo(file).Length>16384)
            throw new ArgumentException("Trusted local spec is missing or oversized");
        var spec=JsonSerializer.Deserialize<JobSpec>(File.ReadAllText(file),JsonOptions)
            ??throw new ArgumentException("Invalid local native spec");
        if(!Guid.TryParseExact(spec.JobId,"D",out _)||
            !Regex.IsMatch(spec.Token,@"^[a-f0-9]{64}$")||
            !Path.IsPathFullyQualified(spec.Executable)||!File.Exists(spec.Executable)||
            spec.Args is null||spec.Args.Length>64||
            spec.Args.Any(v=>v is null||v.Length>1024||
                v.IndexOfAny(new[]{'\0','\n','\r'})>=0)||
            (spec.Cwd.Length>0&&(!Path.IsPathFullyQualified(spec.Cwd)||
                !Directory.Exists(spec.Cwd)))||
            spec.Cpus<1||spec.HostCpus<1||
            spec.Cpus>spec.HostCpus||
            spec.HostCpus>Environment.ProcessorCount||
            spec.RamMb<128||spec.RamMb>100000000||
            !Path.IsPathFullyQualified(spec.ReceiptPath)||
            !string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(spec.ReceiptPath)),
                Path.GetDirectoryName(Path.GetFullPath(file)),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unsafe or inconsistent locally installed native job");
        return spec;
    }
    private static async Task<string> ReadCommand(StreamReader reader)
    {
        var command=new StringBuilder();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var one=new char[1];
        while(command.Length<2048)
        {
            var n=await reader.ReadAsync(one.AsMemory(),timeout.Token);
            if(n==0)throw new IOException("Closed control connection");
            if(one[0]=='\n')return command.ToString();
            command.Append(one[0]);
        }
        throw new InvalidOperationException("Control command too long");
    }
    private static async Task Serve(JobSpec spec,OwnedJob job)
    {
        var pipeName="experiment-hub-"+spec.JobId.Replace("-","");
        while(true)
        {
            if(!job.IsActive())
            {
                job.WriteTerminalReceipt(job.Status());
                return;
            }
            using var pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            using var poll=new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try{await pipe.WaitForConnectionAsync(poll.Token);}
            catch(OperationCanceledException){continue;}
            using var input=new StreamReader(pipe,new UTF8Encoding(false),false,1024,true);
            using var output=new StreamWriter(pipe,new UTF8Encoding(false),1024,true){
                AutoFlush=true};
            try
            {
                var request=JsonSerializer.Deserialize<PipeRequest>(
                    await ReadCommand(input),JsonOptions);
                if(request==null||request.Action is not ("inspect" or "stop")||
                    !Regex.IsMatch(request.Token,@"^[a-f0-9]{64}$")||
                    !CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(request.Token),
                        Convert.FromHexString(spec.Token)))
                    throw new UnauthorizedAccessException("Unrecognized local control request");
                if(request.Action=="stop")job.Stop();
                await output.WriteLineAsync(JsonSerializer.Serialize(job.Status()));
            }
            catch(Exception e)
            {
                // Do not disclose secret paths or native command details.
                await output.WriteLineAsync(JsonSerializer.Serialize(new {
                    error=e is UnauthorizedAccessException
                        ?"Local authorization rejected"
                        :"Local native inspection failed"
                }));
            }
        }
    }
    private static int Probe()
    {
        // Probe the installed owner binary itself. It does no work, opens
        // no project files and requires neither a console nor another tool.
        var executable=Environment.ProcessPath
            ??throw new InvalidOperationException("Native owner binary path unavailable");
        var spec=new JobSpec{
            JobId=Guid.NewGuid().ToString("D"),Token=new string('a',64),
            Executable=executable,Args=new[]{"--probe-child"},
            Cpus=1,HostCpus=Environment.ProcessorCount,
            RamMb=256,ReceiptPath=Path.Combine(Path.GetTempPath(),
                "unused-"+Guid.NewGuid().ToString("N")+".json")};
        using var owned=new OwnedJob(spec);
        var deadline=DateTime.UtcNow.AddSeconds(10);
        while(owned.IsActive()&&DateTime.UtcNow<deadline)
            Thread.Sleep(100);
        if(owned.IsActive()||!owned.SuccessfulExit())
            throw new InvalidOperationException("Native preflight child did not exit successfully");
        Console.WriteLine(JsonSerializer.Serialize(new {
            passed=true,cpu_hard_cap_verified=true,memory_limit_verified=true,
            job_process_assignment_verified=true
        }));
        return 0;
    }
    internal static async Task<int> Main(string[] args)
    {
        if(!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows Job Objects are available only on Windows.");
            return 2;
        }
        try
        {
            if(args.Length==1&&args[0]=="--probe-child")return 0;
            if(args.Length==1&&args[0]=="--probe")return Probe();
            if(args.Length!=2||args[0]!="--run")
                throw new ArgumentException("Usage: --probe or --run <local-job-spec>");
            var spec=Load(args[1]);
            using var owned=new OwnedJob(spec);
            await Serve(spec,owned);
            return 0;
        }
        catch(Exception error)
        {
            Console.Error.WriteLine("Native job ownership failed: "+error.Message);
            return 1;
        }
    }
}
