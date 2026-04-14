using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// The enrollment flow a device is going through.
    /// Wire format (HTTP, Table Storage) stays "v1"/"v2" for backward compatibility —
    /// use <see cref="EnrollmentTypeExtensions"/> to convert.
    /// </summary>
    public enum EnrollmentType
    {
        Unknown = 0,
        Classic = 1,            // Windows Autopilot (v1): ESP-driven OOBE
        DevicePreparation = 2   // Windows Autopilot Device Preparation (v2, WDP): no ESP
    }

    public static class EnrollmentTypeExtensions
    {
        public const string WireClassic = "v1";
        public const string WireDevicePreparation = "v2";

        /// <summary>
        /// Returns the wire format string ("v1"/"v2") used in persisted data and HTTP payloads.
        /// Unknown returns "v1" to preserve legacy default behavior for sessions that predate typing.
        /// </summary>
        public static string ToWireFormat(this EnrollmentType value) => value switch
        {
            EnrollmentType.DevicePreparation => WireDevicePreparation,
            EnrollmentType.Classic => WireClassic,
            _ => WireClassic
        };

        /// <summary>
        /// Parses the wire format string into an <see cref="EnrollmentType"/>.
        /// Null/empty/unrecognized values return <see cref="EnrollmentType.Unknown"/>.
        /// </summary>
        public static EnrollmentType FromWireFormat(string? wireValue)
        {
            if (string.IsNullOrWhiteSpace(wireValue))
                return EnrollmentType.Unknown;

            if (string.Equals(wireValue, WireClassic, StringComparison.OrdinalIgnoreCase))
                return EnrollmentType.Classic;

            if (string.Equals(wireValue, WireDevicePreparation, StringComparison.OrdinalIgnoreCase))
                return EnrollmentType.DevicePreparation;

            return EnrollmentType.Unknown;
        }
    }
}
