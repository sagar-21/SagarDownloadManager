using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DM.App.Licensing;

internal static class AntiTamper
{
    // ── Win32 / NT imports ─────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(
        IntPtr hProcess,
        [MarshalAs(UnmanagedType.Bool)] out bool debuggerPresent);

    // int-sized output (ProcessDebugPort = 7, ProcessDebugFlags = 31)
    [DllImport("ntdll.dll", SetLastError = false)]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref int processInformation, int processInformationLength,
        out int returnLength);

    // PROCESS_BASIC_INFORMATION-sized output (class 0 — for parent PID)
    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess", SetLastError = false)]
    private static extern int NtQueryInformationProcessBasic(
        IntPtr processHandle, int processInformationClass,
        ref ProcessBasicInfo processInformation, int processInformationLength,
        out int returnLength);

    // Kernel-mode debugger detection (SystemKernelDebuggerInformation = 35)
    [DllImport("ntdll.dll", SetLastError = false)]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        ref KernelDebuggerInfo systemInformation,
        int systemInformationLength,
        out int returnLength);

    // Thread hiding — makes threads invisible to user-mode debugger events
    [DllImport("ntdll.dll", SetLastError = false)]
    private static extern int NtSetInformationThread(
        IntPtr threadHandle, int threadInformationClass,
        IntPtr threadInformation, int threadInformationLength);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentThread();

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInfo
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KernelDebuggerInfo
    {
        public byte KernelDebuggerEnabled;
        public byte KernelDebuggerNotPresent;
    }

    // Known reverse-engineering tool process names
    private static readonly HashSet<string> _reTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "dnSpy", "dnspy-x86", "dnspy64", "ILSpy", "dotPeek", "JustDecompile",
        "x64dbg", "x32dbg", "OllyDbg", "OllyDbg110", "WinDbg", "windbg",
        "ida", "ida64", "idaq", "idaq64",
        "Ghidra", "ghidra",
        "de4dot", "de4dot-x64",
        "ProcessHacker", "SystemInformer",
        "HxD", "010Editor", "CFF Explorer",
    };

    // ── Main entry point ───────────────────────────────────────────────────────

    internal static TamperResult Check(StoredLicense? stored)
    {
        bool debugger         = IsDebuggerAttached();
        bool timingAnomaly    = DetectTimingAnomaly();
        bool suspiciousEnv    = DetectSuspiciousEnvironment();
        bool vmDetected       = IsRunningInVm();
        bool assemblyModified = stored is not null && !AssemblyMatchesBaseline(stored);

        return new TamperResult(
            DebuggerDetected:  debugger,
            TimingAnomaly:     timingAnomaly,
            SuspiciousEnv:     suspiciousEnv,
            VmDetected:        vmDetected,
            AssemblyModified:  assemblyModified
        );
    }

    // ── Thread hardening ───────────────────────────────────────────────────────
    // Call once from App.OnStartup. Makes this thread invisible to user-mode
    // debugger events (single-step, breakpoints, debug events stop working).

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void HardenThread()
    {
#if !DEBUG
        try { NtSetInformationThread(GetCurrentThread(), 17 /* ThreadHideFromDebugger */, IntPtr.Zero, 0); }
        catch { }
#endif
    }

    // ── Layer 1: Assembly hash ─────────────────────────────────────────────────

    internal static string? HashMainAssembly()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path is null || !File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    internal static bool AssemblyMatchesBaseline(StoredLicense stored)
    {
        if (stored.AssemblyHash is null) return true;
        var current = HashMainAssembly();
        if (current is null) return true;
        return string.Equals(current, stored.AssemblyHash, StringComparison.OrdinalIgnoreCase);
    }

    // ── Layer 2: Debugger presence (6 independent vectors) ────────────────────

    internal static bool IsDebuggerAttached()
    {
#if DEBUG
        return false;
#else
        // Vector 1: managed API
        if (Debugger.IsAttached) return true;

        // Vector 2: kernel32 — detects remote debuggers (Visual Studio attach, etc.)
        try
        {
            CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, out var remote);
            if (remote) return true;
        }
        catch { }

        // Vector 3: ProcessDebugPort (class 7) — non-zero when debugged
        try
        {
            int port = 0;
            if (NtQueryInformationProcess(Process.GetCurrentProcess().Handle,
                    7, ref port, sizeof(int), out _) == 0 && port != 0) return true;
        }
        catch { }

        // Vector 4: ProcessDebugFlags (class 31) — 0 when debugged (inverse of DebugPort)
        // Catches debuggers that clear the DebugPort field (ScyllaHide etc.)
        try
        {
            int flags = 1; // default non-zero so a query failure doesn't trigger
            if (NtQueryInformationProcess(Process.GetCurrentProcess().Handle,
                    31, ref flags, sizeof(int), out _) == 0 && flags == 0) return true;
        }
        catch { }

        // Vector 5: kernel debugger (WinDbg at kernel level, boot debugging)
        try
        {
            var kdbg = default(KernelDebuggerInfo);
            if (NtQuerySystemInformation(35, ref kdbg, 2, out _) == 0
                && kdbg.KernelDebuggerEnabled != 0
                && kdbg.KernelDebuggerNotPresent == 0) return true;
        }
        catch { }

        // Vector 6: parent process — dnSpy/x64dbg launching the app to debug it
        if (HasSuspiciousParentProcess()) return true;

        return false;
