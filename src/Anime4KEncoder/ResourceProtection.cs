using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Anime4KEncoder;

internal sealed record ResourceSnapshot(ulong TotalRam, ulong AvailableRam, ulong AvailableCommit,
    ulong TotalCommit, IReadOnlyList<GpuMemory> Gpus)
{
    internal double RamPercent => 100d * (TotalRam - Math.Min(AvailableRam, TotalRam)) / Math.Max(1UL, TotalRam);
}

internal sealed record GpuMemory(uint Index, ulong Total, ulong Free, ulong Used);

internal static class ResourcePolicy
{
    internal const ulong GiB = 1024UL * 1024 * 1024;

    internal static string? Pressure(ResourceSnapshot snapshot, int percent)
    {
        if (percent == 100) return null;
        if (snapshot.AvailableRam < GiB) return "menos de 1 GB de RAM livre";
        if (snapshot.AvailableCommit < GiB) return "menos de 1 GB de memória comprometível livre";
        if (snapshot.Gpus.Any(gpu => gpu.Total > 0 && gpu.Free < GiB / 4))
            return "menos de 256 MB de VRAM livre";
        return null;
    }

    internal static bool Ready(ResourceSnapshot snapshot) => snapshot.AvailableRam >= 4 * GiB &&
        snapshot.AvailableCommit >= 4 * GiB && snapshot.Gpus.All(gpu => gpu.Free >= GiB);

    internal static ulong WorkerMemoryBudget(ResourceSnapshot snapshot, int percent) =>
        Math.Min(snapshot.AvailableRam > 4 * GiB ? snapshot.AvailableRam - 4 * GiB : 0,
            snapshot.AvailableCommit > 4 * GiB ? snapshot.AvailableCommit - 4 * GiB : 0);
}

// The UI stays outside the job. Children of FFmpeg/aji (including trtexec) inherit its limits.
// CPU is capped; RAM admission is cooperative, since a commit cap can itself cause OOM.
internal sealed class ResourceProtection : IDisposable
{
    private readonly SafeFileHandle _job;
    private readonly CancellationTokenSource _monitorCancellation = new();
    private readonly Task _monitor;
    private readonly CancellationTokenSource _queueCancellation;
    private readonly Action<ResourceSnapshot, string> _report;
    private readonly Func<ResourceSnapshot> _sample;
    private readonly NvidiaMemoryReader? _nvidia;
    private readonly object _gate = new();
    private readonly int _percent;
    private readonly int _expectedGpuCount;
    private string? _stopReason;
    private bool _disposed;
    private bool _gpuPriorityUnavailable;
    private readonly List<Process> _processes = [];
    private int _liveMode;

    internal static int CpuPercent(int mode) => mode switch { 70 => 35, 80 => 75, _ => 100 };

