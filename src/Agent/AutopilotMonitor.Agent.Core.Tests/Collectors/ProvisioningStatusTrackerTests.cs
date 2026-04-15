using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AutopilotMonitor.Agent.Core.Tests.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;

namespace AutopilotMonitor.Agent.Core.Tests.Collectors
{
    /// <summary>
    /// Tests for <see cref="ProvisioningStatusTracker"/>. Drives category-status processing via
    /// <c>ProcessCategoryStatusForTest</c> with synthetic JSON payloads to exercise first-seen,
    /// subcategory transitions, categorySucceeded resolution, failure escalation, DeviceSetup
    /// completion event, and snapshot APIs. Registry / RegistryWatcher are NOT invoked.
    /// </summary>
    public sealed class ProvisioningStatusTrackerTests
    {
        private readonly ConcurrentBag<EnrollmentEvent> _captured = new ConcurrentBag<EnrollmentEvent>();
        private readonly List<string> _espFailures = new List<string>();
        private int _deviceSetupCompleteCount;

        private ProvisioningStatusTracker CreateTracker()
        {
            var t = new ProvisioningStatusTracker(
                sessionId: "sess-1",
                tenantId: "tenant-1",
                onEventCollected: e => _captured.Add(e),
                logger: TestLogger.Instance);
            t.EspFailureDetected += (_, ft) => _espFailures.Add(ft);
            t.DeviceSetupProvisioningComplete += (_, __) => System.Threading.Interlocked.Increment(ref _deviceSetupCompleteCount);
            return t;
        }

        private List<EnrollmentEvent> OfType(string eventType) =>
            _captured.ToList().Where(e => e.EventType == eventType).ToList();

        // =====================================================================
        // ProcessCategoryStatus — first-seen + resolved-to-success
        // =====================================================================

        [Fact]
        public void ProcessCategoryStatus_FirstSeen_EmitsStatusAndRawDump()
        {
            var t = CreateTracker();
            var json = @"{""categorySucceeded"":null,""CertificatesSubcategory"":{""subcategoryState"":""in_progress"",""subcategoryStatusText"":""working""}}";

            var result = t.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", json);

            Assert.False(result.IsFailed);
            Assert.NotEmpty(OfType(Constants.EventTypes.EspProvisioningStatus));
            Assert.NotEmpty(OfType("esp_provisioning_raw"));
            var raw = OfType("esp_provisioning_raw").Single();
            Assert.Equal("first_seen", raw.Data["trigger"]);
        }

        [Fact]
        public void ProcessCategoryStatus_ResolvesToSuccess_EmitsResolvedEventAndRaw()
        {
            var t = CreateTracker();
            var initial = @"{""categorySucceeded"":null,""CertificatesSubcategory"":{""subcategoryState"":""in_progress"",""subcategoryStatusText"":""...""}}";
            var resolved = @"{""categorySucceeded"":true,""CertificatesSubcategory"":{""subcategoryState"":""succeeded"",""subcategoryStatusText"":""done""}}";

            t.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", initial);
            t.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", resolved);

            var raws = OfType("esp_provisioning_raw");
            Assert.Equal(2, raws.Count);
            Assert.Contains(raws, r => (string)r.Data["trigger"] == "first_seen");
            Assert.Contains(raws, r => (string)r.Data["trigger"] == "category_resolved_success");
        }

        [Fact]
        public void ProcessCategoryStatus_ResolvesToFailure_FiresEspFailureDetected()
        {
            var t = CreateTracker();
            var failed = @"{""categorySucceeded"":false,""CertificatesSubcategory"":{""subcategoryState"":""failed"",""subcategoryStatusText"":""err""}}";

            var result = t.ProcessCategoryStatusForTest("AccountSetupCategory.Status", failed);

            Assert.True(result.IsFailed);
            Assert.Contains("Provisioning_AccountSetup_Certificates_Failed", result.FailureType);
        }

        [Fact]
        public void ProcessCategoryStatus_SubcategoryFailureBeforeCategorySucceededResolves_AlsoEscalates()
        {
            // Timeout scenario: categorySucceeded still null, but a subcategory already failed.
            var t = CreateTracker();
            var partialFailure = @"{""CertificatesSubcategory"":{""subcategoryState"":""failed"",""subcategoryStatusText"":""err""}}";

            var result = t.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", partialFailure);

            Assert.True(result.IsFailed);
            Assert.Equal("Provisioning_DeviceSetup_Certificates_Failed", result.FailureType);
        }