#endif
    }

    private static bool HasSuspiciousParentProcess()
    {
        try
        {
            var pbi = default(ProcessBasicInfo);
            int ret = NtQueryInformationProcessBasic(
                Process.GetCurrentProcess().Handle,
                0, ref pbi, Marshal.SizeOf<ProcessBasicInfo>(), out _);
            if (ret != 0) return false;

            int parentPid = (int)pbi.InheritedFromUniqueProcessId;
            if (parentPid <= 0) return false;

            using var parent = Process.GetProcessById(parentPid);
            return _reTools.Contains(parent.ProcessName);
        }
        catch { return false; }
    }

    // ── Layer 3: Timing anomaly ────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static bool DetectTimingAnomaly()
    {
#if DEBUG
        return false;
#else
        const int  iterations    = 50_000;
        const long maxAllowedMs  = 500;

        var sw    = Stopwatch.StartNew();
        int dummy = 0;
        for (int i = 0; i < iterations; i++)
            dummy ^= (int)(i * 0x9E3779B9);
        sw.Stop();

        GC.KeepAlive(dummy);
        return sw.ElapsedMilliseconds > maxAllowedMs;
#endif
    }

    // ── Layer 4: Suspicious environment ───────────────────────────────────────

    internal static bool DetectSuspiciousEnvironment()
    {
#if DEBUG
        return false;
#else
        string[] suspiciousEnvKeys =
        [
            "_MEIPASS2",        // PyInstaller RE wrapper
            "CORECLR_PROFILER", // CLR profiler API (some RE tools)
        ];

        foreach (var key in suspiciousEnvKeys)
            if (Environment.GetEnvironmentVariable(key) is not null) return true;

        try
        {
            if (Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wine") is not null)
                return true;
        }
        catch { }

        return false;
#endif
    }

    // ── Layer 5: Virtual machine detection ────────────────────────────────────

    internal static bool IsRunningInVm()
    {
#if DEBUG
        return false;
#else
        string[] vmRegKeys =
        [
            @"SOFTWARE\VMware, Inc.\VMware Tools",
            @"SOFTWARE\Oracle\VirtualBox Guest Additions",
            @"SYSTEM\CurrentControlSet\Services\VBoxGuest",
            @"SYSTEM\CurrentControlSet\Services\VMTools",
            @"SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters",  // Hyper-V
        ];

        foreach (var keyPath in vmRegKeys)
        {
            try
            {
                if (Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath) is not null)
                    return true;
            }
            catch { }
        }

        // BIOS string check for QEMU / Bochs
        try
        {
            var sysKey = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System");
            if (sysKey?.GetValue("SystemBiosVersion") is string[] bios)
            {
                var combined = string.Join(" ", bios).ToUpperInvariant();
                if (combined.Contains("VBOX") || combined.Contains("VMWARE")
                    || combined.Contains("QEMU") || combined.Contains("BOCHS"))
                    return true;
            }
        }
        catch { }

        return false;
#endif
    }
}

// ── Result record ──────────────────────────────────────────────────────────────

internal sealed record TamperResult(
    bool DebuggerDetected,
    bool TimingAnomaly,
    bool SuspiciousEnv,
    bool VmDetected,
    bool AssemblyModified)
{
    internal bool RequiresImmediateHeartbeat => AssemblyModified;

    internal string? SoftReportType =>
        DebuggerDetected ? "debugger" :
        TimingAnomaly    ? "timing_anomaly" :
        SuspiciousEnv    ? "suspicious_env" :
        VmDetected       ? "vm_detected" :
        null;

    internal bool AnySoftSignal =>
        DebuggerDetected || TimingAnomaly || SuspiciousEnv || VmDetected;
}
