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
    /// Tests CMTrace log format parsing used for IME log files.
    /// Prevents: timestamp parsing failures on single-digit months, fractional second overflow,
    /// crashes on non-CMTrace lines.
    /// </summary>
    public class CmTraceLogParserTests
    {
        [Fact]
        public void TryParseLine_ValidCmTrace_ParsesAllFields()
        {
            var line = "<![LOG[Starting app installation for user]LOG]!>" +
                       "<time=\"14:30:45.1234567\" date=\"3-15-2026\" " +
                       "component=\"IntuneManagementExtension\" context=\"\" " +
                       "type=\"1\" thread=\"1234\" file=\"\">";

            var result = CmTraceLogParser.TryParseLine(line, out var entry);

            Assert.True(result);
            Assert.Equal("Starting app installation for user", entry.Message);
            Assert.Equal("IntuneManagementExtension", entry.Component);
            Assert.Equal(1, entry.Type);
            Assert.Equal("1234", entry.Thread);
            // Timestamp should be parsed and converted to UTC
            Assert.NotEqual(default, entry.Timestamp);
        }

        [Fact]
        public void TryParseLine_EmptyLine_ReturnsFalse()
        {
            var result = CmTraceLogParser.TryParseLine("", out var entry);

            Assert.False(result);
            Assert.Null(entry);
        }

        [Fact]
        public void TryParseLine_NullLine_ReturnsFalse()
        {
            var result = CmTraceLogParser.TryParseLine(null, out var entry);

            Assert.False(result);
            Assert.Null(entry);
        }

        [Fact]
        public void TryParseLine_NonCmTrace_ReturnsFalse()
        {
            var result = CmTraceLogParser.TryParseLine(
                "2026-03-15 14:30:45 INFO This is a plain text log line", out var entry);

            Assert.False(result);
            Assert.Null(entry);
        }

        [Fact]
        public void TryParseLine_SingleDigitMonth_Parses()
        {
            // Real IME logs use M-d-yyyy (no zero-padding): "2-8-2026"
            var line = "<![LOG[Test message]LOG]!>" +
                       "<time=\"06:08:04.883\" date=\"2-8-2026\" " +
                       "component=\"IME\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";

            var result = CmTraceLogParser.TryParseLine(line, out var entry);

            Assert.True(result);
            Assert.Equal("Test message", entry.Message);
        }

        [Fact]
        public void TryParseLine_LongFractionalSeconds_Truncated()
        {
            // Some IME builds emit >7 fractional digits which would cause FormatException
            var line = "<![LOG[Test]LOG]!>" +
                       "<time=\"14:30:45.12345678901\" date=\"3-15-2026\" " +
                       "component=\"IME\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";

            var result = CmTraceLogParser.TryParseLine(line, out var entry);

            Assert.True(result);
            Assert.NotNull(entry);
        }

        [Fact]
        public void TryParseLine_NoFractionalSeconds_Parses()
        {
            var line = "<![LOG[Test]LOG]!>" +
                       "<time=\"14:30:45\" date=\"3-15-2026\" " +
                       "component=\"IME\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";

            var result = CmTraceLogParser.TryParseLine(line, out var entry);

            Assert.True(result);
        }

        [Fact]
        public void TryParseLine_WarningType_ParsesType2()
        {
            var line = "<![LOG[Warning message]LOG]!>" +
                       "<time=\"14:30:45.123\" date=\"3-15-2026\" " +
                       "component=\"IME\" context=\"\" type=\"2\" thread=\"1\" file=\"\">";

            var result = CmTraceLogParser.TryParseLine(line, out var entry);

            Assert.True(result);
            Assert.Equal(2, entry.Type);
        }

        [Fact]
        public void TryParseLine_MessageWithSpecialChars_Parses()
        {
            // Messages can contain brackets, quotes, and XML-like content
            var line = "<![LOG[Get policies = [Status: Success]]LOG]!>" +
                       "<time=\"14:30:45.123\" date=\"3-15-2026\" " +
                       "component=\"IME\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";

            var result = CmTraceLogParser.TryParseLine(line, out var entry);

            Assert.True(result);
            Assert.Equal("Get policies = [Status: Success]", entry.Message);
        }

        [Fact]
        public void TryParseLine_PartialCmTracePrefix_ReturnsFalse()
        {
            // Starts with <![LOG[ but doesn't complete the format
            var result = CmTraceLogParser.TryParseLine("<![LOG[incomplete", out var entry);

            Assert.False(result);
            Assert.Null(entry);
        }
    }
}
