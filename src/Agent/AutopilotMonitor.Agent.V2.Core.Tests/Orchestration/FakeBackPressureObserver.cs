using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    internal sealed class FakeBackPressureObserver : IBackPressureObserver
    {
        private readonly object _lock = new object();
        private readonly List<BackPressureCall> _calls = new List<BackPressureCall>();

        public int CallCount
        {
            get { lock (_lock) return _calls.Count; }
        }

        public IReadOnlyList<BackPressureCall> Calls
        {
            get { lock (_lock) return _calls.ToArray(); }
        }

        public Exception? ThrowOnNotify { get; set; }

        public void OnBackPressure(string origin, int channelCapacity, int queueLength, TimeSpan blockDuration)
        {
            lock (_lock)
            {
                _calls.Add(new BackPressureCall(origin, channelCapacity, queueLength, blockDuration));
            }
            if (ThrowOnNotify != null) throw ThrowOnNotify;
        }

        internal sealed class BackPressureCall
        {
            public BackPressureCall(string origin, int channelCapacity, int queueLength, TimeSpan blockDuration)
            {
                Origin = origin;
                ChannelCapacity = channelCapacity;
                QueueLength = queueLength;
                BlockDuration = blockDuration;
            }

            public string Origin { get; }
            public int ChannelCapacity { get; }
            public int QueueLength { get; }
            public TimeSpan BlockDuration { get; }
        }
    }
}
