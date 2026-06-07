#nullable enable
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Provisioning;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Telemetry;

/// <summary>
/// Unit tests for the pure aggregation in <see cref="ProvisioningPackageCollector"/>
/// (<c>BuildPayload</c> / <c>BuildContentIndicators</c>). The IO probes are environment
/// dependent and not exercised here; this locks the fact-shaping + classification rules.
/// </summary>
public sealed class ProvisioningPackageCollectorTests
{
    [Fact]
    public void BuildPayload_clean_device_reports_nothing_found()
    {
        var findings = new ProvisioningScanFindings();

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        Assert.False((bool)payload["anyPpkgFound"]);
        Assert.Equal(0, (int)payload["ppkgFileCount"]);
        Assert.Equal(0, (int)payload["packageCount"]);
        Assert.False((bool)payload["recoveryCustomizationsResidue"]);
        Assert.False((bool)payload["omadmAccountsPresent"]);
        Assert.Empty((List<string>)payload["scanErrors"]);

        var indicators = (Dictionary<string, object>)payload["contentIndicators"];
        Assert.False((bool)indicators["localAccounts"]);
        Assert.False((bool)indicators["certificates"]);
        Assert.False((bool)indicators["wifiProfiles"]);
        Assert.False((bool)indicators["vpnProfiles"]);
        Assert.False((bool)indicators["appManagement"]);
        Assert.False((bool)indicators["scripts"]);
        Assert.True(indicators.ContainsKey("note"));
    }

    [Fact]
    public void BuildPayload_ppkg_file_present_sets_anyPpkgFound()
    {
        var findings = new ProvisioningScanFindings();
        findings.Files.Add(new PpkgFileFact
        {
            Directory = @"C:\ProgramData\Microsoft\Provisioning",
            Name = "bulk.ppkg",
            FullPath = @"C:\ProgramData\Microsoft\Provisioning\bulk.ppkg",
            SizeBytes = 4096,
            LastWriteUtc = "2026-06-06T10:00:00.0000000Z",
        });

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        Assert.True((bool)payload["anyPpkgFound"]);
        Assert.Equal(1, (int)payload["ppkgFileCount"]);
        var files = (List<Dictionary<string, object>>)payload["ppkgFiles"];
        Assert.Single(files);
        Assert.Equal("bulk.ppkg", files[0]["name"]);
        Assert.Equal(4096L, files[0]["sizeBytes"]);
    }

    [Fact]
    public void BuildPayload_package_metadata_and_subkeys_projected()
    {
        var findings = new ProvisioningScanFindings();
        var pkg = new PpkgPackageFact
        {
            PackageId = "{abc}",
            Name = "Contoso Bulk",
            OwnerType = "ITPro",
            Rank = "100",
            InstallTime = "2026-06-06T09:00:00Z",
        };
        pkg.SubKeyNames.Add("WiFi");
        pkg.SubKeyNames.Add("Accounts");
        findings.Packages.Add(pkg);

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        Assert.True((bool)payload["anyPpkgFound"]);
        Assert.Equal(1, (int)payload["packageCount"]);
        var packages = (List<Dictionary<string, object>>)payload["packages"];
        var first = packages.Single();
        Assert.Equal("{abc}", first["packageId"]);
        Assert.Equal("Contoso Bulk", first["name"]);
        Assert.Equal("ITPro", first["ownerType"]);
        Assert.Equal("100", first["rank"]);
        var subKeys = (List<string>)first["registrySubKeys"];
        Assert.Contains("WiFi", subKeys);

        // Content indicators are derived from the package-scoped subkey names.
        var indicators = (Dictionary<string, object>)payload["contentIndicators"];
        Assert.True((bool)indicators["wifiProfiles"]);
        Assert.True((bool)indicators["localAccounts"]);
        Assert.False((bool)indicators["vpnProfiles"]);
    }

