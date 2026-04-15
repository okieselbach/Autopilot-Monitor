using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.Core.Tests.Helpers;
using Xunit;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;

namespace AutopilotMonitor.Agent.Core.Tests.Tracking
{
    /// <summary>
    /// Tests state serialization round-trip including all fields.
    /// Prevents: state loss after crash (IsHybridJoin, signal timestamps, SignalsSeen),
    /// crashes on missing/corrupt state files.
    /// </summary>
    public class EnrollmentStatePersistenceTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly EnrollmentStatePersistence _persistence;

        public EnrollmentStatePersistenceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "autopilot-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _persistence = new EnrollmentStatePersistence(_tempDir, TestLogger.Instance);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch { }
        }

        [Fact]
        public void SaveAndLoad_RoundTrip_PreservesAllBooleans()
        {
            var state = new EnrollmentStateData
            {
                EspEverSeen = true,
                EspFinalExitSeen = true,
                DesktopArrived = true,
                IsWaitingForHello = true,
                EnrollmentCompleteEmitted = false,
                AadJoinedWithUser = true,
                IsHybridJoin = true,
            };

            _persistence.Save(state);
            var loaded = _persistence.Load();

            Assert.NotNull(loaded);
            Assert.True(loaded.EspEverSeen);
            Assert.True(loaded.EspFinalExitSeen);
            Assert.True(loaded.DesktopArrived);
            Assert.True(loaded.IsWaitingForHello);
            Assert.False(loaded.EnrollmentCompleteEmitted);
            Assert.True(loaded.AadJoinedWithUser);
            Assert.True(loaded.IsHybridJoin);
        }

        [Fact]
        public void SaveAndLoad_IsHybridJoin_Preserved()
        {
            var state = new EnrollmentStateData { IsHybridJoin = true };

            _persistence.Save(state);
            var loaded = _persistence.Load();

            Assert.True(loaded.IsHybridJoin);
        }

        [Fact]
        public void SaveAndLoad_AllTimestamps_Preserved()
        {
            var now = DateTime.UtcNow;
            var state = new EnrollmentStateData
            {
                EspFirstSeenUtc = now.AddMinutes(-10),
                EspFinalExitUtc = now.AddMinutes(-5),
                DesktopArrivedUtc = now.AddMinutes(-3),
                HelloResolvedUtc = now.AddMinutes(-2),
                ImePatternSeenUtc = now.AddMinutes(-1),
                DeviceSetupProvisioningCompleteUtc = now,
            };

            _persistence.Save(state);
            var loaded = _persistence.Load();

            Assert.NotNull(loaded);
            Assert.NotNull(loaded.EspFirstSeenUtc);
            Assert.NotNull(loaded.EspFinalExitUtc);
            Assert.NotNull(loaded.DesktopArrivedUtc);
            Assert.NotNull(loaded.HelloResolvedUtc);
            Assert.NotNull(loaded.ImePatternSeenUtc);
            Assert.NotNull(loaded.DeviceSetupProvisioningCompleteUtc);
            // Allow 1 second tolerance for serialization round-trip
            Assert.InRange(loaded.EspFirstSeenUtc.Value, now.AddMinutes(-10).AddSeconds(-1), now.AddMinutes(-10).AddSeconds(1));
        }

        [Fact]
        public void SaveAndLoad_SignalsSeen_Preserved()
        {
            var state = new EnrollmentStateData
            {
                SignalsSeen = new List<string>
                {
                    "esp_first_seen",
                    "esp_final_exit",
                    "desktop_arrived",
                    "hello_completed",
                    "esp_resumed"
                }
            };

            _persistence.Save(state);
            var loaded = _persistence.Load();

            Assert.NotNull(loaded);
            Assert.Equal(5, loaded.SignalsSeen.Count);
            Assert.Contains("esp_resumed", loaded.SignalsSeen);
        }

        [Fact]
        public void SaveAndLoad_EnrollmentConfig_Preserved()
        {
            var state = new EnrollmentStateData
            {
                LastEspPhase = "AccountSetup",
                EnrollmentType = "v1",
                SkipUserStatusPage = true,
                SkipDeviceStatusPage = false,
                AutopilotMode = 1,
            };

            _persistence.Save(state);
            var loaded = _persistence.Load();

            Assert.Equal("AccountSetup", loaded.LastEspPhase);
            Assert.Equal("v1", loaded.EnrollmentType);
            Assert.True(loaded.SkipUserStatusPage);
            Assert.False(loaded.SkipDeviceStatusPage);
            Assert.Equal(1, loaded.AutopilotMode);
        }

        [Fact]
        public void Load_MissingFile_ReturnsNull()
        {
            var tempDir2 = Path.Combine(Path.GetTempPath(), "autopilot-test-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir2);
            try
            {
                var persistence = new EnrollmentStatePersistence(tempDir2, TestLogger.Instance);
                var loaded = persistence.Load();

                Assert.Null(loaded);
            }
            finally
            {
                Directory.Delete(tempDir2, true);
            }
        }

        [Fact]
        public void Load_CorruptJson_ReturnsNull()
        {
            // Write corrupt JSON directly
            File.WriteAllText(Path.Combine(_tempDir, "enrollment-state.json"), "{ corrupt json !!!");

            var loaded = _persistence.Load();

            Assert.Null(loaded);
        }

        [Fact]
        public void Load_EmptyFile_ReturnsNull()
        {
            File.WriteAllText(Path.Combine(_tempDir, "enrollment-state.json"), "");

            var loaded = _persistence.Load();

            Assert.Null(loaded);
        }

        [Fact]
        public void Delete_RemovesFile()
        {
            var state = new EnrollmentStateData { EspEverSeen = true };
            _persistence.Save(state);
            Assert.True(File.Exists(_persistence.StateFilePath));

            _persistence.Delete();

            Assert.False(File.Exists(_persistence.StateFilePath));
        }

        [Fact]
        public void Delete_NoFile_DoesNotThrow()
        {
            // Should not throw when no file exists
            _persistence.Delete();
        }

        [Fact]
        public void Save_CreatesDirectory()
        {
            var nestedDir = Path.Combine(_tempDir, "nested", "subdir");
            var persistence = new EnrollmentStatePersistence(nestedDir, TestLogger.Instance);

            persistence.Save(new EnrollmentStateData { EspEverSeen = true });

            Assert.True(File.Exists(persistence.StateFilePath));
        }
    }
}
