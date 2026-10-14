using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DIE.ServerConsole
{
    /// <summary>
    /// 负载: 服务端进程的 CPU / 内存 / 线程, 整机的 CPU / 内存。两次采样之间的差算百分比。
    /// </summary>
    internal sealed class SystemLoad
    {
        private long _lastIdle;
        private long _lastKernel;
        private long _lastUser;

        private TimeSpan _lastProcessCpu;
        private DateTime _lastProcessSample;
        private int _lastPid;

        /// <summary>整机 CPU 占用 %; 第一次采样返回 NaN。</summary>
        public double MachineCpu()
        {
            if (!GetSystemTimes(out long idle, out long kernel, out long user))
            {
                return double.NaN;
            }

            long idleDelta = idle - _lastIdle;
            long totalDelta = kernel - _lastKernel + (user - _lastUser);     // kernel 时间里含 idle
            bool first = _lastKernel == 0;

            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;

            if (first || totalDelta <= 0)
            {
                return double.NaN;
            }

            return Clamp(100.0 * (totalDelta - idleDelta) / totalDelta);
        }

        /// <summary>整机内存: (已用 MB, 总共 MB)。</summary>
        public (long UsedMb, long TotalMb) MachineMemory()
        {
            MemoryStatus status = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (!GlobalMemoryStatusEx(ref status))
            {
                return (0, 0);
            }

            long total = (long)(status.TotalPhys / (1024 * 1024));
            long free = (long)(status.AvailPhys / (1024 * 1024));
            return (total - free, total);
        }

        /// <summary>进程 CPU 占用 % (按整机核数折算, 满载 100%); 第一次采样返回 NaN。</summary>
        public double ProcessCpu(Process process)
        {
            try
            {
                process.Refresh();
                TimeSpan cpu = process.TotalProcessorTime;
                DateTime now = DateTime.UtcNow;

                bool first = _lastPid != process.Id;
                double elapsed = (now - _lastProcessSample).TotalMilliseconds;
                double used = (cpu - _lastProcessCpu).TotalMilliseconds;

                _lastPid = process.Id;
                _lastProcessCpu = cpu;
                _lastProcessSample = now;

                if (first || elapsed <= 0)
                {
                    return double.NaN;
                }

                return Clamp(100.0 * used / (elapsed * Environment.ProcessorCount));
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                return double.NaN;
            }
        }

        private static double Clamp(double value) => Math.Max(0, Math.Min(100, value));

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    }
}