    [Fact]
    public void BuildPayload_omadm_alone_is_context_not_a_ppkg_signal()
    {
        // OMADM\Accounts exists on every MDM-enrolled device — must NOT flip anyPpkgFound.
        var findings = new ProvisioningScanFindings { OmadmAccountsPresent = true, DiagnosticsPresent = true };

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        Assert.False((bool)payload["anyPpkgFound"]);
        Assert.True((bool)payload["omadmAccountsPresent"]);
        Assert.True((bool)payload["provisioningDiagnosticsPresent"]);
    }

    [Fact]
    public void BuildPayload_recovery_residue_alone_sets_anyPpkgFound_and_emits_detected_event()
    {
        // Non-.ppkg residue in Recovery\Customizations is the gap case: it must still flip
        // anyPpkgFound AND produce a detected event so ANALYZE-SEC-005 fires on residue-only devices.
        var findings = new ProvisioningScanFindings { RecoveryCustomizationsDir = @"C:\Recovery\Customizations" };
        findings.RecoveryCustomizationsFiles.Add("setupcomplete.cmd");

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        Assert.True((bool)payload["anyPpkgFound"]);
        Assert.True((bool)payload["recoveryCustomizationsResidue"]);
        Assert.Contains("setupcomplete.cmd", (List<string>)payload["recoveryCustomizationsFiles"]);

        var detected = ProvisioningPackageCollector.BuildDetectedEvents(findings);
        var residue = Assert.Single(detected);
        Assert.Equal("recovery_residue", residue["source"]);
        Assert.Equal("setupcomplete.cmd", residue["identity"]);
    }

    [Fact]
    public void BuildDetectedEvents_skips_ppkg_residue_to_avoid_duplicate_with_file_event()
    {
        // A .ppkg in Recovery is captured BOTH as a .ppkg file AND as a residue name; it must
        // produce exactly one detected event (the file event), not two.
        var findings = new ProvisioningScanFindings { RecoveryCustomizationsDir = @"C:\Recovery\Customizations" };
        findings.Files.Add(new PpkgFileFact
        {
            Directory = @"C:\Recovery\Customizations",
            Name = "oem.ppkg",
            FullPath = @"C:\Recovery\Customizations\oem.ppkg",
        });
        findings.RecoveryCustomizationsFiles.Add("oem.ppkg");

        var detected = ProvisioningPackageCollector.BuildDetectedEvents(findings);

        var only = Assert.Single(detected);
        Assert.Equal("file", only["source"]);
    }

    [Fact]
    public void BuildDetectedEvents_emits_ppkg_residue_when_file_enumeration_missed_it()
    {
        // Gap case: recursive *.ppkg enumeration failed/truncated, so the .ppkg is NOT in
        // findings.Files. The residue pass must still emit a detected event (dedup is by captured
        // path, not by ".ppkg" extension), so anyPpkgFound stays consistent with a firing rule.
        var findings = new ProvisioningScanFindings { RecoveryCustomizationsDir = @"C:\Recovery\Customizations" };
        findings.RecoveryCustomizationsFiles.Add("uncaptured.ppkg");

        var detected = ProvisioningPackageCollector.BuildDetectedEvents(findings);

        var only = Assert.Single(detected);
        Assert.Equal("recovery_residue", only["source"]);
        Assert.Equal("uncaptured.ppkg", only["identity"]);
        Assert.True((bool)ProvisioningPackageCollector.BuildPayload(findings)["anyPpkgFound"]);
    }

    [Fact]
    public void AnyPpkgFound_is_consistent_with_detected_event_count()
    {
        // The invariant the Recovery-residue fix guarantees: anyPpkgFound <=> a detected event exists.
        var clean = new ProvisioningScanFindings { OmadmAccountsPresent = true, DiagnosticsPresent = true };
        Assert.False((bool)ProvisioningPackageCollector.BuildPayload(clean)["anyPpkgFound"]);
        Assert.Empty(ProvisioningPackageCollector.BuildDetectedEvents(clean));

        var residueOnly = new ProvisioningScanFindings { RecoveryCustomizationsDir = @"C:\Recovery\Customizations" };
        residueOnly.RecoveryCustomizationsFiles.Add("unattend.xml");
        Assert.True((bool)ProvisioningPackageCollector.BuildPayload(residueOnly)["anyPpkgFound"]);
        Assert.NotEmpty(ProvisioningPackageCollector.BuildDetectedEvents(residueOnly));
    }

