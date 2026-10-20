using Anime4KEncoder;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
var ownDll = Assembly.GetExecutingAssembly().Location;
ProcessStartInfo Helper(string mode) => new("dotnet") {ArgumentList={ownDll,mode}, RedirectStandardOutput=true, RedirectStandardError=true, RedirectStandardInput=true, UseShellExecute=false, CreateNoWindow=true};
if (args.FirstOrDefault()=="watch") { while(Console.ReadLine() is not null) {Console.WriteLine(Native.CurrentCpuRate()); Console.Out.Flush();} return;}
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
 var expected=$"5:{(mode==70?35:75)*100}";
 Check(await p.StandardOutput.ReadLineAsync()==expected,"CPU cap wrong."); Check(await p.StandardOutput.ReadLineAsync()==expected,"Child CPU cap wrong.");
 guard.Dispose(); await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
}
Console.WriteLine("WINDOWS_CPU_TREE=PASS: 35/75%, descendants, cleanup.");
using(var c=new CancellationTokenSource()) {
 using var guard=new ResourceProtection(70,c,(_,_)=>{},Safe); using var p=new Process{StartInfo=Helper("allocate")}; guard.Start(p); p.StandardInput.WriteLine("go"); p.StandardInput.Flush();
 Check(await p.StandardOutput.ReadLineAsync()=="ALLOWED","Old artificial job-memory allocation failure remains."); await p.WaitForExitAsync();
}
Console.WriteLine("NO_ARTIFICIAL_MEMORY_FAILURE=PASS: 768 MiB committed and released without touching pages.");
using(var c=new CancellationTokenSource()) {
 var critical=0; ResourceSnapshot Sample()=>Volatile.Read(ref critical)==0?Safe():Safe() with{AvailableRam=gb/2};
 using var guard=new ResourceProtection(100,c,(_,_)=>{},Sample);
 Volatile.Write(ref critical,1); await Task.Delay(500); Check(!c.IsCancellationRequested,"Transient pressure terminated work.");
 Volatile.Write(ref critical,0); await Task.Delay(500); Check(!c.IsCancellationRequested,"Pressure recovery failed.");
 Volatile.Write(ref critical,1); await Task.Delay(4000); Check(c.IsCancellationRequested&&guard.StopReason is not null,"Sustained pressure didn't stop attempt.");
}
Console.WriteLine("PRESSURE_HYSTERESIS=PASS: transient tolerated; sustained critical pressure stops attempt.");
using(var nvml=NvidiaMemoryReader.TryCreate()){var live=ResourceProtection.Sample(nvml); Console.WriteLine($"LIVE_TELEMETRY: RAM free={live.AvailableRam}, commit free={live.AvailableCommit}, GPU count={live.Gpus.Count}");}
using(var c=new CancellationTokenSource()) {
 using var guard=new ResourceProtection(70,c,(_,_)=>{},Safe); using var p=new Process{StartInfo=Helper("watch")}; guard.Start(p);
 foreach(var mode in new[]{70,80,100,70}) {
  guard.ApplyMode(mode); p.StandardInput.WriteLine("query"); p.StandardInput.Flush();
  var actual=await p.StandardOutput.ReadLineAsync();
  Check(mode==100?actual!.StartsWith("0:"):actual==$"5:{ResourceProtection.CpuPercent(mode)*100}","Live CPU update failed: "+actual);
 }
 Check(!c.IsCancellationRequested,"Changing mode cancelled the attempt.");
}
Console.WriteLine("LIVE_CPU=PASS: 35 -> 75 -> uncapped -> 35 on same running process.");
if(args.Length>0 && File.Exists(args[0])) {
 var folder=Path.Combine(Path.GetTempPath(),"live-pacing-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
 var reference=Path.Combine(folder,"reference.md5"); var actual=Path.Combine(folder,"actual.md5");
 ProcessStartInfo Video(string path,string? filter=null) {
  var info=new ProcessStartInfo(args[0]){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
  foreach(var a in new[]{"-hide_banner","-nostdin","-v","error","-f","lavfi","-i","testsrc2=s=64x64:r=10:d=20"})info.ArgumentList.Add(a);
  if(filter is not null){info.ArgumentList.Add("-vf"); info.ArgumentList.Add(filter);}
  foreach(var a in new[]{"-f","framemd5",path})info.ArgumentList.Add(a); return info;
 }
 using(var p=Process.Start(Video(reference))!) {var error=await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); Check(p.ExitCode==0,error);}
 using var pacing=new LivePacing(0.15);
 using(var p=Process.Start(Video(actual,pacing.Filter))!) {
  async Task Applied(double rate){pacing.SetRate(rate); var clock=Stopwatch.StartNew(); while(pacing.AppliedRate!=rate&&!p.HasExited&&clock.Elapsed.TotalSeconds<5)await Task.Delay(50); Check(pacing.AppliedRate==rate,"Pacing command failed: "+pacing.Error+"; filter="+pacing.Filter); Console.WriteLine($"LIVE_RATE={rate}; ACK_MS={clock.ElapsedMilliseconds}");}
  await Applied(0.15); await Applied(0.25); await Applied(0.15); await Applied(1000000);
  await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); var error=await p.StandardError.ReadToEndAsync(); Check(p.ExitCode==0,error);
 }
 Check(File.ReadAllLines(reference).Where(x=>!x.StartsWith('#')).SequenceEqual(File.ReadAllLines(actual).Where(x=>!x.StartsWith('#'))),"Live pacing changed frames or timestamps.");
 Console.WriteLine("LIVE_PACING=PASS: same process, runtime speed changes, 200 frame hashes and timestamps unchanged.");
}
if(args.Length>0 && File.Exists(args[0])) {
 using var pacing=new LivePacing(1000000);
 var info=new ProcessStartInfo(args[0]){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
 foreach(var a in new[]{"-hide_banner","-nostdin","-v","error","-f","lavfi","-readrate","1","-i","testsrc2=s=64x64:r=10:d=5","-vf",pacing.Filter,"-f","null","-"})info.ArgumentList.Add(a);
 using var p=Process.Start(info)!;
 foreach(var rate in new[]{1000000d,0.15,1000000}) {
  pacing.SetRate(rate); var clock=Stopwatch.StartNew();
  while(pacing.AppliedRate!=rate&&!p.HasExited&&clock.Elapsed.TotalSeconds<3)await Task.Delay(25);
  Check(pacing.AppliedRate==rate,"Maximum -> limited switch failed: "+pacing.Error);
 }
 await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Check(p.ExitCode==0,await p.StandardError.ReadToEndAsync());
 Console.WriteLine("LIVE_MAX_TO_LIMITED=PASS: maximum -> limited -> maximum without restarting FFmpeg.");
}
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
