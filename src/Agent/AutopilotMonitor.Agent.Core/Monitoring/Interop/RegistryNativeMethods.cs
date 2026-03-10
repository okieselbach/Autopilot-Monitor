using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutopilotMonitor.Agent.Core.Monitoring.Interop
{
    /// <summary>
    /// P/Invoke declarations for registry change notification via RegNotifyChangeKeyValue.
    /// Uses SafeRegistryHandle and SafeWaitHandle for proper handle lifecycle management.
    /// Used by RegistryWatcher to detect registry changes instantly.
    /// </summary>
    internal static class RegistryNativeMethods
    {
        public const int KEY_NOTIFY = 0x0010;

        [Flags]
        public enum RegChangeNotifyFilter : uint
        {
            Name = 1,
            Attributes = 2,
            LastSet = 4,
            Security = 8
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RegOpenKeyEx(
            UIntPtr hKey, string lpSubKey, uint ulOptions,
            int samDesired, out SafeRegistryHandle phkResult);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int RegNotifyChangeKeyValue(
            SafeRegistryHandle hKey, bool bWatchSubtree,
            RegChangeNotifyFilter dwNotifyFilter,
            SafeWaitHandle hEvent, bool fAsynchronous);
    }
}
