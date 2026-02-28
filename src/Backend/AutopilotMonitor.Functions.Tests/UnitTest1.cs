using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

public class BuiltInRulesTests
{
    [Fact]
    public void BuiltInGatherRules_LoadsFromEmbeddedResource()
    {
        var rules = BuiltInGatherRules.GetAll();
        Assert.NotNull(rules);
        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => r.RuleId == "GATHER-DEVICE-001");
        Assert.Contains(rules, r => r.RuleId == "GATHER-DEVICE-002");

        foreach (var rule in rules)
        {
            Assert.False(string.IsNullOrEmpty(rule.RuleId), "RuleId must not be empty");
            Assert.False(string.IsNullOrEmpty(rule.Title), $"Rule {rule.RuleId} is missing Title");
            Assert.False(string.IsNullOrEmpty(rule.CollectorType), $"Rule {rule.RuleId} is missing CollectorType");
            Assert.False(string.IsNullOrEmpty(rule.Target), $"Rule {rule.RuleId} is missing Target");
            Assert.False(string.IsNullOrEmpty(rule.Trigger), $"Rule {rule.RuleId} is missing Trigger");
            Assert.False(string.IsNullOrEmpty(rule.OutputEventType), $"Rule {rule.RuleId} is missing OutputEventType");
            Assert.True(rule.IsBuiltIn, $"Rule {rule.RuleId} should have IsBuiltIn=true");
        }
    }

    [Fact]
    public void BuiltInAnalyzeRules_LoadsFromEmbeddedResource()
    {
        var rules = BuiltInAnalyzeRules.GetAll();
        Assert.NotNull(rules);
        Assert.True(rules.Count >= 18, $"Expected at least 18 rules, got {rules.Count}");

        foreach (var rule in rules)
        {
            Assert.False(string.IsNullOrEmpty(rule.RuleId), "RuleId must not be empty");
            Assert.False(string.IsNullOrEmpty(rule.Title), $"Rule {rule.RuleId} is missing Title");
            Assert.False(string.IsNullOrEmpty(rule.Severity), $"Rule {rule.RuleId} is missing Severity");
            Assert.False(string.IsNullOrEmpty(rule.Explanation), $"Rule {rule.RuleId} is missing Explanation");
            Assert.NotNull(rule.Conditions);
            Assert.NotEmpty(rule.Conditions);
            Assert.True(rule.IsBuiltIn, $"Rule {rule.RuleId} should have IsBuiltIn=true");
        }
    }

    [Fact]
    public void BuiltInImeLogPatterns_LoadsFromEmbeddedResource()
    {
        var patterns = BuiltInImeLogPatterns.GetAll();
        Assert.NotNull(patterns);
        Assert.True(patterns.Count >= 47, $"Expected at least 47 patterns, got {patterns.Count}");

        foreach (var pattern in patterns)
        {
            Assert.False(string.IsNullOrEmpty(pattern.PatternId), "PatternId must not be empty");
            Assert.False(string.IsNullOrEmpty(pattern.Category), $"Pattern {pattern.PatternId} is missing Category");
            Assert.False(string.IsNullOrEmpty(pattern.Pattern), $"Pattern {pattern.PatternId} is missing Pattern");
            Assert.False(string.IsNullOrEmpty(pattern.Action), $"Pattern {pattern.PatternId} is missing Action");
            Assert.False(string.IsNullOrEmpty(pattern.Description),
                $"Pattern {pattern.PatternId} is missing Description");
            Assert.True(pattern.IsBuiltIn, $"Pattern {pattern.PatternId} should have IsBuiltIn=true");
        }
    }

    [Fact]
    public void BuiltInImeLogPatterns_AllActionsAreKnown()
    {
        var knownActions = new HashSet<string>
        {
            "setCurrentApp", "updateStateInstalled", "updateStateDownloading",
            "updateStateInstalling", "updateStateSkipped", "updateStateError",
            "updateStatePostponed", "espPhaseDetected", "imeStarted",
            "policiesDiscovered", "ignoreCompletedApp", "imeAgentVersion",
            "espTrackStatus", "updateName", "updateWin32AppState",
            "cancelStuckAndSetCurrent", "imeSessionChange", "imeImpersonation",
            "enrollmentCompleted"
        };

        var patterns = BuiltInImeLogPatterns.GetAll();
        foreach (var pattern in patterns)
        {
            Assert.True(knownActions.Contains(pattern.Action),
                $"Pattern {pattern.PatternId} has unknown action '{pattern.Action}'");
        }
    }

    [Fact]
    public void BuiltInImeLogPatterns_AllCategoriesAreValid()
    {
        var validCategories = new HashSet<string> { "always", "currentPhase", "otherPhases" };

        var patterns = BuiltInImeLogPatterns.GetAll();
        foreach (var pattern in patterns)
        {
            Assert.True(validCategories.Contains(pattern.Category),
                $"Pattern {pattern.PatternId} has invalid category '{pattern.Category}'");
        }
    }
}
