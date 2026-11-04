using System;
using System.Collections.Generic;
using UnityEngine;

namespace November.DI.Tests
{
    /// <summary>
    /// Counts log messages through <c>Application.logMessageReceived</c>.
    ///
    /// <c>LogAssert</c> only fails a test for an unexpected Error / Assert / Exception, so "this warning appears
    /// exactly once" and "nothing is logged" cannot be asserted with it alone; they have to be counted.
    /// Create it before the code under test and dispose it afterwards (<c>using</c>).
    /// </summary>
    internal sealed class LogCounter : IDisposable
    {
        private readonly List<(LogType Type, string Message)> _entries = new List<(LogType, string)>();

        public LogCounter() => Application.logMessageReceived += OnLog;

        public void Dispose() => Application.logMessageReceived -= OnLog;

        private void OnLog(string condition, string stackTrace, LogType type) => _entries.Add((type, condition));

        /// <summary>Every message received, of any type.</summary>
        public int Total => _entries.Count;

        /// <summary>Messages of one type, optionally only those whose text contains <paramref name="contains"/>.</summary>
        public int Count(LogType type, string contains = null)
        {
            int count = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Type != type) continue;
                if (contains != null && _entries[i].Message.IndexOf(contains, StringComparison.Ordinal) < 0) continue;
                count++;
            }
            return count;
        }

        public override string ToString()
        {
            var lines = new List<string>(_entries.Count);
            foreach (var entry in _entries) lines.Add("[" + entry.Type + "] " + entry.Message);
            return string.Join("\n", lines);
        }
    }
}
