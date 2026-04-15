namespace AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;

/// <summary>
/// Accumulates multi-line script execution data from IME logs before emitting
/// a single consolidated event. Used for both platform scripts and remediation scripts.
/// </summary>
public class ScriptExecutionState
{
    /// <summary>Intune policy GUID identifying the script.</summary>
    public string PolicyId { get; set; }

    /// <summary>"platform" or "remediation".</summary>
    public string ScriptType { get; set; }

    /// <summary>"detection" or "remediation" (remediation scripts only).</summary>
    public string ScriptPart { get; set; }

    /// <summary>"System" or "User" execution context.</summary>
    public string RunContext { get; set; }

    /// <summary>PowerShell exit code.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Standard output (truncated).</summary>
    public string Stdout { get; set; }

    /// <summary>Standard error output (truncated).</summary>
    public string Stderr { get; set; }

    /// <summary>"Success" or "Failed" (platform scripts).</summary>
    public string Result { get; set; }

    /// <summary>"True" or "False" compliance result (remediation detection only).</summary>
    public string ComplianceResult { get; set; }
}