        [Fact]
        public void ProcessCategoryStatus_SameFailureTwice_FireOnceGuard()
        {
            var t = CreateTracker();
            var failed = @"{""categorySucceeded"":false,""AppsSubcategory"":{""subcategoryState"":""failed"",""subcategoryStatusText"":""err""}}";
            var failed2 = @"{""categorySucceeded"":false,""AppsSubcategory"":{""subcategoryState"":""failed"",""subcategoryStatusText"":""err - refresh""}}";

            t.ProcessCategoryStatusForTest("AccountSetupCategory.Status", failed);
            var secondResult = t.ProcessCategoryStatusForTest("AccountSetupCategory.Status", failed2);

            Assert.False(secondResult.IsFailed); // Fire-once guard in TryFireProvisioningFailure
        }

        // =====================================================================
        // Flat (string) subcategory format — legacy Windows versions
        // =====================================================================

        [Fact]
        public void ParseSubcategories_FlatStringFormat_InferredFromText()
        {
            var json = @"{""CertificatesSubcategory"":""Certificates (1 of 1 applied)""}";
            using var doc = JsonDocument.Parse(json);
            var subs = ProvisioningStatusTracker.ParseSubcategories(doc.RootElement);

            Assert.Single(subs);
            Assert.Equal("Certificates", subs[0].Name);
            Assert.Equal("succeeded", subs[0].State);
        }

        [Fact]
        public void ParseSubcategories_NestedObjectFormat_UsesExplicitState()
        {
            var json = @"{""AppsSubcategory"":{""subcategoryState"":""in_progress"",""subcategoryStatusText"":""2 of 5""}}";
            using var doc = JsonDocument.Parse(json);
            var subs = ProvisioningStatusTracker.ParseSubcategories(doc.RootElement);

            Assert.Single(subs);
            Assert.Equal("Apps", subs[0].Name);
            Assert.Equal("in_progress", subs[0].State);
            Assert.Equal("2 of 5", subs[0].StatusText);
        }

        [Fact]
        public void ParseSubcategories_DottedName_CleanedToSuffix()
        {
            var json = @"{""AccountSetup.CertificatesSubcategory"":{""subcategoryState"":""succeeded"",""subcategoryStatusText"":""done""}}";
            using var doc = JsonDocument.Parse(json);
            var subs = ProvisioningStatusTracker.ParseSubcategories(doc.RootElement);

            Assert.Single(subs);
            Assert.Equal("Certificates", subs[0].Name);
        }

        [Fact]
        public void ParseSubcategories_IgnoresNonSubcategoryProperties()
        {
            var json = @"{""categorySucceeded"":true,""CertificatesSubcategory"":""done""}";
            using var doc = JsonDocument.Parse(json);
            var subs = ProvisioningStatusTracker.ParseSubcategories(doc.RootElement);

            Assert.Single(subs);
            Assert.Equal("Certificates", subs[0].Name);
        }

        // =====================================================================
        // State inference from flat text
        // =====================================================================

        [Theory]
        [InlineData("Complete", "succeeded")]
        [InlineData("Certificates (1 of 1 applied)", "succeeded")]
        [InlineData("Apps installed", "succeeded")]
        [InlineData("No setup needed", "succeeded")]
        [InlineData("Error during install", "failed")]
        [InlineData("Registration Failed", "failed")]
        [InlineData("Working on it", "in_progress")]
        [InlineData("", "unknown")]
        public void InferStateFromText_RecognizesCommonPatterns(string text, string expected)
        {
            Assert.Equal(expected, ProvisioningStatusTracker.InferStateFromText(text));
        }

        // =====================================================================
        // SafeGetBool / SafeGetString
        // =====================================================================

