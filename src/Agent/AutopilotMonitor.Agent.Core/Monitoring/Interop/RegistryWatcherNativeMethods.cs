using System;
using System.Runtime.InteropServices;

namespace AutopilotMonitor.Agent.Core.Monitoring.Interop
{
    /// <summary>
    /// P/Invoke declarations for registry change notification via RegNotifyChangeKeyValue.
    /// Used by EspAndHelloTracker.ProvisioningStatusTracking to detect provisioning status changes instantly.
    /// </summary>
    internal static class RegistryWatcherNativeMethods
    {
        public static readonly IntPtr HKEY_LOCAL_MACHINE = new IntPtr(unchecked((int)0x80000002));

        public const uint KEY_NOTIFY = 0x0010;
        public const uint KEY_READ = 0x20019;
        public const uint REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;
        public const uint WAIT_OBJECT_0 = 0;
        public const uint WAIT_TIMEOUT = 258;
        public const uint INFINITE = 0xFFFFFFFF;

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int RegOpenKeyEx(
            IntPtr hKey, string subKey, uint options, uint samDesired, out IntPtr result);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int RegNotifyChangeKeyValue(
            IntPtr hKey, bool watchSubtree, uint notifyFilter, IntPtr hEvent, bool asynchronous);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int RegCloseKey(IntPtr hKey);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEvent(
            IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForMultipleObjects(
            uint nCount, IntPtr[] lpHandles, bool bWaitAll, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
