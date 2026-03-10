using System;
using System.ComponentModel;
using System.Threading;
using AutopilotMonitor.Agent.Core.Monitoring.Interop;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Watches a registry key for changes using RegNotifyChangeKeyValue.
    /// Raises Changed whenever the watched key/subtree changes.
    /// 
    /// Notes:
    /// - Notification is for the key scope, not one individual value.
    /// - To track a specific value, read/compare that value in the Changed handler.
    /// - Uses a dedicated background thread for robustness.
    /// </summary>
    internal sealed class RegistryWatcher : IDisposable
    {
        private readonly UIntPtr _hive;
        private readonly string _subKey;
        private readonly bool _watchSubtree;
        private readonly RegistryView _view;
        private readonly RegistryNativeMethods.RegChangeNotifyFilter _filter;

        private readonly object _sync = new object();

        private CancellationTokenSource _cts;
        private Thread _thread;
        private Exception _backgroundException;
        private bool _disposed;

        public event EventHandler Changed;
        public event EventHandler<Exception> Error;

        public RegistryWatcher(
            RegistryHive hive,
            string subKey,
            bool watchSubtree = false,
            RegistryView view = RegistryView.Default,
            RegistryNativeMethods.RegChangeNotifyFilter filter =
                RegistryNativeMethods.RegChangeNotifyFilter.LastSet |
                RegistryNativeMethods.RegChangeNotifyFilter.ThreadAgnostic)
        {
            _hive = HiveToPointer(hive);
            _subKey = subKey ?? throw new ArgumentNullException(nameof(subKey));
            _watchSubtree = watchSubtree;
            _view = view;
            _filter = filter;
        }

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                {
                    return _thread != null;
                }
            }
        }

        public void Start()
        {
            ThrowIfDisposed();

            lock (_sync)
            {
                if (_thread != null)
                    throw new InvalidOperationException("Watcher is already running.");

                _backgroundException = null;
                _cts = new CancellationTokenSource();

                _thread = new Thread(WatchThreadMain)
                {
                    IsBackground = true,
                    Name = $"RegistryWatcher: {_subKey}"
                };

                _thread.Start(_cts.Token);
            }
        }

        /// <summary>
        /// Requests stop without blocking.
        /// Safe to call from Changed handlers.
        /// </summary>
        public void RequestStop()
        {
            lock (_sync)
            {
                _cts?.Cancel();
            }
        }

        /// <summary>
        /// Stops and waits for the watcher thread to exit.
        /// Avoid calling from inside Changed; use RequestStop there.
        /// </summary>
        public void Stop()
        {
            Thread threadToJoin = null;
            CancellationTokenSource ctsToDispose = null;
            Exception backgroundException = null;

            lock (_sync)
            {
                if (_thread == null)
                    return;

                _cts.Cancel();
                threadToJoin = _thread;
            }

            threadToJoin.Join();

            lock (_sync)
            {
                backgroundException = _backgroundException;

                ctsToDispose = _cts;
                _cts = null;
                _thread = null;
                _backgroundException = null;
            }

            ctsToDispose?.Dispose();

            if (backgroundException != null &&
                !(backgroundException is OperationCanceledException))
            {
                throw new InvalidOperationException(
                    "Registry watcher stopped because the background thread failed.",
                    backgroundException);
            }
        }

        private void WatchThreadMain(object state)
        {
            var cancellationToken = (CancellationToken)state;

            try
            {
                WatchLoop(cancellationToken);
            }
            catch (OperationCanceledException ex)
            {
                lock (_sync)
                {
                    _backgroundException = ex;
                }
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _backgroundException = ex;
                }

                try
                {
                    Error?.Invoke(this, ex);
                }
                catch
                {
                    // Never let handler exceptions escape the background thread.
                }
            }
        }

        private void WatchLoop(CancellationToken cancellationToken)
        {
            int samDesired = RegistryNativeMethods.GetSamDesired(_view);

            int openResult = RegistryNativeMethods.RegOpenKeyEx(
                _hive,
                _subKey,
                0,
                samDesired,
                out SafeRegistryHandle keyHandle);

            if (openResult != 0)
            {
                throw new Win32Exception(
                    openResult,
                    $"RegOpenKeyEx failed for '{_subKey}' (view: {_view}).");
            }

            using (keyHandle)
            using (var changedEvent = new AutoResetEvent(false))
            using (var cancelEvent = new ManualResetEvent(false))
            using (cancellationToken.Register(() => cancelEvent.Set()))
            {
                WaitHandle[] waitHandles = { changedEvent, cancelEvent };

                while (!cancellationToken.IsCancellationRequested)
                {
                    int notifyResult = RegistryNativeMethods.RegNotifyChangeKeyValue(
                        keyHandle,
                        _watchSubtree,
                        _filter,
                        changedEvent.SafeWaitHandle,
                        true);

                    if (notifyResult != 0)
                    {
                        throw new Win32Exception(
                            notifyResult,
                            $"RegNotifyChangeKeyValue failed for '{_subKey}'.");
                    }

                    int signaled = WaitHandle.WaitAny(waitHandles);

                    if (signaled == WaitHandle.WaitTimeout)
                        continue;

                    if (signaled == 1)
                        break;

                    try
                    {
                        Changed?.Invoke(this, EventArgs.Empty);
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Error?.Invoke(this, ex);
                        }
                        catch
                        {
                            // Ignore secondary error-handler failures.
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                Stop();
            }
            catch
            {
                // Dispose must not throw.
            }

            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RegistryWatcher));
        }

        private static UIntPtr HiveToPointer(RegistryHive hive)
        {
            switch (hive)
            {
                case RegistryHive.ClassesRoot:
                    return (UIntPtr)0x80000000u;
                case RegistryHive.CurrentUser:
                    return (UIntPtr)0x80000001u;
                case RegistryHive.LocalMachine:
                    return (UIntPtr)0x80000002u;
                case RegistryHive.Users:
                    return (UIntPtr)0x80000003u;
                case RegistryHive.CurrentConfig:
                    return (UIntPtr)0x80000005u;
                default:
                    throw new ArgumentOutOfRangeException(nameof(hive));
            }
        }
    }
}