using AutopilotMonitor.Agent.Core.Monitoring.Telemetry.DeviceInfo;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Telemetry.DeviceInfo;

/// <summary>
/// V1 mirror of DeviceInfoProviderVmDetectionTests — V1 is still the active ingest path
/// until V2 cutover, so the VM detection logic must stay in lock-step. See V2 sibling file
/// for the rationale (conservative-bias false negatives over false positives).
/// </summary>
public class DeviceInfoProviderVmDetectionTests
{
    [Fact] public void HyperV_VM_DetectedByModel() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("Microsoft Corporation", "Virtual Machine"));

    [Fact] public void HyperV_VM_CaseInsensitive() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("microsoft corporation", "virtual machine"));

    [Fact] public void SurfaceLaptop_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("Microsoft Corporation", "Surface Laptop 5"));

    [Fact] public void SurfacePro_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("Microsoft Corporation", "Surface Pro 9"));

    [Fact] public void SurfaceBook_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("Microsoft Corporation", "Surface Book 3"));

    [Fact] public void VMware_DetectedByManufacturer() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("VMware, Inc.", "VMware Virtual Platform"));

    [Fact] public void VMware_DetectedByModel_Defensive() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("Other Vendor", "VMware Virtual Platform"));

    [Fact] public void VirtualBox_DetectedByManufacturer() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("innotek GmbH", "VirtualBox"));

    [Fact] public void VirtualBox_DetectedByModel_Defensive() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("Other Vendor", "VirtualBox"));

    [Fact] public void Xen_Detected() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("Xen", "HVM domU"));

    [Fact] public void QEMU_Detected() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("QEMU", "Standard PC"));

    [Fact] public void Parallels_Detected() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("Parallels Software International Inc.", "Parallels Virtual Platform"));

    [Fact] public void RedHat_KVM_Detected() =>
        Assert.True(DeviceInfoProvider.IsVirtualMachine("Red Hat", "KVM"));

    [Fact] public void Lenovo_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("LENOVO", "20XW00JEMZ"));

    [Fact] public void Dell_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("Dell Inc.", "Latitude 7430"));

    [Fact] public void HP_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("HP", "EliteBook 840 G9"));

    [Fact] public void NullManufacturerAndModel_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine(null!, null!));

    [Fact] public void EmptyManufacturerAndModel_NotVM() =>
        Assert.False(DeviceInfoProvider.IsVirtualMachine("", ""));
}
