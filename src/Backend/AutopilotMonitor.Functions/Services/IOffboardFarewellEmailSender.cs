namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Sends the post-completion "sorry to see you go" farewell email to the tenant's
/// Preview-Notification-Email captured at Phase 1 of tenant offboarding.
/// <para>
/// Invocation point: <c>TenantOffboardingHandler.RunPostDrainPhasesAsync</c> immediately
/// after the History terminal write (Side-effect 6). Always fail-soft — implementations
/// must NOT throw; the offboarding correctness contract does not depend on email delivery.
/// </para>
/// <para>
/// The send path is gated by a hard-coded boolean inside the implementation
/// (<c>ResendEmailService.OffboardFarewellEmailArmed</c>). Flipping that constant to
/// <c>true</c> is the explicit "arm" action — disarmed by design until the final
/// template + feedback form are signed off.
/// </para>
/// </summary>
public interface IOffboardFarewellEmailSender
{
    Task SendAsync(string toEmail, string domainName, string tenantId, CancellationToken ct = default);
}
