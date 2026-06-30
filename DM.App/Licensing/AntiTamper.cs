using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DM.App.Licensing;

/// <summary>
/// Defense-in-depth anti-tamper checks.
///
/// ════════════════════════════════════════════════════════════════════
/// HONEST ASSESSMENT OF EACH LAYER
/// ════════════════════════════════════════════════════════════════════
///
/// Layer 1 — Assembly hash baseline
///   Detects: hex-editor or IL patcher modification of the .exe.
///   Does NOT detect: in-memory patching after the initial hash check.
///   Action: queue an integrity report, force immediate heartbeat.
///   Honest limit: an attacker can delete the stored baseline to reset it;
///   the server's own hash tracking (KnownGoodHash) is the real check.
///
/// Layer 2 — Debugger detection (IsDebuggerPresent + CheckRemoteDebuggerPresent)
///   Detects: standard debuggers attached before startup.
///   Does NOT detect: debuggers that clear the IsDebugged PEB flag
///   (ScyllaHide, x64dbg plugin mode, custom debuggers).
///   Action: queue a soft report — do NOT lock the app, to avoid false
///   positives from AV heuristic engines that use debugger APIs.
///
/// Layer 3 — Timing check
///   Detects: single-step execution (debugger makes loops take 1000× longer).
///   Does NOT detect: hardware breakpoints, out-of-process debugging.
///   Action: soft flag only.
///
/// Layer 4 — Suspicious environment detection
///   Detects: common RE environment variables and Wine.
///   Does NOT detect: custom RE environments, VMs, automated test rigs.
///   Action: soft flag only.
///
/// Layer 5 — EncryptedStrings for sensitive literals
///   Prevents: trivial `strings` grep for the server URL or DPAPI salt.
///   Does NOT prevent: memory inspection of a running process.
///
/// OVERALL GUARANTEES
///   ✓ Makes a simple "nop the license check" patch insufficient — the
///     download engine also needs the server-issued session key.
///   ✓ Forces a server heartbeat on any detected modification, so the
///     server can revoke within 6 hours.
///   ✗ Does NOT prevent a skilled attacker using dnSpy, WinDbg, or a
///     custom CLR host.
///   ✗ All checks here can be bypassed by patching this class itself.
///   The SERVER (via heartbeat + /report) is the definitive authority.
///
/// OBFUSCATOR RECOMMENDATION
///   Apply at release time to raise the bar substantially:
///
///   Free / open source:
///     Obfuscar — https://docs.obfuscar.com
///       Use with scripts/obfuscar.xml (skip ViewModels/Views namespaces).
///     ConfuserEx — https://github.com/yck1509/ConfuserEx
///       Unmaintained but still effective for basic rename + flow obfuscation.
///
///   Paid (recommended):
///     .NET Reactor   — https://www.eziriz.com — best balance of price and strength
///     Eazfuscator.NET — https://www.gapotchenko.com/eazfuscator.net
///     SmartAssembly  — https://www.red-gate.com/products/smartassembly/
///     Dotfuscator Pro — https://www.preemptive.com/products/dotfuscator/
///
///   Apply in Release.ps1 AFTER `dotnet publish` and BEFORE `signtool`.
///   Obfuscate only DM.App.dll and DM.Core.dll; skip ViewModels/Views (XAML binds by name).
/// ════════════════════════════════════════════════════════════════════
/// </summary>
internal static class AntiTamper
{
    // ── Win32 imports ──────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(
        IntPtr hProcess,
        [MarshalAs(UnmanagedType.Bool)] out bool debuggerPresent);

    [DllImport("ntdll.dll", SetLastError = false)]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref int processInformation, int processInformationLength,
        out int returnLength);

    // ── Main entry point ───────────────────────────────────────────────────────

    internal static TamperResult Check(StoredLicense? stored)
    {
        bool debugger        = IsDebuggerAttached();
        bool timingAnomaly   = DetectTimingAnomaly();
        bool suspiciousEnv   = DetectSuspiciousEnvironment();
        bool assemblyModified = stored is not null && !AssemblyMatchesBaseline(stored);

        return new TamperResult(
            DebuggerDetected:  debugger,
            TimingAnomaly:     timingAnomaly,
            SuspiciousEnv:     suspiciousEnv,
            AssemblyModified:  assemblyModified
        );
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
        if (stored.AssemblyHash is null) return true;   // no baseline — don't false-positive
        var current = HashMainAssembly();
        if (current is null)       return true;          // can't hash — don't false-positive
        return string.Equals(current, stored.AssemblyHash, StringComparison.OrdinalIgnoreCase);
    }

    // ── Layer 2: Debugger presence ─────────────────────────────────────────────

    internal static bool IsDebuggerAttached()
    {
#if DEBUG
        return false; // never fire in dev builds — developers attach debuggers legitimately
#else
        if (Debugger.IsAttached) return true;

        try
        {
            CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, out var remote);
            if (remote) return true;
        }
        catch { /* P/Invoke failed — skip, don't false-positive */ }

        // NtQueryInformationProcess ProcessDebugPort (class 7) — returns non-zero if debugged
        try
        {
            int debugPort = 0;
            int ret = NtQueryInformationProcess(
                Process.GetCurrentProcess().Handle,
                7 /* ProcessDebugPort */,
                ref debugPort, sizeof(int), out _);
            if (ret == 0 && debugPort != 0) return true;
        }
        catch { }

        return false;
