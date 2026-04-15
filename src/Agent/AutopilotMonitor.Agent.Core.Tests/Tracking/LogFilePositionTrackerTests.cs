using Xunit;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;

namespace AutopilotMonitor.Agent.Core.Tests.Tracking
{
    /// <summary>
    /// Tests incremental log file reading: position tracking, rotation detection, and crash recovery.
    /// Prevents: missed log entries after rotation, duplicate event emission after restart.
    /// </summary>
    public class LogFilePositionTrackerTests
    {
        [Fact]
        public void GetSafePosition_NewFile_ReturnsZero()
        {
            var tracker = new LogFilePositionTracker();

            var pos = tracker.GetSafePosition(@"C:\logs\test.log", currentFileSize: 5000);

            Assert.Equal(0, pos);
        }

        [Fact]
        public void GetSafePosition_NormalGrowth_ReturnsSavedPosition()
        {
            var tracker = new LogFilePositionTracker();
            tracker.SetPosition(@"C:\logs\test.log", 1000);

            var pos = tracker.GetSafePosition(@"C:\logs\test.log", currentFileSize: 2000);

            Assert.Equal(1000, pos);
        }

        [Fact]
        public void GetSafePosition_FileRotation_ResetsToZero()
        {
            // File shrunk (rotation) — must restart from beginning
            var tracker = new LogFilePositionTracker();
            tracker.SetPosition(@"C:\logs\test.log", 5000);

            var pos = tracker.GetSafePosition(@"C:\logs\test.log", currentFileSize: 500);

            Assert.Equal(0, pos);
        }

        [Fact]
        public void GetPosition_UnknownFile_ReturnsZero()
        {
            var tracker = new LogFilePositionTracker();

            var pos = tracker.GetPosition(@"C:\logs\unknown.log");

            Assert.Equal(0, pos);
        }

        [Fact]
        public void SetPosition_ThenGetPosition_ReturnsCorrectValue()
        {
            var tracker = new LogFilePositionTracker();
            tracker.SetPosition(@"C:\logs\test.log", 42);

            Assert.Equal(42, tracker.GetPosition(@"C:\logs\test.log"));
        }

        [Fact]
        public void RestorePosition_SetsCorrectState()
        {
            var tracker = new LogFilePositionTracker();
            tracker.RestorePosition(@"C:\logs\test.log", 3000, 5000);

            // Position restored — should return saved position when file is larger
            var pos = tracker.GetSafePosition(@"C:\logs\test.log", currentFileSize: 6000);
            Assert.Equal(3000, pos);
        }

        [Fact]
        public void RestorePosition_ThenRotation_ResetsToZero()
        {
            var tracker = new LogFilePositionTracker();
            tracker.RestorePosition(@"C:\logs\test.log", 3000, 5000);

            // File rotated — smaller than restored position
            var pos = tracker.GetSafePosition(@"C:\logs\test.log", currentFileSize: 100);
            Assert.Equal(0, pos);
        }

        [Fact]
        public void GetAllPositions_ReturnsDefensiveCopy()
        {
            var tracker = new LogFilePositionTracker();
            tracker.SetPosition(@"C:\logs\a.log", 100);
            tracker.SetPosition(@"C:\logs\b.log", 200);

            var positions = tracker.GetAllPositions();

            Assert.Equal(2, positions.Count);
            // Modifying returned dict should not affect tracker
            positions.Clear();
            Assert.Equal(100, tracker.GetPosition(@"C:\logs\a.log"));
        }

        [Fact]
        public void CaseInsensitive_SameFileDifferentCase()
        {
            var tracker = new LogFilePositionTracker();
            tracker.SetPosition(@"C:\Logs\Test.log", 500);

            var pos = tracker.GetPosition(@"C:\logs\test.log");

            Assert.Equal(500, pos);
        }

        [Fact]
        public void MultipleFiles_IndependentTracking()
        {
            var tracker = new LogFilePositionTracker();
            tracker.SetPosition(@"C:\logs\a.log", 100);
            tracker.SetPosition(@"C:\logs\b.log", 200);

            Assert.Equal(100, tracker.GetPosition(@"C:\logs\a.log"));
            Assert.Equal(200, tracker.GetPosition(@"C:\logs\b.log"));
        }
    }
}
