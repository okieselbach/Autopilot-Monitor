using AutopilotMonitor.Functions.Functions.Admin;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Guards against redirect URI mismatches in the admin consent flow.
/// A mismatch causes Azure AD to reject the consent with AADSTS50011,
/// silently breaking tenant onboarding with no backend-visible error.
///
/// ROOT CAUSE (2026-03-23 → 2026-04-02, 10 days undetected):
/// Route renamed from /settings/validation/autopilot to /settings/tenant/autopilot
/// but the Entra ID app registration was not updated → every consent attempt failed.
///
/// This test ensures that ANY redirect URI the frontend might send is covered
/// in RegisteredConsentRedirectPaths, and conversely that the list stays clean.
/// </summary>
public class ConsentRedirectUriTests
{
    /// <summary>
    /// The consent flow in TenantConfigContext.tsx sends:
    ///   `${window.location.origin}/settings/tenant/autopilot`
    /// This path MUST be in the registered set.
    /// </summary>
    [Fact]
    public void FrontendConsentRedirectPath_IsRegistered()
    {
        // This is the path sent by TenantConfigContext.tsx line ~592
        const string frontendConsentPath = "/settings/tenant/autopilot";

        Assert.Contains(
            frontendConsentPath,
            AutopilotDeviceValidationConsentFunction.RegisteredConsentRedirectPaths);
    }

    /// <summary>
    /// The backend default fallback uses /settings when no redirectUri query param is provided.
    /// </summary>
    [Fact]
    public void DefaultFallbackRedirectPath_IsRegistered()
    {
        const string fallbackPath = "/settings";

        Assert.Contains(
            fallbackPath,
            AutopilotDeviceValidationConsentFunction.RegisteredConsentRedirectPaths);
    }

    /// <summary>
    /// Every registered path must start with / (absolute path, not relative).
    /// Prevents accidental registration of malformed paths.
    /// </summary>
    [Fact]
    public void AllRegisteredPaths_AreAbsolute()
    {
        foreach (var path in AutopilotDeviceValidationConsentFunction.RegisteredConsentRedirectPaths)
        {
            Assert.True(path.StartsWith("/"),
                $"Registered consent redirect path '{path}' must be an absolute path starting with '/'");
        }
    }

    /// <summary>
    /// Paths should be lowercase and trimmed — prevents case-sensitivity bugs
    /// where the path works in the HashSet but fails at Azure AD.
    /// </summary>
    [Fact]
    public void AllRegisteredPaths_AreClean()
    {
        foreach (var path in AutopilotDeviceValidationConsentFunction.RegisteredConsentRedirectPaths)
        {
            Assert.Equal(path.Trim(), path);
            Assert.DoesNotContain("//", path);
        }
    }
}