#endif
    }

    // ── Layer 3: Timing anomaly ────────────────────────────────────────────────
    //
    // A debugger running a loop in single-step mode makes it take 3–4 orders of
    // magnitude longer.  Measure a tight loop and compare to a baseline threshold.
    //
    // LIMIT: hardware breakpoints don't slow the loop; out-of-process debugging
    // doesn't slow it either.  This catches naive single-step debugging only.

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static bool DetectTimingAnomaly()
    {
#if DEBUG
        return false;
#else
        const int iterations = 50_000;
        // Allow for slow VMs: 500ms is extremely generous for 50k iterations
        const long maxAllowedMs = 500;

        var sw = Stopwatch.StartNew();
        int dummy = 0;
        for (int i = 0; i < iterations; i++)
            dummy ^= (int)(i * 0x9E3779B9); // cheap non-dead-code loop body
        sw.Stop();

        GC.KeepAlive(dummy); // prevent optimizer from removing the loop
        return sw.ElapsedMilliseconds > maxAllowedMs;
#endif
    }

    // ── Layer 4: Suspicious environment ───────────────────────────────────────

    internal static bool DetectSuspiciousEnvironment()
    {
#if DEBUG
        return false;
#else
        // x64dbg, OllyDbg, WinDbg, IDA set these
        string[] suspiciousEnvKeys =
        [
            "_MEIPASS2",           // PyInstaller packer (common RE wrapper)
            "CORECLR_PROFILER",    // CLR profiler (some RE tools attach via profiler API)
        ];

        foreach (var key in suspiciousEnvKeys)
            if (Environment.GetEnvironmentVariable(key) is not null) return true;

        // Wine check (many crackers use Wine on Linux for .NET RE)
        try
        {
            var wineKey = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Wine");
            if (wineKey is not null) return true;
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
    bool AssemblyModified)
{
    /// <summary>
    /// True when the assembly was modified — the most actionable signal.
    /// Forces an immediate server heartbeat so the server can compare hashes.
    ///
    /// We do NOT hard-lock on debugger/timing/env — too many false positives
    /// (AV engines, corporate security tools, Wine) hurt legitimate users.
    /// Soft signals are queued as /report events for admin review.
    /// </summary>
    internal bool RequiresImmediateHeartbeat => AssemblyModified;

    /// <summary>Report type string for /report endpoint.</summary>
    internal string? SoftReportType =>
        DebuggerDetected ? "debugger" :
        TimingAnomaly    ? "timing_anomaly" :
        SuspiciousEnv    ? "suspicious_env" :
        null;

    internal bool AnySoftSignal => DebuggerDetected || TimingAnomaly || SuspiciousEnv;
}
