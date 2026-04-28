using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.DeviceInfo;

/// <summary>
/// Pins the VM-detection allowlist used by the rule engine's preconditions feature
/// (see ANALYZE-SEC-001 "skip on virtual machines"). Conservative-bias: false negatives
/// (a misclassified physical box → rule still fires) are preferred over false positives
/// (a misclassified VM → rule silently skipped, user-visible regression).
/// </summary>
public class DeviceInfoProviderVmDetectionTests
{
    // ===== Hyper-V =====
    // Hyper-V VMs (incl. Cloud PC, Azure Virtual Desktop) report manufacturer
    // "Microsoft Corporation" + model "Virtual Machine". The model string is what
    // disambiguates them from physical Surface devices that share the manufacturer.
    [Fact] public void HyperV_VM_DetectedByModel() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("Microsoft Corporation", "Virtual Machine"));

    [Fact] public void HyperV_VM_CaseInsensitive() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("microsoft corporation", "virtual machine"));

    // ===== Surface (the tricky physical case sharing manufacturer with Hyper-V) =====
    [Fact] public void SurfaceLaptop_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("Microsoft Corporation", "Surface Laptop 5"));

    [Fact] public void SurfacePro_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("Microsoft Corporation", "Surface Pro 9"));

    [Fact] public void SurfaceBook_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("Microsoft Corporation", "Surface Book 3"));

    // ===== VMware =====
    [Fact] public void VMware_DetectedByManufacturer() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("VMware, Inc.", "VMware Virtual Platform"));

    [Fact] public void VMware_DetectedByModel_Defensive() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("Other Vendor", "VMware Virtual Platform"));

    // ===== VirtualBox =====
    [Fact] public void VirtualBox_DetectedByManufacturer() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("innotek GmbH", "VirtualBox"));

    [Fact] public void VirtualBox_DetectedByModel_Defensive() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("Other Vendor", "VirtualBox"));

    // ===== Other hypervisors =====
    [Fact] public void Xen_Detected() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("Xen", "HVM domU"));

    [Fact] public void QEMU_Detected() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("QEMU", "Standard PC"));

    [Fact] public void Parallels_Detected() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("Parallels Software International Inc.", "Parallels Virtual Platform"));

    [Fact] public void RedHat_KVM_Detected() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("Red Hat", "KVM"));

    // ===== Common physical OEMs (must stay false) =====
    [Fact] public void Lenovo_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("LENOVO", "20XW00JEMZ"));

    [Fact] public void Dell_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("Dell Inc.", "Latitude 7430"));

    [Fact] public void HP_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("HP", "EliteBook 840 G9"));

    // ===== Edge cases =====
    [Fact] public void NullManufacturerAndModel_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine(null!, null!));

    [Fact] public void EmptyManufacturerAndModel_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("", ""));
}
