using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TaskbarMonitor.Counters
{
    /// <summary>
    /// Samples per-process resource usage on demand (used by the hover popup).
    /// Not thread-safe: call Sample/Reset from a single thread at a time.
    /// </summary>
    internal sealed class ProcessSampler : IDisposable
    {
        public enum Metric { CPU, MEM, DISK, GPU, GPUMEM }

        public sealed class Entry
        {
            public string Name { get; set; }
            public int Count { get; set; }
            public double Value { get; set; }
        }

        private struct Snap
        {
            public long Creation;
            public long CpuTime;      // 100ns units
            public ulong IoBytes;
            public ulong WorkingSet;
        }

        private const string GpuEnginePath = @"\GPU Engine(*)\Utilization Percentage";
        private const string GpuMemPath = @"\GPU Process Memory(*)\Dedicated Usage";
        private static readonly Regex PidRegex = new Regex(@"^pid_(\d+)_", RegexOptions.Compiled);

        private readonly object sync = new object();
        private Dictionary<int, Snap> prev;
        private long prevTimestamp;
        private PerformanceCounterReader gpuReader;
        private bool gpuPrimed;
        private bool disposed;

        public static Metric? MetricFor(string counterName)
        {
            switch (counterName)
            {
                case "CPU": return Metric.CPU;
                case "MEM": return Metric.MEM;
                case "DISK": return Metric.DISK;
                case "GPU 3D": return Metric.GPU;
                case "GPU MEM": return Metric.GPUMEM;
                default: return null; // NET: per-process network usage is not available without ETW
            }
        }

        public static string Format(Metric metric, double value)
        {
            switch (metric)
            {
                case Metric.CPU:
                case Metric.GPU:
                    return value.ToString("0.0") + "%";
                case Metric.DISK:
                    return FormatBytes(value) + "/s";
                default:
                    return FormatBytes(value);
            }
        }

        private static string FormatBytes(double bytes)
        {
            if (bytes >= 1024d * 1024 * 1024) return (bytes / (1024d * 1024 * 1024)).ToString("0.0") + " GB";
            if (bytes >= 1024d * 1024) return (bytes / (1024d * 1024)).ToString("0.0") + " MB";
            if (bytes >= 1024d) return (bytes / 1024d).ToString("0") + " KB";
            return bytes.ToString("0") + " B";
        }

        /// <summary>Forget the previous snapshot so rate metrics restart measuring.</summary>
        public void Reset()
        {
            lock (sync)
            {
                prev = null;
                gpuPrimed = false;
            }
        }

        /// <summary>
        /// Returns the top entries (aggregated by process name) for the metric,
        /// or null when a rate metric has no previous sample yet.
        /// </summary>
        public List<Entry> Sample(Metric metric, int top)
        {
            lock (sync)
            {
                if (disposed) return null;

                long now = Stopwatch.GetTimestamp();
                var names = GetProcessNames();
                var cur = new Dictionary<int, Snap>();
                foreach (var pid in names.Keys)
                {
                    if (pid == 0) continue;
                    if (TryReadProcess(pid, out Snap s))
                        cur[pid] = s;
                }

                double elapsedSec = prev == null ? 0 : (now - prevTimestamp) / (double)Stopwatch.Frequency;
                var perPid = new Dictionary<int, double>();
                bool ready = true;

                switch (metric)
                {
                    case Metric.CPU:
                    case Metric.DISK:
                        if (prev == null || elapsedSec <= 0)
                        {
                            ready = false;
                            break;
                        }
                        foreach (var kv in cur)
                        {
                            if (!prev.TryGetValue(kv.Key, out Snap old) || old.Creation != kv.Value.Creation)
                                continue;
                            double v;
                            if (metric == Metric.CPU)
                                v = (kv.Value.CpuTime - old.CpuTime) / (elapsedSec * 1e7 * Environment.ProcessorCount) * 100.0;
                            else
                                v = (kv.Value.IoBytes - old.IoBytes) / elapsedSec;
                            perPid[kv.Key] = Math.Max(0, v);
                        }
                        break;
                    case Metric.MEM:
                        foreach (var kv in cur)
                            perPid[kv.Key] = kv.Value.WorkingSet;
                        break;
                    case Metric.GPU:
                    case Metric.GPUMEM:
                        ready = ReadGpu(metric, perPid);
                        break;
                }

                prev = cur;
                prevTimestamp = now;

                if (!ready) return null;

                return perPid
                    .Where(kv => names.ContainsKey(kv.Key))
                    .GroupBy(kv => names[kv.Key], StringComparer.OrdinalIgnoreCase)
                    .Select(g => new Entry { Name = g.Key, Count = g.Count(), Value = g.Sum(x => x.Value) })
                    .OrderByDescending(e => e.Value)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(top)
                    .ToList();
            }
        }

        private bool ReadGpu(Metric metric, Dictionary<int, double> perPid)
        {
            if (gpuReader == null)
            {
                gpuReader = new PerformanceCounterReader(TimeSpan.FromSeconds(5));
                gpuReader.AddPath(GpuEnginePath);
                gpuReader.AddPath(GpuMemPath);
            }
            var values = gpuReader.ReadCounters();

            if (metric == Metric.GPU)
            {
                // utilization is a rate counter: the first collection has no valid value
                if (!gpuPrimed)
                {
                    gpuPrimed = true;
                    return false;
                }
                // per pid: sum per engine type, then take the busiest engine type (same as Task Manager)
                var perPidEngine = new Dictionary<int, Dictionary<string, double>>();
                string prefix = GpuEnginePath + ":";
                foreach (var kv in values)
                {
                    if (!kv.Key.StartsWith(prefix)) continue;
                    string instance = kv.Key.Substring(prefix.Length);
                    var m = PidRegex.Match(instance);
                    if (!m.Success) continue;
                    int pid = int.Parse(m.Groups[1].Value);
                    int idx = instance.IndexOf("engtype_", StringComparison.Ordinal);
                    string eng = idx >= 0 ? instance.Substring(idx + 8) : "";
                    if (!perPidEngine.TryGetValue(pid, out var engines))
                        perPidEngine[pid] = engines = new Dictionary<string, double>();
                    engines.TryGetValue(eng, out double sum);
                    engines[eng] = sum + kv.Value;
                }
                foreach (var kv in perPidEngine)
                    perPid[kv.Key] = Math.Min(100.0, kv.Value.Values.Max());
            }
            else
            {
                string prefix = GpuMemPath + ":";
                foreach (var kv in values)
                {
                    if (!kv.Key.StartsWith(prefix)) continue;
                    var m = PidRegex.Match(kv.Key.Substring(prefix.Length));
                    if (!m.Success) continue;
                    int pid = int.Parse(m.Groups[1].Value);
                    perPid.TryGetValue(pid, out double sum);
                    perPid[pid] = sum + kv.Value;
                }
            }
            return true;
        }

        private static Dictionary<int, string> GetProcessNames()
        {
            var names = new Dictionary<int, string>();
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return names; }
            foreach (var p in procs)
            {
                try { names[p.Id] = p.ProcessName; }
                catch { }
                finally { p.Dispose(); }
            }
            return names;
        }

        private static bool TryReadProcess(int pid, out Snap snap)
        {
            snap = new Snap();
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                if (!GetProcessTimes(h, out long creation, out _, out long kernel, out long user))
                    return false;
                snap.Creation = creation;
                snap.CpuTime = kernel + user;

                if (GetProcessIoCounters(h, out IO_COUNTERS io))
                    snap.IoBytes = io.ReadTransferCount + io.WriteTransferCount;

                var mem = new PROCESS_MEMORY_COUNTERS();
                mem.cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS));
                if (K32GetProcessMemoryInfo(h, ref mem, mem.cb))
                    snap.WorkingSet = mem.WorkingSetSize.ToUInt64();
                return true;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                gpuReader?.Dispose();
                gpuReader = null;
            }
        }

        #region Win32
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll")]
        private static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

        [DllImport("kernel32.dll")]
        private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS counters, uint cb);
        #endregion
    }
}
