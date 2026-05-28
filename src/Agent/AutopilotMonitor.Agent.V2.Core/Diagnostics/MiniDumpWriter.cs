#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AutopilotMonitor.Agent.V2.Core.Diagnostics
{
    /// <summary>
    /// Thin P/Invoke wrapper around <c>MiniDumpWriteDump</c> from <c>dbghelp.dll</c>.
    /// Writes a compact MiniDump of the current process so the managed stack and key
    /// metadata can be recovered post-mortem.
    /// <para>
    /// Designed to run from inside an <see cref="AppDomain.UnhandledException"/> handler —
    /// fully synchronous, swallows all exceptions, returns <c>false</c> on any failure.
    /// </para>
    /// </summary>
    public static class MiniDumpWriter
    {
        /// <summary>
        /// Dump flags chosen for a small-but-useful dump (~5-20 MB for the agent):
        /// data-segs (globals), handles (loaded modules), thread-info (state/priority/affinity),
        /// unloaded-modules (for module-load failures). No full memory — that would push
        /// the dump to 100+ MB and break diagnostics-upload size budgets.
        /// </summary>
        private const uint MiniDumpFlags =
            0x00000001 |  // MiniDumpWithDataSegs
            0x00000004 |  // MiniDumpWithHandleData
            0x00001000 |  // MiniDumpWithThreadInfo
            0x00000020;   // MiniDumpWithUnloadedModules

        public static bool TryWriteDump(string dumpFilePath)
        {
            if (string.IsNullOrEmpty(dumpFilePath)) return false;

            FileStream? fs = null;
            try
            {
                var dir = Path.GetDirectoryName(dumpFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                fs = new FileStream(dumpFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                var process = Process.GetCurrentProcess();
                return MiniDumpWriteDump(
                    process.Handle,
                    (uint)process.Id,
                    fs.SafeFileHandle.DangerousGetHandle(),
                    MiniDumpFlags,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }
            catch
            {
                return false;
            }
            finally
            {
                try { fs?.Dispose(); } catch { }
            }
        }

        [DllImport("dbghelp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MiniDumpWriteDump(
            IntPtr hProcess,
            uint processId,
            IntPtr hFile,
            uint dumpType,
            IntPtr expParam,
            IntPtr userStreamParam,
            IntPtr callbackParam);
    }
}
