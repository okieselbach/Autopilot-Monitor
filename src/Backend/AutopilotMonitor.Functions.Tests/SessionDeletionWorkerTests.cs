using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Worker-level tests for plan §5 PR4. Wraps the BackgroundService poll-loop in a
/// CancellationTokenSource and exercises the four explicit behaviours:
/// kill-switch entry guard, poison after max-dequeue (+ audit), malformed-envelope drop,
/// heartbeat extends visibility on long handler runs.
/// </summary>
public class SessionDeletionWorkerTests
{
    private const string TenantId   = "11111111-1111-1111-1111-111111111111";
    private const string SessionId  = "22222222-2222-2222-2222-222222222222";
    private const string ManifestId = "0123456789ABCDEF_FEDCBA9876543210";

    [Fact]
    public async Task Worker_does_not_receive_messages_while_kill_switch_active()
    {
        var harness = new Harness();
        harness.SetKillSwitch(true);

        await harness.RunForAsync(TimeSpan.FromMilliseconds(500));

        harness.MainQueue.Verify(q => q.ReceiveMessagesAsync(
            It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.HandlerMock.Verify(h => h.HandleAsync(
            It.IsAny<SessionDeletionEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Worker_moves_message_to_poison_after_max_dequeue_and_audits()
    {
        var harness = new Harness();
        var envelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: SessionDeletionWorker.MaxDequeueCount + 1);

        await harness.RunForAsync(TimeSpan.FromMilliseconds(500));

        // Poison queue received the body and main queue deleted the original.
        harness.PoisonQueue.Verify(q => q.SendMessageAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        harness.MainQueue.Verify(q => q.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        // Audit deletion_poisoned written with the parsed envelope's manifestId.
        Assert.Contains(harness.AuditCalls, a => a.Action == "deletion_poisoned"
            && a.Details != null && a.Details["manifestId"] == ManifestId);
    }

    [Fact]
    public async Task Worker_transitions_state_to_Poisoned_before_sending_to_poison_queue()
    {
        // PR4b E1 fix: MoveToPoisonAsync must CAS Sessions.DeletionState: Running → Poisoned
        // BEFORE sending to the poison queue. Without this transition, the restore endpoint
        // (PR4b) cannot dispatch into partial-restore mode (it keys off DeletionState=Poisoned).
        var harness = new Harness();
        var envelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: SessionDeletionWorker.MaxDequeueCount + 1);

        // Track call order: the CAS must come BEFORE the poison-queue send.
        var callOrder = new List<string>();
        harness.StorageMock.Setup(s => s.CasSetSessionDeletionStateAsync(
                TenantId, SessionId,
                SessionDeletionState.Running, SessionDeletionState.Poisoned,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("cas-running-to-poisoned"))
            .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
            {
                Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                CurrentState = SessionDeletionState.Poisoned,
            });
        harness.PoisonQueue.Setup(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("poison-queue-send"))
            .Returns<string, CancellationToken>((body, _) =>
            {
                var receipt = QueuesModelFactory.SendReceipt("poison-msg", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(7), "poison-pop", DateTimeOffset.UtcNow);
                return Task.FromResult(Response.FromValue(receipt, new Mock<Response>().Object));
            });

        await harness.RunForAsync(TimeSpan.FromMilliseconds(500));

        var casIdx = callOrder.IndexOf("cas-running-to-poisoned");
        var poisonIdx = callOrder.IndexOf("poison-queue-send");
        Assert.True(casIdx >= 0, "CAS Running→Poisoned was not issued");
        Assert.True(poisonIdx >= 0, "Poison-queue send was not issued");
        Assert.True(casIdx < poisonIdx,
            "CAS Running→Poisoned must precede the poison-queue send so the restore endpoint can dispatch.");
    }

    [Fact]
    public async Task Worker_falls_back_to_Queued_to_Poisoned_when_Running_CAS_misses()
    {
        // Rare case: cascade poisoned before the worker ever successfully transitioned Queued→Running
        // (handler always threw at the state-acquire step). In that case Running→Poisoned hits
        // WrongState and the fallback Queued→Poisoned must succeed.
        var harness = new Harness();
        var envelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: SessionDeletionWorker.MaxDequeueCount + 1);

        // First attempt: Running→Poisoned WrongState (current=Queued).
        harness.StorageMock.Setup(s => s.CasSetSessionDeletionStateAsync(
                TenantId, SessionId,
                SessionDeletionState.Running, SessionDeletionState.Poisoned,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
            {
                Outcome = TableStorageService.SessionDeletionStateCasOutcome.WrongState,
                CurrentState = SessionDeletionState.Queued,
            });
        // Fallback attempt: Queued→Poisoned Updated.
        harness.StorageMock.Setup(s => s.CasSetSessionDeletionStateAsync(
                TenantId, SessionId,
                SessionDeletionState.Queued, SessionDeletionState.Poisoned,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
            {
                Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                CurrentState = SessionDeletionState.Poisoned,
            });

        await harness.RunForAsync(TimeSpan.FromMilliseconds(500));

        // Both CAS attempts fired, and the poison-queue send still completed.
        harness.StorageMock.Verify(s => s.CasSetSessionDeletionStateAsync(
            TenantId, SessionId,
            SessionDeletionState.Running, SessionDeletionState.Poisoned,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.StorageMock.Verify(s => s.CasSetSessionDeletionStateAsync(
            TenantId, SessionId,
            SessionDeletionState.Queued, SessionDeletionState.Poisoned,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.PoisonQueue.Verify(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Worker_still_poisons_message_when_state_CAS_persistently_fails()
    {
        // Best-effort contract: if both CAS attempts fail (concurrent writer chaos), the
        // poison-queue move + audit must STILL complete so the operator has an observable
        // trail. Restore endpoint may reject with 409 "active_cascade" until the operator
        // manually intervenes — but the cascade message itself is off the main queue.
        var harness = new Harness();
        var envelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: SessionDeletionWorker.MaxDequeueCount + 1);

        harness.StorageMock.Setup(s => s.CasSetSessionDeletionStateAsync(
                TenantId, SessionId,
                It.IsAny<string>(), SessionDeletionState.Poisoned,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
            {
                Outcome = TableStorageService.SessionDeletionStateCasOutcome.WrongState,
                CurrentState = "Unknown",
            });

        await harness.RunForAsync(TimeSpan.FromMilliseconds(500));

        harness.PoisonQueue.Verify(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        Assert.Contains(harness.AuditCalls, a => a.Action == "deletion_poisoned");
    }

    [Fact]
    public async Task Worker_drops_malformed_envelope_without_invoking_handler()
    {
        var harness = new Harness();
        harness.EnqueueMessage("{ \"this is not valid JSON for an envelope: garbage", dequeueCount: 1);

        await harness.RunForAsync(TimeSpan.FromMilliseconds(500));

        harness.HandlerMock.Verify(h => h.HandleAsync(
            It.IsAny<SessionDeletionEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Malformed messages are removed from the main queue (otherwise they'd loop forever).
        harness.MainQueue.Verify(q => q.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        // Poison queue should NOT receive a malformed body — those are dropped, not poisoned.
        harness.PoisonQueue.Verify(q => q.SendMessageAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Worker_heartbeat_extends_visibility_while_handler_runs()
    {
        var harness = new Harness(heartbeatInterval: TimeSpan.FromMilliseconds(50));
        var envelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: 1);

        // Handler hangs for 350ms so the heartbeat task ticks at least 5×.
        harness.HandlerMock.Setup(h => h.HandleAsync(
                It.IsAny<SessionDeletionEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns(async (SessionDeletionEnvelope _, CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350), ct);
            });

        await harness.RunForAsync(TimeSpan.FromMilliseconds(800));

        // Heartbeat called at least 3× while the handler was busy (350ms / 50ms ≈ 7, minus a few
        // due to scheduling jitter). One call is enough to prove the heartbeat task is wired.
        harness.MainQueue.Verify(q => q.UpdateMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(3));
    }

    [Fact]
    public async Task Worker_drops_envelope_missing_required_fields_without_invoking_handler()
    {
        var harness = new Harness();
        var envelope = new SessionDeletionEnvelope
        {
            // ManifestId deliberately empty — required-fields guard must catch this.
            TenantId = TenantId, SessionId = SessionId, ManifestId = string.Empty,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(envelope), dequeueCount: 1);

        await harness.RunForAsync(TimeSpan.FromMilliseconds(500));

        harness.HandlerMock.Verify(h => h.HandleAsync(
            It.IsAny<SessionDeletionEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.MainQueue.Verify(q => q.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ============================================================ Harness ====

    private sealed class Harness
    {
        public Mock<QueueClient> MainQueue { get; }
        public Mock<QueueClient> PoisonQueue { get; }
        public Mock<SessionDeletionHandler> HandlerMock { get; }
        public Mock<AdminConfigurationService> AdminConfig { get; }
        public Mock<IMaintenanceRepository> Maintenance { get; }
        public List<AuditEntry> AuditCalls { get; } = new List<AuditEntry>();
        public SessionDeletionWorker Sut { get; }

        private readonly Queue<QueueMessage> _pendingMessages = new Queue<QueueMessage>();

        public Harness(TimeSpan? heartbeatInterval = null)
        {
            MainQueue = new Mock<QueueClient>();
            PoisonQueue = new Mock<QueueClient>();

            MainQueue.Setup(q => q.CreateIfNotExistsAsync(
                    It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Response?)null);
            PoisonQueue.Setup(q => q.CreateIfNotExistsAsync(
                    It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Response?)null);

            MainQueue.Setup(q => q.ReceiveMessagesAsync(
                    It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns<int, TimeSpan?, CancellationToken>((maxMessages, _, _) =>
                {
                    var batch = new List<QueueMessage>();
                    while (batch.Count < maxMessages && _pendingMessages.Count > 0)
                    {
                        batch.Add(_pendingMessages.Dequeue());
                    }
                    var response = QueuesModelFactory.QueueMessage(
                        messageId: "msg-batch", popReceipt: "pop-batch",
                        body: new BinaryData(string.Empty), dequeueCount: 0);
                    return Task.FromResult(Response.FromValue(batch.ToArray(), new Mock<Response>().Object));
                });

            MainQueue.Setup(q => q.DeleteMessageAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Mock<Response>().Object);

            MainQueue.Setup(q => q.UpdateMessageAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, string, TimeSpan, CancellationToken>((id, pr, body, vis, ct) =>
                {
                    var updated = QueuesModelFactory.UpdateReceipt(
                        "pop-extended-" + Guid.NewGuid().ToString("N"),
                        DateTimeOffset.UtcNow.Add(vis));
                    return Task.FromResult(Response.FromValue(updated, new Mock<Response>().Object));
                });

            PoisonQueue.Setup(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((body, _) =>
                {
                    var receipt = QueuesModelFactory.SendReceipt("poison-msg", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(7), "poison-pop", DateTimeOffset.UtcNow);
                    return Task.FromResult(Response.FromValue(receipt, new Mock<Response>().Object));
                });

            // Handler — virtual HandleAsync; default behaviour is no-op (handler succeeded).
            // Moq needs real ctor args for the proxy; the worker only calls HandleAsync so the
            // inner deps stay untouched.
            var storageMock = new Mock<TableStorageService>(Mock.Of<TableServiceClient>(), NullLogger<TableStorageService>.Instance);
            // PR4c F5: Worker now reads the Sessions row to verify PendingDeletionManifestId
            // matches the envelope's manifestId before issuing the poison CAS. Default mock
            // returns a row whose pending matches the test's ManifestId so the existing PR4b
            // tests (which exercise the post-pre-check CAS paths) keep working.
            storageMock.Setup(s => s.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new TableEntity(TenantId, SessionId)
                {
                    ["DeletionState"] = SessionDeletionState.Running,
                    ["PendingDeletionManifestId"] = ManifestId,
                });
            // Default: poison-state CAS succeeds. Individual tests override to verify the E1 fix.
            storageMock.Setup(s => s.CasSetSessionDeletionStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
                {
                    Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                    CurrentState = SessionDeletionState.Poisoned,
                });
            var blobMock = new Mock<BlobStorageService>(
                new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<BlobStorageService>.Instance, false);
            var verifierMock = new Mock<CascadeVerificationService>(
                Mock.Of<ISessionDeletionInventoryReader>(),
                NullLogger<CascadeVerificationService>.Instance);
            HandlerMock = new Mock<SessionDeletionHandler>(
                storageMock.Object, blobMock.Object, verifierMock.Object,
                Mock.Of<IMaintenanceRepository>(),
                new FakeSignalRNotificationService(),
                NullLogger<SessionDeletionHandler>.Instance);
            HandlerMock.Setup(h => h.HandleAsync(It.IsAny<SessionDeletionEnvelope>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            AdminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(),
                NullLogger<AdminConfigurationService>.Instance,
                new MemoryCache(new MemoryCacheOptions()));
            AdminConfig.Setup(a => a.GetConfigurationAsync())
                .ReturnsAsync(new AdminConfiguration { SessionDeletionKillSwitch = false });

            Maintenance = new Mock<IMaintenanceRepository>();
            Maintenance.Setup(m => m.LogAuditEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
                .Returns<string, string, string, string, string, Dictionary<string, string>?>(
                    (tenantId, action, entityType, entityId, performedBy, details) =>
                    {
                        AuditCalls.Add(new AuditEntry(tenantId, action, entityType, entityId, performedBy, details));
                        return Task.FromResult(true);
                    });

            Sut = new SessionDeletionWorker(
                MainQueue.Object, PoisonQueue.Object,
                HandlerMock.Object, storageMock.Object,
                AdminConfig.Object, Maintenance.Object,
                NullLogger<SessionDeletionWorker>.Instance,
                heartbeatInterval: heartbeatInterval ?? TimeSpan.FromMilliseconds(200),
                pollInterval: TimeSpan.FromMilliseconds(50));

            StorageMock = storageMock;
        }

        public Mock<TableStorageService> StorageMock { get; private set; } = null!;

        public void SetKillSwitch(bool active)
        {
            AdminConfig.Setup(a => a.GetConfigurationAsync())
                .ReturnsAsync(new AdminConfiguration { SessionDeletionKillSwitch = active });
        }

        public void EnqueueMessage(string body, int dequeueCount)
        {
            var msg = QueuesModelFactory.QueueMessage(
                messageId: "msg-" + Guid.NewGuid().ToString("N"),
                popReceipt: "pop-" + Guid.NewGuid().ToString("N"),
                body: new BinaryData(body),
                dequeueCount: dequeueCount);
            _pendingMessages.Enqueue(msg);
        }

        public async Task RunForAsync(TimeSpan duration)
        {
            using var cts = new CancellationTokenSource(duration);
            try { await Sut.StartAsync(cts.Token); }
            catch (OperationCanceledException) { /* expected */ }
            try { await Task.Delay(duration, cts.Token); }
            catch (OperationCanceledException) { /* expected on timeout */ }
            try { await Sut.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    private sealed record AuditEntry(
        string TenantId, string Action, string EntityType, string EntityId, string PerformedBy,
        Dictionary<string, string>? Details);
}
