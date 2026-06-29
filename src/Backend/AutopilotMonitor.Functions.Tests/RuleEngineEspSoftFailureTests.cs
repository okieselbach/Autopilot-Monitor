using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins ANALYZE-ESP-004 ("ESP Timeout with 'Continue Anyway' — Soft Failure").
///
/// Background (tenant c9787ba2, session cb7036a6): a slow blocking app (Encompass
/// Hybrid Installer) does not finish inside the 30-min Device-ESP window, so the ESP
/// fails terminally in DeviceSetup. The profile allows "Continue anyway", so the user
/// most likely dismissed the failure screen and reached the desktop — but the agent
/// only ever sees the terminal failure and the session is recorded as Failed. The
/// DecisionEngine already stamps the enrollment_failed event with
/// <c>mayHaveContinuedAnyway=true</c>; this rule turns that into a customer-visible
/// "soft failure" advisory with the root-cause remediation.
///
/// The rule keys ONLY on <c>mayHaveContinuedAnyway==true</c> (set exclusively on the
/// ESP terminal-failure path), so an ordinary hard failure must not trip it.
/// </summary>
public class RuleEngineEspSoftFailureTests
{
    private const string TenantId  = "c9787ba2-29de-4944-91f0-73594c12f85d";
    private const string SessionId = "cb7036a6-2c7c-470f-851b-24e5e537991c";
    private const string FailedAppName = "Encompass Hybrid Installer";

    [Fact]
    public async Task ANALYZE_ESP_004_fires_on_continue_anyway_soft_failure()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ESP-004");
        Assert.True(rule.Enabled, "ANALYZE-ESP-004 should be enabled by default");
        Assert.Equal("warning", rule.Severity);
        Assert.Empty(rule.TemplateVariables ?? new List<TemplateVariable>());

        var events = new List<EnrollmentEvent>
        {
            EnrollmentFailedEvent(mayHaveContinuedAnyway: "true"),
            AppInstallFailed(),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-ESP-004", result.RuleId);
        Assert.Equal("warning", result.Severity);

        // Required gate matched on the continue-anyway flag.
        var gate = AsDict(result.MatchedConditions["may_have_continued"]);
        Assert.Equal("true", AsString(gate["value"]));

        // Interpolation material for the explanation / remediation templates is present:
        // {{espSyncFailureTimeoutMinutes}}, {{failedSubcategory}} (from enrollment_failed)
        // and {{appName}} (from app_install_failed).
        var timeout = AsDict(result.MatchedConditions["espSyncFailureTimeoutMinutes"]);
        Assert.Equal("30", AsString(timeout["value"]));

        var subcategory = AsDict(result.MatchedConditions["failedSubcategory"]);
        Assert.Equal("Certificates", AsString(subcategory["value"]));

        var failedApp = AsDict(result.MatchedConditions["appName"]);
        Assert.Equal("appName", AsString(failedApp["field"]));
        Assert.Equal(FailedAppName, AsString(failedApp["value"]));
    }

    [Fact]
    public async Task ANALYZE_ESP_004_does_not_fire_when_continue_anyway_is_false()
    {
        // Hard failure (profile blocks "Continue anyway"): the device is genuinely stuck,
        // so the soft-failure advisory must stay silent — ANALYZE-ENRL-001 owns that case.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ESP-004");

        var events = new List<EnrollmentEvent>
        {
            EnrollmentFailedEvent(mayHaveContinuedAnyway: "false"),
            AppInstallFailed(),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_ESP_004_does_not_fire_when_flag_absent()
    {
        // Older agents (or non-ESP failure paths) emit enrollment_failed without the
        // mayHaveContinuedAnyway flag — the required gate must fail closed.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ESP-004");

        var events = new List<EnrollmentEvent>
        {
            EnrollmentFailedEvent(mayHaveContinuedAnyway: null),
            AppInstallFailed(),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    // ===== Event builders (faithful to session cb7036a6's actual payloads) =====

    private static EnrollmentEvent EnrollmentFailedEvent(string? mayHaveContinuedAnyway) =>
        new()
        {
            EventId = Guid.NewGuid().ToString(),
            TenantId = TenantId,
            SessionId = SessionId,
            EventType = "enrollment_failed",
            Timestamp = DateTime.UtcNow,
            Sequence = 224,
            Data = BuildFailureData(mayHaveContinuedAnyway),
        };

    private static Dictionary<string, object> BuildFailureData(string? mayHaveContinuedAnyway)
    {
        var data = new Dictionary<string, object>
        {
            ["reason"] = "esp_terminal_failure",
            ["decisionSource"] = "DecisionEngine",
            ["trigger"] = "EspTerminalFailure",
            ["sessionStage"] = "Failed",
            ["failureType"] = "Provisioning_DeviceSetup_Certificates_Failed",
            ["failedSubcategory"] = "Certificates",
            ["category"] = "DeviceSetup",
            ["espSyncFailureTimeoutMinutes"] = "30",
            ["espAllowContinueAnyway"] = "true",
        };
        if (mayHaveContinuedAnyway != null)
            data["mayHaveContinuedAnyway"] = mayHaveContinuedAnyway;
        return data;
    }

    private static EnrollmentEvent AppInstallFailed() => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "app_install_failed",
        Timestamp = DateTime.UtcNow,
        Sequence = 220,
        Data = new Dictionary<string, object>
        {
            ["appName"] = FailedAppName,
            ["state"] = "Error",
            ["targeted"] = "Device",
            ["failureType"] = "esp_apps_timeout",
            ["errorDetail"] = "Install status unconfirmed — ESP gave up while this app was still installing.",
        }
    };

    private static Dictionary<string, object> AsDict(object o)
    {
        if (o is Dictionary<string, object> d) return d;
        throw new InvalidOperationException($"Expected Dictionary<string,object>, got {o?.GetType().Name ?? "null"}");
    }

    private static string AsString(object o) => o?.ToString() ?? string.Empty;

    private static async Task<AnalysisOutcome> RunAsync(AnalyzeRule rule, List<EnrollmentEvent> events)
    {
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule> { rule });
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new List<RuleResult>());

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineEspSoftFailureTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
