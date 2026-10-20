using Anime4KEncoder;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
var ownDll = Assembly.GetExecutingAssembly().Location;
ProcessStartInfo Helper(string mode) => new("dotnet") {ArgumentList={ownDll,mode}, RedirectStandardOutput=true, RedirectStandardError=true, RedirectStandardInput=true, UseShellExecute=false, CreateNoWindow=true};
if (args.FirstOrDefault()=="child") {Console.WriteLine(Native.CurrentCpuRate()); Console.Out.Flush(); Thread.Sleep(30000); return;}
if (args.FirstOrDefault()=="parent") {Console.ReadLine(); using var child=Process.Start(Helper("child"))!; Console.WriteLine(child.Id); Console.WriteLine(Native.CurrentCpuRate()); Console.WriteLine(child.StandardOutput.ReadLine()); Console.Out.Flush(); Thread.Sleep(30000); return;}
if (args.FirstOrDefault()=="allocate") {Console.ReadLine(); var p=Native.VirtualAlloc(IntPtr.Zero,(UIntPtr)(768UL*1024*1024),0x3000,4); Console.WriteLine(p==IntPtr.Zero?"BLOCKED":"ALLOWED"); if(p!=IntPtr.Zero)Native.VirtualFree(p,UIntPtr.Zero,0x8000); return;}
void Check(bool value,string message) {if(!value)throw new Exception(message);}
var gb=ResourcePolicy.GiB;
ResourceSnapshot Safe()=>new(32*gb,8*gb,10*gb,96*gb,[new(0,12*gb,2*gb,10*gb)]);
Check(ResourcePolicy.Pressure(Safe(),70) is null,"Old percentage trigger still active.");
Check(ResourcePolicy.Ready(Safe()),"Healthy reserve rejected.");
Check(!ResourcePolicy.Ready(Safe() with {AvailableRam=3*gb}),"Admission ignored RAM reserve.");
Check(!ResourcePolicy.Ready(Safe() with {AvailableCommit=3*gb}),"Admission ignored commit reserve.");
Check(!ResourcePolicy.Ready(Safe() with {Gpus=[new(0,12*gb,gb/2,12*gb-gb/2)]}),"Admission ignored VRAM reserve.");
Check(ResourcePolicy.Pressure(Safe() with {AvailableRam=gb/2},70) is not null,"Critical RAM ignored.");
var recovery=new BackgroundRecovery();
Check(!recovery.TryRetry(false,false),"Ordinary errors retry.");
Check(!recovery.TryRetry(true,true),"User cancellation retries.");
var initial=recovery.ReadRate(70);
Check(recovery.TryRetry(true,false)&&recovery.ReadRate(70)<initial,"First retry not slower.");
Check(recovery.TryRetry(true,false)&&!recovery.TryRetry(true,false),"Retry circuit breaker failed.");
Check(BackgroundRecovery.AllocationFailure("CUDA_ERROR_OUT_OF_MEMORY")&&!BackgroundRecovery.AllocationFailure("Invalid input data"),"Allocation classification failed.");
Console.WriteLine("RESERVE_AND_RETRY_POLICY=PASS: no global percentage stop, admission, critical reserve, 2 retries, slower pace, user cancel, ordinary error.");
foreach(var mode in new[]{70,80}) {
 using var c=new CancellationTokenSource(); using var guard=new ResourceProtection(mode,c,(_,_)=>{},Safe);
 using var p=new Process{StartInfo=Helper("parent")}; guard.Start(p); p.StandardInput.WriteLine("go"); p.StandardInput.Flush();
 using var child=Process.GetProcessById(int.Parse((await p.StandardOutput.ReadLineAsync())!));
 var expected=$"5:{(mode==70?35:50)*100}";
 Check(await p.StandardOutput.ReadLineAsync()==expected,"CPU cap wrong."); Check(await p.StandardOutput.ReadLineAsync()==expected,"Child CPU cap wrong.");
 guard.Dispose(); await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
}
Console.WriteLine("WINDOWS_CPU_TREE=PASS: 35/50%, descendants, cleanup.");
using(var c=new CancellationTokenSource()) {
 using var guard=new ResourceProtection(70,c,(_,_)=>{},Safe); using var p=new Process{StartInfo=Helper("allocate")}; guard.Start(p); p.StandardInput.WriteLine("go"); p.StandardInput.Flush();
 Check(await p.StandardOutput.ReadLineAsync()=="ALLOWED","Old artificial job-memory allocation failure remains."); await p.WaitForExitAsync();
}
Console.WriteLine("NO_ARTIFICIAL_MEMORY_FAILURE=PASS: 768 MiB committed and released without touching pages.");
using(var c=new CancellationTokenSource()) {
 var critical=0; ResourceSnapshot Sample()=>Volatile.Read(ref critical)==0?Safe():Safe() with{AvailableRam=gb/2};
 using var guard=new ResourceProtection(70,c,(_,_)=>{},Sample);
 Volatile.Write(ref critical,1); await Task.Delay(500); Check(!c.IsCancellationRequested,"Transient pressure terminated work.");
 Volatile.Write(ref critical,0); await Task.Delay(500); Check(!c.IsCancellationRequested,"Pressure recovery failed.");
 Volatile.Write(ref critical,1); await Task.Delay(4000); Check(c.IsCancellationRequested&&guard.StopReason is not null,"Sustained pressure didn't stop attempt.");
}
Console.WriteLine("PRESSURE_HYSTERESIS=PASS: transient tolerated; sustained critical pressure stops attempt.");
using(var nvml=NvidiaMemoryReader.TryCreate()){var live=ResourceProtection.Sample(nvml); Console.WriteLine($"LIVE_TELEMETRY: RAM free={live.AvailableRam}, commit free={live.AvailableCommit}, GPU count={live.Gpus.Count}");}
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct CpuRate { internal uint ControlFlags, CpuRateValue; }
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out CpuRate rate, uint size, IntPtr returned);
    internal static string CurrentCpuRate()
    {
        if (!QueryInformationJobObject(IntPtr.Zero, 15, out var rate, (uint)Marshal.SizeOf<CpuRate>(), IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return $"{rate.ControlFlags}:{rate.CpuRateValue}";
    }
}
