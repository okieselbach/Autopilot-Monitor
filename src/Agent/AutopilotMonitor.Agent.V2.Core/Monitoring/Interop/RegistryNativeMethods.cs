using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    internal static class RegistryNativeMethods
    {
        public const int KEY_NOTIFY = 0x0010;
        public const int KEY_WOW64_64KEY = 0x0100;
        public const int KEY_WOW64_32KEY = 0x0200;

        [Flags]
        public enum RegChangeNotifyFilter : uint
        {
            Name = 0x00000001,
            Attributes = 0x00000002,
            LastSet = 0x00000004,
            Security = 0x00000008,

            // Supported on Windows 8+
            ThreadAgnostic = 0x10000000
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RegOpenKeyEx(
            UIntPtr hKey,
            string lpSubKey,
            uint ulOptions,
            int samDesired,
            out SafeRegistryHandle phkResult);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int RegNotifyChangeKeyValue(
            SafeRegistryHandle hKey,
            bool bWatchSubtree,
            RegChangeNotifyFilter dwNotifyFilter,
            SafeWaitHandle hEvent,
            bool fAsynchronous);

        public static int GetSamDesired(RegistryView view)
        {
            switch (view)
            {
                case RegistryView.Registry64:
                    return KEY_NOTIFY | KEY_WOW64_64KEY;

                case RegistryView.Registry32:
                    return KEY_NOTIFY | KEY_WOW64_32KEY;

                case RegistryView.Default:
                    return KEY_NOTIFY;

                default:
                    throw new ArgumentOutOfRangeException(nameof(view));
            }
        }
    }
}