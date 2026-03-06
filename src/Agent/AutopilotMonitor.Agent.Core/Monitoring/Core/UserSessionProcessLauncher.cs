using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using AutopilotMonitor.Agent.Core.Logging;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Launches a process in the logged-in user's desktop session from a SYSTEM context.
    /// Uses CreateProcessAsUser with a token duplicated from explorer.exe.
    /// </summary>
    internal static class UserSessionProcessLauncher
    {
        /// <summary>
        /// System/service account names that should NOT be considered real users.
        /// </summary>
        private static readonly string[] ExcludedUserNames =
        {
            "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE",
            "DefaultUser0", "DefaultUser1", "defaultuser0", "defaultuser1"
        };

        /// <summary>
        /// Launches the specified executable in the logged-in user's desktop session.
        /// Returns true if the process was launched successfully, false otherwise.
        /// This is fire-and-forget — the caller does not wait for the process to exit.
        /// </summary>
        public static bool LaunchInUserSession(string exePath, string arguments, AgentLogger logger)
        {
            IntPtr userToken = IntPtr.Zero;
            IntPtr duplicatedToken = IntPtr.Zero;
            IntPtr environment = IntPtr.Zero;

            try
            {
                // Find explorer.exe in a non-zero session owned by a real user
                var explorerProcess = FindExplorerProcess(logger);
                if (explorerProcess == null)
                {
                    logger.Warning("UserSessionProcessLauncher: No explorer.exe found in user session — cannot launch dialog");
                    return false;
                }

                using (explorerProcess)
                {
                    logger.Info($"UserSessionProcessLauncher: Found explorer.exe PID {explorerProcess.Id} in session {explorerProcess.SessionId}");

                    // Open the token of explorer.exe
                    if (!NativeMethods.OpenProcessToken(explorerProcess.Handle,
                        NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_QUERY, out userToken))
                    {
                        logger.Warning($"UserSessionProcessLauncher: OpenProcessToken failed (error {Marshal.GetLastWin32Error()})");
                        return false;
                    }

                    // Duplicate the token as a primary token for CreateProcessAsUser
                    var sa = new NativeMethods.SECURITY_ATTRIBUTES();
                    sa.nLength = Marshal.SizeOf(sa);

                    if (!NativeMethods.DuplicateTokenEx(userToken,
                        NativeMethods.MAXIMUM_ALLOWED, ref sa,
                        NativeMethods.SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
                        NativeMethods.TOKEN_TYPE.TokenPrimary, out duplicatedToken))
                    {
                        logger.Warning($"UserSessionProcessLauncher: DuplicateTokenEx failed (error {Marshal.GetLastWin32Error()})");
                        return false;
                    }

                    // Create an environment block for the user
                    if (!NativeMethods.CreateEnvironmentBlock(out environment, duplicatedToken, false))
                    {
                        logger.Warning($"UserSessionProcessLauncher: CreateEnvironmentBlock failed (error {Marshal.GetLastWin32Error()})");
                        // Non-fatal: we can proceed without it, but env vars may be wrong
                        environment = IntPtr.Zero;
                    }

                    // Set up startup info to target the user's desktop
                    var si = new NativeMethods.STARTUPINFO();
                    si.cb = Marshal.SizeOf(si);
                    si.lpDesktop = @"WinSta0\Default";

                    var creationFlags = NativeMethods.CREATE_NEW_CONSOLE;
                    if (environment != IntPtr.Zero)
                        creationFlags |= NativeMethods.CREATE_UNICODE_ENVIRONMENT;

                    var commandLine = $"\"{exePath}\" {arguments}";

                    var procSa = new NativeMethods.SECURITY_ATTRIBUTES();
                    procSa.nLength = Marshal.SizeOf(procSa);
                    var threadSa = new NativeMethods.SECURITY_ATTRIBUTES();
                    threadSa.nLength = Marshal.SizeOf(threadSa);

                    if (!NativeMethods.CreateProcessAsUser(
                        duplicatedToken,
                        null,           // lpApplicationName (null, use command line)
                        commandLine,    // lpCommandLine
                        ref procSa,
                        ref threadSa,
                        false,          // bInheritHandles
                        creationFlags,
                        environment,
                        null,           // lpCurrentDirectory (inherit)
                        ref si,
                        out var pi))
                    {
                        logger.Warning($"UserSessionProcessLauncher: CreateProcessAsUser failed (error {Marshal.GetLastWin32Error()})");
                        return false;
                    }

                    // Close the process and thread handles — we don't need them (fire-and-forget)
                    NativeMethods.CloseHandle(pi.hProcess);
                    NativeMethods.CloseHandle(pi.hThread);

                    logger.Info($"UserSessionProcessLauncher: Successfully launched process PID {pi.dwProcessId} in user session");
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"UserSessionProcessLauncher: Exception: {ex.Message}");
                return false;
            }
            finally
            {
                if (environment != IntPtr.Zero)
                    NativeMethods.DestroyEnvironmentBlock(environment);
                if (duplicatedToken != IntPtr.Zero)
                    NativeMethods.CloseHandle(duplicatedToken);
                if (userToken != IntPtr.Zero)
                    NativeMethods.CloseHandle(userToken);
            }
        }

        /// <summary>
        /// Finds an explorer.exe process running in a non-SYSTEM user session.
        /// Returns the Process handle, or null if none found.
        /// </summary>
        private static Process FindExplorerProcess(AgentLogger logger)
        {
            try
            {
                var explorerProcesses = Process.GetProcessesByName("explorer");
                foreach (var proc in explorerProcesses)
                {
                    try
                    {
                        if (proc.SessionId == 0)
                        {
                            proc.Dispose();
                            continue;
                        }

                        var owner = GetProcessOwner(proc.Id);
                        if (owner == null || IsExcludedUser(owner))
                        {
                            proc.Dispose();
                            continue;
                        }

                        // Found a valid user-session explorer.exe
                        // Dispose the others
                        foreach (var other in explorerProcesses)
                        {
                            if (other.Id != proc.Id)
                            {
                                try { other.Dispose(); } catch { }
                            }
                        }
                        return proc;
                    }
                    catch
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug($"UserSessionProcessLauncher: Error finding explorer.exe: {ex.Message}");
            }

            return null;
        }

        private static string GetProcessOwner(int processId)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_Process WHERE ProcessId = {processId}"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var outParams = new object[2];
                        var result = (uint)obj.InvokeMethod("GetOwner", outParams);
                        if (result == 0)
                        {
                            var user = outParams[0]?.ToString();
                            var domain = outParams[1]?.ToString();
                            return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool IsExcludedUser(string fullUserName)
        {
            if (string.IsNullOrEmpty(fullUserName))
                return true;

            var userName = fullUserName;
            var backslashIndex = fullUserName.LastIndexOf('\\');
            if (backslashIndex >= 0 && backslashIndex < fullUserName.Length - 1)
                userName = fullUserName.Substring(backslashIndex + 1);

            foreach (var excluded in ExcludedUserNames)
            {
                if (string.Equals(userName, excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (userName.StartsWith("DefaultUser", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// Win32 P/Invoke declarations for process creation in user session.
        /// </summary>
        private static class NativeMethods
        {
            public const uint TOKEN_DUPLICATE = 0x0002;
            public const uint TOKEN_QUERY = 0x0008;
            public const uint MAXIMUM_ALLOWED = 0x02000000;
            public const uint CREATE_NEW_CONSOLE = 0x00000010;
            public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

            public enum SECURITY_IMPERSONATION_LEVEL
            {
                SecurityAnonymous,
                SecurityIdentification,
                SecurityImpersonation,
                SecurityDelegation
            }

            public enum TOKEN_TYPE
            {
                TokenPrimary = 1,
                TokenImpersonation
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct SECURITY_ATTRIBUTES
            {
                public int nLength;
                public IntPtr lpSecurityDescriptor;
                public bool bInheritHandle;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct STARTUPINFO
            {
                public int cb;
                public string lpReserved;
                public string lpDesktop;
                public string lpTitle;
                public int dwX;
                public int dwY;
                public int dwXSize;
                public int dwYSize;
                public int dwXCountChars;
                public int dwYCountChars;
                public int dwFillAttribute;
                public int dwFlags;
                public short wShowWindow;
                public short cbReserved2;
                public IntPtr lpReserved2;
                public IntPtr hStdInput;
                public IntPtr hStdOutput;
                public IntPtr hStdError;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct PROCESS_INFORMATION
            {
                public IntPtr hProcess;
                public IntPtr hThread;
                public int dwProcessId;
                public int dwThreadId;
            }

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool DuplicateTokenEx(
                IntPtr hExistingToken, uint dwDesiredAccess,
                ref SECURITY_ATTRIBUTES lpTokenAttributes,
                SECURITY_IMPERSONATION_LEVEL impersonationLevel,
                TOKEN_TYPE tokenType, out IntPtr phNewToken);

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool CreateProcessAsUser(
                IntPtr hToken, string lpApplicationName, string lpCommandLine,
                ref SECURITY_ATTRIBUTES lpProcessAttributes,
                ref SECURITY_ATTRIBUTES lpThreadAttributes,
                bool bInheritHandles, uint dwCreationFlags,
                IntPtr lpEnvironment, string lpCurrentDirectory,
                ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

            [DllImport("userenv.dll", SetLastError = true)]
            public static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

            [DllImport("userenv.dll", SetLastError = true)]
            public static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr hObject);
        }
    }
}
