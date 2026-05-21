#nullable enable
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
{
    /// <summary>
    /// Resolves a Windows account name (<c>DOMAIN\\User</c> or just <c>User</c>) to its
    /// string-form SID via P/Invoke. Used by <c>RealmJoinHost</c> to attach a registry watcher
    /// to <c>HKEY_USERS\&lt;sid&gt;\SOFTWARE\RealmJoin\Packages</c> after the
    /// <see cref="Enrollment.SystemSignals.DesktopArrivalDetector"/> reports a real user.
    /// </summary>
    /// <remarks>
    /// The agent runs as SYSTEM and has the rights for <c>LookupAccountName</c> without
    /// impersonation. No registry hive load is required — the API resolves the SID from
    /// the account database directly.
    /// </remarks>
    internal static class UserSidResolver
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupAccountNameW(
            string? lpSystemName,
            string lpAccountName,
            IntPtr Sid,
            ref uint cbSid,
            StringBuilder? ReferencedDomainName,
            ref uint cchReferencedDomainName,
            out int peUse);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertSidToStringSidW(
            IntPtr sid,
            out IntPtr stringSid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>
        /// Resolve <paramref name="accountName"/> to a string-form SID (<c>S-1-5-21-...</c>).
        /// Returns <c>true</c> on success; <paramref name="sid"/> is null on failure. Accepts
        /// <c>DOMAIN\\User</c>, <c>User@Domain</c>, and bare <c>User</c> forms.
        /// </summary>
        public static bool TryResolveSid(string accountName, out string? sid)
        {
            sid = null;
            if (string.IsNullOrEmpty(accountName)) return false;

            uint cbSid = 0;
            uint cchDomain = 0;
            // First call probes the required buffer sizes. ERROR_INSUFFICIENT_BUFFER (122) is
            // expected; any other failure mode means the name didn't resolve.
            LookupAccountNameW(
                lpSystemName: null,
                lpAccountName: accountName,
                Sid: IntPtr.Zero,
                cbSid: ref cbSid,
                ReferencedDomainName: null,
                cchReferencedDomainName: ref cchDomain,
                peUse: out _);

            var err = Marshal.GetLastWin32Error();
            if (err != 122 /* ERROR_INSUFFICIENT_BUFFER */ || cbSid == 0)
            {
                return false;
            }

            var sidBuffer = Marshal.AllocHGlobal((int)cbSid);
            var domainBuffer = new StringBuilder((int)cchDomain);
            try
            {
                if (!LookupAccountNameW(
                        lpSystemName: null,
                        lpAccountName: accountName,
                        Sid: sidBuffer,
                        cbSid: ref cbSid,
                        ReferencedDomainName: domainBuffer,
                        cchReferencedDomainName: ref cchDomain,
                        peUse: out _))
                {
                    return false;
                }

                if (!ConvertSidToStringSidW(sidBuffer, out var stringSid) || stringSid == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    sid = Marshal.PtrToStringUni(stringSid);
                    return !string.IsNullOrEmpty(sid);
                }
                finally
                {
                    LocalFree(stringSid);
                }
            }
            catch (Win32Exception)
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(sidBuffer);
            }
        }
    }
}