    internal void ApplyMode(int mode)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var cpu = new Native.CpuRate { ControlFlags = mode == 100 ? 0u : 5u, CpuRateValue = mode == 100 ? 0u : (uint)CpuPercent(mode) * 100 };
            if (mode != 100 || _liveMode != 100)
                Native.Check(Native.SetCpuRate(_job, 15, ref cpu, (uint)Marshal.SizeOf<Native.CpuRate>()));
            _liveMode = mode;
            foreach (var process in _processes)
                try { if (!process.HasExited) { process.PriorityClass = mode == 100 ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal; _gpuPriorityUnavailable = Native.D3DKMTSetProcessSchedulingPriorityClass(process.Handle, mode == 100 ? 2 : 0) != 0; } }
                catch (InvalidOperationException) { }
        }
    }

    internal string? StopReason { get { lock (_gate) return _stopReason; } }
    internal ulong MemoryBudget { get; }

    internal ResourceProtection(int percent, CancellationTokenSource queueCancellation,
        Action<ResourceSnapshot, string> report, Func<ResourceSnapshot>? sample = null)
    {
        if (percent is not (70 or 80 or 100)) throw new ArgumentOutOfRangeException(nameof(percent));
        _percent = 70; // Emergency monitoring remains active even in maximum mode.
        _liveMode = percent;
        _queueCancellation = queueCancellation;
        _report = report;
        _nvidia = sample is null ? NvidiaMemoryReader.TryCreate() : null;
        _sample = sample ?? (() => Sample(_nvidia));
        _job = Native.CreateJobObject(IntPtr.Zero, null);
        try
        {
            if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível criar a proteção de recursos.");
            var initial = _sample();
            _expectedGpuCount = initial.Gpus.Count;
            MemoryBudget = ResourcePolicy.WorkerMemoryBudget(initial, percent);
            var limits = new Native.ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE; no artificial allocation failures

            Native.Check(Native.SetExtendedLimits(_job, 9, ref limits, (uint)Marshal.SizeOf<Native.ExtendedLimits>()));
            var cpu = new Native.CpuRate { ControlFlags = percent == 100 ? 0u : 5u, CpuRateValue = (uint)CpuPercent(percent) * 100 };
            if (percent != 100)
                Native.Check(Native.SetCpuRate(_job, 15, ref cpu, (uint)Marshal.SizeOf<Native.CpuRate>()));
            _report(initial, MonitoringNote(initial));
            _monitor = Task.Run(MonitorAsync);
        }
        catch
        {
            _job.Dispose();
            _nvidia?.Dispose();
            _monitorCancellation.Dispose();
            throw;
        }
    }

    private static string MonitoringNote(ResourceSnapshot snapshot) => snapshot.Gpus.Count == 0
        ? "VRAM indisponível; proteção de RAM/CPU ativa" : "VRAM monitorada";

    internal void Start(Process process)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _queueCancellation.Token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("Não foi possível iniciar o processo de trabalho.");
            try
            {
                Native.Check(Native.AssignProcessToJobObject(_job, process.Handle));
                _processes.Add(process);
                process.PriorityClass = _liveMode == 100 ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal;
                // Supported WDDM scheduling priority; does not impose a GPU utilization cap.
                if (Native.D3DKMTSetProcessSchedulingPriorityClass(process.Handle, _liveMode == 100 ? 2 : 0) != 0)
                    _gpuPriorityUnavailable = true;
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
        }
    }

    private async Task MonitorAsync()
    {
        var pressuredSamples = 0;
        var sensorFailures = 0;
        try
        {
            while (true)
            {
                await Task.Delay(250, _monitorCancellation.Token).ConfigureAwait(false);
                ResourceSnapshot snapshot;
                try { snapshot = _sample(); }
                catch
                {
                    if (++sensorFailures < 20) continue;
                    Stop("Monitor de memória indisponível por 5 segundos; tentativa encerrada para recuperação.");
                    return;
                }
                if (_expectedGpuCount > 0 && snapshot.Gpus.Count != _expectedGpuCount)
                {
                    if (++sensorFailures < 20) continue;
                    Stop("Leitura de VRAM indisponível por 5 segundos; tentativa encerrada para recuperação.");
                    return;
                }
                sensorFailures = 0;
                var pressure = ResourcePolicy.Pressure(snapshot, _percent);
                pressuredSamples = pressure is null ? 0 : pressuredSamples + 1;
                if (pressure is not null && pressuredSamples >= 12)
                {
                    Stop($"Reserva esgotada: {pressure}. RAM livre={snapshot.AvailableRam}; compromisso livre={snapshot.AvailableCommit}; VRAM livre={string.Join(',', snapshot.Gpus.Select(gpu => gpu.Free))}. Blocos preservados.");
                    _report(snapshot, StopReason!);
                    return;
                }
                _report(snapshot, MonitoringNote(snapshot) + (_gpuPriorityUnavailable ? "; prioridade GPU indisponível" : ""));
            }
        }
        catch (OperationCanceledException) when (_monitorCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Stop("Proteção interrompeu o processamento porque não conseguiu monitorar a memória: " + ex.Message);
        }
    }

    private void Stop(string reason)
    {
        lock (_gate)
        {
            if (_disposed || _stopReason is not null) return;
            _stopReason = reason;
            // Terminate first: stopping an async pipe reader must not leave GPU allocations alive.
            Native.TerminateJobObject(_job, 0xE0000001);
        }
        _queueCancellation.Cancel();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _monitorCancellation.Cancel();
        _monitor.GetAwaiter().GetResult();
        _job.Dispose(); // also kills descendants left behind after an error or app shutdown
        _nvidia?.Dispose();
        _monitorCancellation.Dispose();
    }

    internal static ResourceSnapshot Sample(NvidiaMemoryReader? nvidia = null)
    {
        var memory = new Native.MemoryStatus { Length = (uint)Marshal.SizeOf<Native.MemoryStatus>() };
        Native.Check(Native.GlobalMemoryStatusEx(ref memory));
        return new(memory.TotalPhys, memory.AvailPhys, memory.AvailPageFile, memory.TotalPageFile, nvidia?.Read() ?? []);
    }

    internal static class Native
    {
        internal static void Check(bool success)
        {
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "O Windows não conseguiu aplicar a proteção de recursos.");
        }
        [StructLayout(LayoutKind.Sequential)] internal struct MemoryStatus
        {
            internal uint Length, MemoryLoad;
            internal ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits
        {
            internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters
        {
            internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits
        {
            internal BasicLimits Basic;
            internal IoCounters Io;
            internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct CpuRate { internal uint ControlFlags, CpuRateValue; }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
        internal static extern bool SetExtendedLimits(SafeFileHandle job, int infoClass, ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
        internal static extern bool SetCpuRate(SafeFileHandle job, int infoClass, ref CpuRate limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        [DllImport("gdi32.dll")] internal static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);
    }
}

// Optional NVIDIA driver API. Load from System32 only, not a DLL from a video's folder.
internal sealed class NvidiaMemoryReader : IDisposable
{
    private readonly IntPtr _library;
    private readonly Shutdown _shutdown;
    private readonly GetMemory _getMemory;
    private readonly List<(uint Index, IntPtr Device)> _devices = [];
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetCount(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDevice(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetMemory(IntPtr device, out Memory memory);
    [StructLayout(LayoutKind.Sequential)] private struct Memory { internal ulong Total, Free, Used; }

    private NvidiaMemoryReader(IntPtr library)
    {
        _library = library;
        T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        _shutdown = Export<Shutdown>("nvmlShutdown");
        _getMemory = Export<GetMemory>("nvmlDeviceGetMemoryInfo");
        var getCount = Export<GetCount>("nvmlDeviceGetCount_v2");
        var getDevice = Export<GetDevice>("nvmlDeviceGetHandleByIndex_v2");
        if (Export<Init>("nvmlInit_v2")() != 0) throw new InvalidOperationException("NVML indisponível.");
        if (getCount(out var count) == 0)
            for (uint index = 0; index < count; index++)
                if (getDevice(index, out var device) == 0) _devices.Add((index, device));
    }

    internal static NvidiaMemoryReader? TryCreate()
    {
        if (!NativeLibrary.TryLoad(System.IO.Path.Combine(Environment.SystemDirectory, "nvml.dll"), out var library)) return null;
        try { return new(library); }
        catch { NativeLibrary.Free(library); return null; }
    }

    internal IReadOnlyList<GpuMemory> Read()
    {
        var result = new List<GpuMemory>();
        foreach (var (index, device) in _devices)
            if (_getMemory(device, out var memory) == 0 && memory.Total > 0)
                result.Add(new(index, memory.Total, memory.Free, memory.Used));
        return result;
    }

    public void Dispose() { _shutdown(); NativeLibrary.Free(_library); }
}
