using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Validates the rule-JSON side of the KO-criterion feature (MarkSessionAsFailed).
/// Deep end-to-end behavior (RuleEngine actually flipping a session to Failed) is exercised
/// against live Table Storage during manual QA — these tests pin the rule shape so future
/// refactors can't silently break the wiring.
/// </summary>
public class AnalyzeRuleSessionFailureTests
{
    [Fact]
    public void BuiltInAnalyzeRules_EspCertificatesRule_HasExpectedShape()
    {
        var rules = BuiltInAnalyzeRules.GetAll();
        var rule = rules.FirstOrDefault(r => r.RuleId == "ANALYZE-ESP-002");

        Assert.NotNull(rule);
        Assert.Equal("high", rule!.Severity);
        Assert.Equal("esp", rule.Category);

        // The rule must match against the flat top-level field emitted by ProvisioningStatusTracker,
        // not a nested subcategories path — the RuleEngine can't traverse nested collections.
        var cond = rule.Conditions.Single();
        Assert.Equal("esp_provisioning_status", cond.EventType);
        Assert.Equal("event_data", cond.Source);
        Assert.Equal("failedSubcategories", cond.DataField);
        Assert.Equal("contains", cond.Operator);
        Assert.Equal("Certificates", cond.Value);
        Assert.True(cond.Required);
    }

    [Fact]
    public void BuiltInAnalyzeRules_MarkSessionAsFailedDefault_IsFalseForAllBuiltIns()
    {
        // Opt-in only: no built-in rule should force sessions to failed out of the box.
        // Tenants enable KO behavior per rule via the portal. If you add a rule that genuinely
        // needs `true` as a default, update this test with a narrow allow-list.
        foreach (var rule in BuiltInAnalyzeRules.GetAll())
        {
            Assert.False(rule.MarkSessionAsFailedDefault,
                $"Rule {rule.RuleId} ships with MarkSessionAsFailedDefault=true, which would fail sessions for every tenant without opt-in.");
        }
    }

    [Fact]
    public void RuleState_NullOverride_MeansInheritDefault()
    {
        // The dict returned by GetRuleStatesAsync maps ruleId → RuleState. A null MarkSessionAsFailed
        // is the canonical "tenant did not express a preference" value and AnalyzeRuleService
        // must copy it through to AnalyzeRule.MarkSessionAsFailed unchanged.
        var state = new RuleState { Enabled = true, MarkSessionAsFailed = null };
        var rule = new AnalyzeRule
        {
            RuleId = "ANALYZE-ESP-002",
            MarkSessionAsFailedDefault = false
        };

        rule.Enabled = state.Enabled;
        rule.MarkSessionAsFailed = state.MarkSessionAsFailed;

        var effective = rule.MarkSessionAsFailed ?? rule.MarkSessionAsFailedDefault;
        Assert.False(effective);
    }

    [Fact]
    public void RuleState_ExplicitOverride_WinsOverDefault()
    {
        var state = new RuleState { Enabled = true, MarkSessionAsFailed = true };
        var rule = new AnalyzeRule
        {
            RuleId = "ANALYZE-ESP-002",
            MarkSessionAsFailedDefault = false
        };

        rule.MarkSessionAsFailed = state.MarkSessionAsFailed;
        var effective = rule.MarkSessionAsFailed ?? rule.MarkSessionAsFailedDefault;
        Assert.True(effective);
    }
}
