using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Monitoring.Interop;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Watches a registry key for changes using RegNotifyChangeKeyValue.
    /// Fires the Changed event on each detected change.
    ///
    /// Usage:
    ///   var watcher = new RegistryWatcher(RegistryHive.LocalMachine, @"SOFTWARE\...", watchSubtree: true);
    ///   watcher.Changed += (s, e) => { /* read values */ };
    ///   watcher.Start();
    ///   // later:
    ///   await watcher.StopAsync();
    ///   watcher.Dispose();
    ///
    /// Important: one change per registration — the watcher re-registers automatically after each notification.
    /// The watched scope is a key (including values); to track a specific value, compare in the Changed handler.
    /// </summary>
    internal sealed class RegistryWatcher : IDisposable
    {
        private readonly UIntPtr _hive;
        private readonly string _subKey;
        private readonly bool _watchSubtree;
        private readonly RegistryNativeMethods.RegChangeNotifyFilter _filter;

        private CancellationTokenSource _cts;
        private Task _watchTask;
        private readonly object _sync = new object();

        /// <summary>
        /// Fired when the watched key (or subtree) changes. Handler runs on a thread pool thread.
        /// </summary>
        public event EventHandler Changed;

        /// <summary>
        /// Fired when the Changed handler throws. Allows the consumer to log without crashing the watcher.
        /// </summary>
        public event EventHandler<Exception> Error;

        public RegistryWatcher(
            RegistryHive hive,
            string subKey,
            bool watchSubtree = false,
            RegistryNativeMethods.RegChangeNotifyFilter filter =
                RegistryNativeMethods.RegChangeNotifyFilter.LastSet)
        {
            _hive = HiveToPointer(hive);
            _subKey = subKey ?? throw new ArgumentNullException(nameof(subKey));
            _watchSubtree = watchSubtree;
            _filter = filter;
        }

        /// <summary>
        /// Starts watching on a background thread pool thread.
        /// Throws Win32Exception if the registry key cannot be opened.
        /// </summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_watchTask != null)
                    throw new InvalidOperationException("Watcher is already running.");

                _cts = new CancellationTokenSource();
                _watchTask = Task.Run(() => WatchLoopAsync(_cts.Token));
            }
        }

        /// <summary>
        /// Non-blocking stop request. Safe to call from within the Changed handler
        /// (does not wait for the loop to exit, so no deadlock).
        /// </summary>
        public void RequestStop()
        {
            lock (_sync)
            {
                _cts?.Cancel();
            }
        }

        /// <summary>
        /// Cancels the watcher and waits for the background loop to exit.
        /// Do NOT call from within the Changed handler — use RequestStop() instead.
        /// </summary>
        public async Task StopAsync()
        {
            Task taskToWait;

            lock (_sync)
            {
                if (_cts == null)
                    return;

                _cts.Cancel();
                taskToWait = _watchTask;
            }

            if (taskToWait != null)
            {
                try
                {
                    await taskToWait.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Win32Exception) { }
            }

            lock (_sync)
            {
                _cts?.Dispose();
                _cts = null;
                _watchTask = null;
            }
        }

        private async Task WatchLoopAsync(CancellationToken cancellationToken)
        {
            int openResult = RegistryNativeMethods.RegOpenKeyEx(
                _hive, _subKey, 0, RegistryNativeMethods.KEY_NOTIFY,
                out SafeRegistryHandle keyHandle);

            if (openResult != 0)
                throw new Win32Exception(openResult, $"RegOpenKeyEx failed for '{_subKey}'");

            using (keyHandle)
            using (var changedEvent = new AutoResetEvent(false))
            using (var cancelEvent = new ManualResetEvent(false))
            using (cancellationToken.Register(() => cancelEvent.Set()))
            {
                var waitHandles = new WaitHandle[] { changedEvent, cancelEvent };

                while (!cancellationToken.IsCancellationRequested)
                {
                    // Register for exactly ONE change notification
                    int notifyResult = RegistryNativeMethods.RegNotifyChangeKeyValue(
                        keyHandle,
                        _watchSubtree,
                        _filter,
                        changedEvent.SafeWaitHandle,
                        true);

                    if (notifyResult != 0)
                        throw new Win32Exception(notifyResult, "RegNotifyChangeKeyValue failed");

                    // Wait for change or cancellation
                    int signaled = WaitHandle.WaitAny(waitHandles);

                    if (signaled == 1)
                        break; // cancel requested

                    // Notify consumer
                    try
                    {
                        Changed?.Invoke(this, EventArgs.Empty);
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(this, ex);
                    }

                    await Task.Yield();
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        public void Dispose()
        {
            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Dispose must not throw
            }
        }

        // ===== Helpers =====

        private static UIntPtr HiveToPointer(RegistryHive hive)
        {
            switch (hive)
            {
                case RegistryHive.ClassesRoot: return (UIntPtr)0x80000000u;
                case RegistryHive.CurrentUser: return (UIntPtr)0x80000001u;
                case RegistryHive.LocalMachine: return (UIntPtr)0x80000002u;
                case RegistryHive.Users: return (UIntPtr)0x80000003u;
                case RegistryHive.CurrentConfig: return (UIntPtr)0x80000005u;
                default: throw new ArgumentOutOfRangeException(nameof(hive));
            }
        }
    }
}