        [Fact]
        public void SafeGetBool_TrueFalseNullStringUnknown()
        {
            using var doc = JsonDocument.Parse(@"{""t"":true,""f"":false,""s"":""true"",""n"":null,""num"":42}");
            var root = doc.RootElement;
            Assert.True(ProvisioningStatusTracker.SafeGetBool(root, "t"));
            Assert.False(ProvisioningStatusTracker.SafeGetBool(root, "f"));
            Assert.True(ProvisioningStatusTracker.SafeGetBool(root, "s"));
            Assert.Null(ProvisioningStatusTracker.SafeGetBool(root, "n"));
            Assert.Null(ProvisioningStatusTracker.SafeGetBool(root, "num"));
            Assert.Null(ProvisioningStatusTracker.SafeGetBool(root, "missing"));
        }

        [Fact]
        public void SafeGetString_ReturnsStringOrNull()
        {
            using var doc = JsonDocument.Parse(@"{""s"":""hello"",""num"":1,""missing_key"":null}");
            var root = doc.RootElement;
            Assert.Equal("hello", ProvisioningStatusTracker.SafeGetString(root, "s"));
            Assert.Null(ProvisioningStatusTracker.SafeGetString(root, "num"));
            Assert.Null(ProvisioningStatusTracker.SafeGetString(root, "missing_key"));
            Assert.Null(ProvisioningStatusTracker.SafeGetString(root, "totally_absent"));
        }

        // =====================================================================
        // BuildProgressSummary
        // =====================================================================

        [Fact]
        public void BuildProgressSummary_WithNoSubs_ReturnsInProgress()
        {
            Assert.Equal("In progress",
                ProvisioningStatusTracker.BuildProgressSummary(new List<ProvisioningStatusTracker.SubcategoryInfo>()));
        }

        [Fact]
        public void BuildProgressSummary_WithFailures_ReportsFailures()
        {
            var subs = new List<ProvisioningStatusTracker.SubcategoryInfo>
            {
                new ProvisioningStatusTracker.SubcategoryInfo { Name = "A", State = "succeeded" },
                new ProvisioningStatusTracker.SubcategoryInfo { Name = "B", State = "failed" },
                new ProvisioningStatusTracker.SubcategoryInfo { Name = "C", State = "in_progress" },
            };

            Assert.Equal("1 of 3 subcategories failed",
                ProvisioningStatusTracker.BuildProgressSummary(subs));
        }

        [Fact]
        public void BuildProgressSummary_NoFailures_ReportsCompletion()
        {
            var subs = new List<ProvisioningStatusTracker.SubcategoryInfo>
            {
                new ProvisioningStatusTracker.SubcategoryInfo { Name = "A", State = "succeeded" },
                new ProvisioningStatusTracker.SubcategoryInfo { Name = "B", State = "notRequired" },
                new ProvisioningStatusTracker.SubcategoryInfo { Name = "C", State = "in_progress" },
            };

            Assert.Equal("2 of 3 subcategories completed",
                ProvisioningStatusTracker.BuildProgressSummary(subs));
        }

        // =====================================================================
        // Snapshots + state properties
        // =====================================================================

        [Fact]
        public void GetProvisioningSnapshot_BeforeAnyData_ReturnsNull()
        {
            var t = CreateTracker();
            Assert.Null(t.GetProvisioningSnapshot());
        }

        [Fact]
        public void GetProvisioningSnapshot_AfterSuccess_ReflectsResolved()
        {
            var t = CreateTracker();
            var succeeded = @"{""categorySucceeded"":true,""CertificatesSubcategory"":{""subcategoryState"":""succeeded"",""subcategoryStatusText"":""done""}}";
            t.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", succeeded);

            var snap = t.GetProvisioningSnapshot();
            Assert.NotNull(snap);
            Assert.Equal("success", snap.CategoryOutcomes["DeviceSetup"]);
            Assert.Equal(1, snap.CategoriesSeen);
            Assert.Equal(1, snap.CategoriesResolved);
            Assert.True(snap.AllResolved);
        }

        [Fact]
        public void GetProvisioningCategorySnapshot_ReflectsCategorySucceededMap()
        {
            var t = CreateTracker();
            var succeeded = @"{""categorySucceeded"":true,""X"":{""subcategoryState"":""succeeded"",""subcategoryStatusText"":""""}}";
            var inProgress = @"{""categorySucceeded"":null,""Y"":{""subcategoryState"":""in_progress"",""subcategoryStatusText"":""""}}";
            t.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", succeeded);
            t.ProcessCategoryStatusForTest("AccountSetupCategory.Status", inProgress);

            var snap = t.GetProvisioningCategorySnapshot();
            Assert.Equal(true, snap["DeviceSetupCategory.Status"]);
            Assert.Null(snap["AccountSetupCategory.Status"]);
        }

