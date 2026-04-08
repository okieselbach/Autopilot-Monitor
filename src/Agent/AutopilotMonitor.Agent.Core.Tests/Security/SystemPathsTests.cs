using System;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.Core.Security;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Security
{
    /// <summary>
    /// Smoke test for <see cref="SystemPaths"/>: every pinned System32 binary must
    /// exist on the test host. This catches typos, bad <c>Path.Combine</c> segments,
    /// and accidental rewrites of the historical <c>WindowsPowerShell\v1.0</c> path —
    /// all failure modes that would brick the Agent on every Windows device.
    /// Windows-only (the Agent runs on net48 and CI runs on Windows agents).
    /// </summary>
    public class SystemPathsTests
    {
        [Fact]
        public void AllPinnedPaths_ExistOnWindows()
        {
            Assert.Equal(PlatformID.Win32NT, Environment.OSVersion.Platform);

            var paths = new (string name, string path)[]
            {
                (nameof(SystemPaths.Cmd),        SystemPaths.Cmd),
                (nameof(SystemPaths.Shutdown),   SystemPaths.Shutdown),
                (nameof(SystemPaths.TzUtil),     SystemPaths.TzUtil),
                (nameof(SystemPaths.Netsh),      SystemPaths.Netsh),
                (nameof(SystemPaths.PowerShell), SystemPaths.PowerShell),
            };

            var missing = paths
                .Where(p => !File.Exists(p.path))
                .Select(p => $"{p.name}: {p.path}")
                .ToList();

            Assert.True(
                missing.Count == 0,
                "SystemPaths point to missing binaries:\n  " + string.Join("\n  ", missing));
        }
    }
}
