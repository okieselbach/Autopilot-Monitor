using System.Runtime.CompilerServices;
using AutopilotMonitor.Shared.Diagnostics;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Test-process-wide setup. Runs once before any test executes (via the
/// <c>[ModuleInitializer]</c> hook) so individual tests don't have to bother
/// with global state — in particular, the ContinuationToken signing key.
/// </summary>
internal static class TestSetup
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Fixed deterministic key for reproducible test runs. Any 32 bytes will
        // do — production reads from the PaginationTokenSigningKey env var which
        // we deliberately do NOT inspect here, so test runs aren't affected by
        // (or able to leak into) production secrets.
        var key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 7 + 13);
        ContinuationToken.SetSigningKeyForTesting(key);
        DiagnosticsDownloadTicket.SetSigningKeyForTesting(key);
    }
}