        [Fact]
        public void DeviceSetupCategorySucceeded_ReflectsResolvedSuccess()
        {
            var t = CreateTracker();
            Assert.False(t.DeviceSetupCategorySucceeded);

            t.ProcessCategoryStatusForTest(
                "DeviceSetupCategory.Status",
                @"{""categorySucceeded"":true,""S"":{""subcategoryState"":""succeeded"",""subcategoryStatusText"":""""}}");

            Assert.True(t.DeviceSetupCategorySucceeded);
        }

        [Fact]
        public void HasAccountSetupActivity_TrueAfterAccountSetupSeen()
        {
            var t = CreateTracker();
            Assert.False(t.HasAccountSetupActivity);

            t.ProcessCategoryStatusForTest(
                "AccountSetupCategory.Status",
                @"{""AppsSubcategory"":{""subcategoryState"":""in_progress"",""subcategoryStatusText"":""""}}");

            Assert.True(t.HasAccountSetupActivity);
        }

        [Fact]
        public void FindFailedSubcategory_ReturnsFirstFailedName()
        {
            var subs = new List<ProvisioningStatusTracker.SubcategoryInfo>
            {
                new ProvisioningStatusTracker.SubcategoryInfo { Name = "A", State = "succeeded" },
                new ProvisioningStatusTracker.SubcategoryInfo { Name = "B", State = "failed" },
                new ProvisioningStatusTracker.SubcategoryInfo { Name = "C", State = "failed" },
            };
            Assert.Equal("B", ProvisioningStatusTracker.FindFailedSubcategory(subs));

            var none = new List<ProvisioningStatusTracker.SubcategoryInfo>
            {
                new ProvisioningStatusTracker.SubcategoryInfo { Name = "A", State = "succeeded" },
            };
            Assert.Null(ProvisioningStatusTracker.FindFailedSubcategory(none));
        }

        // =====================================================================
        // Subcategory transitions (noise filtering)
        // =====================================================================

        [Fact]
        public void ProcessCategoryStatus_NotStartedToInProgress_IsNoise_NoTransitionEventEmitted()
        {
            var t = CreateTracker();
            var initial = @"{""AppsSubcategory"":{""subcategoryState"":""notStarted"",""subcategoryStatusText"":""""}}";
            var progress = @"{""AppsSubcategory"":{""subcategoryState"":""in_progress"",""subcategoryStatusText"":""go""}}";

            t.ProcessCategoryStatusForTest("AccountSetupCategory.Status", initial);
            t.ProcessCategoryStatusForTest("AccountSetupCategory.Status", progress);

            // Progress event still emitted (JSON changed) but NO event should carry a
            // subcategory_state_change transition (notStarted → in_progress is filtered as noise).
            var all = OfType(Constants.EventTypes.EspProvisioningStatus);
            Assert.DoesNotContain(all, e => e.Data.ContainsKey("transitions"));
        }

        [Fact]
        public void ProcessCategoryStatus_InProgressToSucceeded_EmitsTransitionEvent()
        {
            var t = CreateTracker();
            var initial = @"{""AppsSubcategory"":{""subcategoryState"":""in_progress"",""subcategoryStatusText"":""""}}";
            var done = @"{""AppsSubcategory"":{""subcategoryState"":""succeeded"",""subcategoryStatusText"":""""}}";

            t.ProcessCategoryStatusForTest("AccountSetupCategory.Status", initial);
            t.ProcessCategoryStatusForTest("AccountSetupCategory.Status", done);

            // At least one of the emitted status events must carry the transition payload.
            var withTransitions = OfType(Constants.EventTypes.EspProvisioningStatus)
                .Where(e => e.Data.ContainsKey("transitions"))
                .ToList();
            Assert.Single(withTransitions);
            Assert.Equal("subcategory_state_change", withTransitions[0].Data["changeType"]);
        }
    }
}