    [Fact]
    public void BuildRecoveryResidueEventData_projects_scalar_fields_with_identity()
    {
        var data = ProvisioningPackageCollector.BuildRecoveryResidueEventData("unattend.xml", @"C:\Recovery\Customizations");

        Assert.Equal("recovery_residue", data["source"]);
        Assert.Equal("unattend.xml", data["fileName"]);
        Assert.Equal("unattend.xml", data["identity"]);
        Assert.Equal(@"C:\Recovery\Customizations", data["dir"]);
    }

    [Fact]
    public void BuildPayload_surfaces_scan_errors_fail_soft()
    {
        var findings = new ProvisioningScanFindings();
        findings.Errors.Add("registry:UnauthorizedAccessException: denied");

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        var errors = (List<string>)payload["scanErrors"];
        Assert.Single(errors);
        Assert.Contains("denied", errors[0]);
    }

    [Fact]
    public void BuildIdentity_joins_non_empty_distinct_fields()
    {
        var identity = ProvisioningPackageCollector.BuildIdentity(
            name: "Dell Recovery", fileName: "dell_recovery.ppkg", ownerType: "OEM", packageId: "{guid}");

        Assert.Equal("Dell Recovery | dell_recovery.ppkg | OEM | {guid}", identity);
    }

    [Fact]
    public void BuildIdentity_skips_empty_and_dedups_case_insensitive()
    {
        var identity = ProvisioningPackageCollector.BuildIdentity(
            name: "bulk", fileName: "bulk", ownerType: null, packageId: "  ");

        Assert.Equal("bulk", identity);
    }

    [Fact]
    public void BuildPackageEventData_projects_scalar_fields_with_identity()
    {
        var data = ProvisioningPackageCollector.BuildPackageEventData(new PpkgPackageFact
        {
            PackageId = "{abc}",
            Name = "Contoso Bulk",
            OwnerType = "ITPro",
        });

        Assert.Equal("registry", data["source"]);
        Assert.Equal("{abc}", data["packageId"]);
        Assert.Equal("Contoso Bulk", data["packageName"]);
        Assert.Equal("Contoso Bulk | ITPro | {abc}", data["identity"]);
    }

    [Fact]
    public void BuildFileEventData_projects_scalar_fields_with_identity()
    {
        var data = ProvisioningPackageCollector.BuildFileEventData(new PpkgFileFact
        {
            Directory = @"C:\Recovery\Customizations",
            Name = "oem.ppkg",
            FullPath = @"C:\Recovery\Customizations\oem.ppkg",
            SizeBytes = 123,
        });

        Assert.Equal("file", data["source"]);
        Assert.Equal("oem.ppkg", data["fileName"]);
        Assert.Equal("oem.ppkg", data["identity"]);
    }

    [Theory]
    [InlineData("EnterpriseDesktopAppManagement", "appManagement")]
    [InlineData("RootCATrustedCertificates", "certificates")]
    [InlineData("ClientCertificateInstall", "certificates")]
    [InlineData("VPNv2", "vpnProfiles")]
    [InlineData("WLAN", "wifiProfiles")]
    [InlineData("ProvisioningCommands", "scripts")]
    public void BuildContentIndicators_maps_known_csp_markers(string subKey, string expectedIndicator)
    {
        var findings = new ProvisioningScanFindings();
        var pkg = new PpkgPackageFact { PackageId = "{x}" };
        pkg.SubKeyNames.Add(subKey);
        findings.Packages.Add(pkg);

        var indicators = ProvisioningPackageCollector.BuildContentIndicators(findings);

        Assert.True((bool)indicators[expectedIndicator]);
    }
}
