using System.Runtime.CompilerServices;

// Internals are exposed to the V2.Core test project so it can cover the startup helpers
// (Program.Guards.cs: DetectPreviousExit, CheckSessionAgeEmergencyBreak, TryReadBootstrapConfig,
// TryReadAwaitEnrollmentConfig, WriteCrashLog, etc.) without duplicating the code into V2.Core.
// Plan §4.x M4.6.α.
[assembly: InternalsVisibleTo("AutopilotMonitor.Agent.V2.Core.Tests")]
