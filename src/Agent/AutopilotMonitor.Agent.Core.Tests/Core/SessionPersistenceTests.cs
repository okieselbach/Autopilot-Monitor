using System;
using System.IO;
using AutopilotMonitor.Agent.Core.Monitoring.Runtime;
using Xunit;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;

namespace AutopilotMonitor.Agent.Core.Tests.Core
{
    /// <summary>
    /// Tests session persistence across agent restarts, reboots, and OS resets.
    /// Prevents: session splits when session.created is lost during reboot (orphan guard
    /// was too aggressive — deleted session.id instead of recovering session.created).
    /// </summary>
    public class SessionPersistenceTests : IDisposable
    {
        private readonly string _tempDir;

        public SessionPersistenceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "autopilot-session-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void LoadOrCreateSessionId_FirstRun_CreatesNewSession()
        {
            var persistence = new SessionPersistence(_tempDir);
            var sessionId = persistence.LoadOrCreateSessionId();

            Assert.True(Guid.TryParse(sessionId, out _));
            Assert.True(File.Exists(Path.Combine(_tempDir, "session.id")));
            Assert.True(File.Exists(Path.Combine(_tempDir, "session.created")));
        }

        [Fact]
        public void LoadOrCreateSessionId_ExistingSession_ReturnsExistingId()
        {
            var persistence = new SessionPersistence(_tempDir);
            var firstId = persistence.LoadOrCreateSessionId();

            // Simulate agent restart — new SessionPersistence instance, same directory
            var persistence2 = new SessionPersistence(_tempDir);
            var secondId = persistence2.LoadOrCreateSessionId();

            Assert.Equal(firstId, secondId);
        }

        /// <summary>
        /// Regression test for the session-split bug: after a reboot, if session.created
        /// is lost (filesystem flush, corruption, etc.) but session.id survives,
        /// the agent must resume the existing session — not create a new one.
        /// </summary>
        [Fact]
        public void LoadOrCreateSessionId_RecentSessionId_MissingSessionCreated_ResumesSession()
        {
            // Arrange: create a valid session, then delete session.created to simulate loss
            var persistence = new SessionPersistence(_tempDir);
            var originalId = persistence.LoadOrCreateSessionId();

            File.Delete(Path.Combine(_tempDir, "session.created"));
            Assert.False(File.Exists(Path.Combine(_tempDir, "session.created")));

            // Act: simulate agent restart after reboot
            var persistence2 = new SessionPersistence(_tempDir);
            var resumedId = persistence2.LoadOrCreateSessionId();

            // Assert: same session ID, session.created re-initialized
            Assert.Equal(originalId, resumedId);
            Assert.True(File.Exists(Path.Combine(_tempDir, "session.created")));
        }

        /// <summary>
        /// True orphan: session.id from a previous enrollment (old file) without
        /// session.created should still be discarded and a new session created.
        /// </summary>
        [Fact]
        public void LoadOrCreateSessionId_OldSessionId_MissingSessionCreated_CreatesNewSession()
        {
            // Arrange: write a session.id with an old modification time
            var sessionIdPath = Path.Combine(_tempDir, "session.id");
            var oldGuid = Guid.NewGuid().ToString();
            File.WriteAllText(sessionIdPath, oldGuid);

            // Set file modification time to well beyond the orphan guard threshold
            var oldTime = DateTime.UtcNow.AddHours(-(SessionPersistence.OrphanGuardMaxAgeHours + 24));
            File.SetLastWriteTimeUtc(sessionIdPath, oldTime);

            // No session.created file — simulates OS reset where ProgramData survived

            // Act
            var persistence = new SessionPersistence(_tempDir);
            var newId = persistence.LoadOrCreateSessionId();

            // Assert: new session created, not the old orphan
            Assert.NotEqual(oldGuid, newId);
            Assert.True(Guid.TryParse(newId, out _));
            Assert.True(File.Exists(Path.Combine(_tempDir, "session.created")));
        }

        [Fact]
        public void LoadOrCreateSessionId_RecentSessionId_MissingSessionCreated_PreservesSequence()
        {
            // Arrange: create session with sequence, then delete session.created
            var persistence = new SessionPersistence(_tempDir);
            var originalId = persistence.LoadOrCreateSessionId();
            persistence.SaveSequence(42);

            File.Delete(Path.Combine(_tempDir, "session.created"));

            // Act: restart
            var persistence2 = new SessionPersistence(_tempDir);
            var resumedId = persistence2.LoadOrCreateSessionId();
            var sequence = persistence2.LoadSequence();

            // Assert: session AND sequence preserved
            Assert.Equal(originalId, resumedId);
            Assert.Equal(42, sequence);
        }

        [Fact]
        public void LoadOrCreateSessionId_OldSessionId_MissingSessionCreated_ClearsSequence()
        {
            // Arrange: old session.id + sequence file, no session.created
            var sessionIdPath = Path.Combine(_tempDir, "session.id");
            File.WriteAllText(sessionIdPath, Guid.NewGuid().ToString());
            File.SetLastWriteTimeUtc(sessionIdPath,
                DateTime.UtcNow.AddHours(-(SessionPersistence.OrphanGuardMaxAgeHours + 24)));

            var seqPath = Path.Combine(_tempDir, "session.seq");
            File.WriteAllText(seqPath, "99");

            // Act
            var persistence = new SessionPersistence(_tempDir);
            persistence.LoadOrCreateSessionId();
            var sequence = persistence.LoadSequence();

            // Assert: sequence reset (old orphan files cleaned up)
            Assert.Equal(0, sequence);
        }

        [Fact]
        public void DeleteSession_RemovesAllFiles()
        {
            var persistence = new SessionPersistence(_tempDir);
            persistence.LoadOrCreateSessionId();
            persistence.SaveSequence(10);

            persistence.DeleteSession();

            Assert.False(File.Exists(Path.Combine(_tempDir, "session.id")));
            Assert.False(File.Exists(Path.Combine(_tempDir, "session.seq")));
            Assert.False(File.Exists(Path.Combine(_tempDir, "session.created")));
        }

        [Fact]
        public void NewSession_Flag_ForcesNewSession()
        {
            var persistence = new SessionPersistence(_tempDir);
            var firstId = persistence.LoadOrCreateSessionId();

            // Simulate --new-session flag: delete then create
            persistence.DeleteSession();
            var secondId = persistence.LoadOrCreateSessionId();

            Assert.NotEqual(firstId, secondId);
        }
    }
}
