using System;
using System.Diagnostics;
using System.IO;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Terminal
{
    /// <summary>
    /// Debug recorder for rendering issues: stores raw PTY output and resize events with timestamps
    /// so a session can be replayed into the emulator outside Unity.
    /// Format, little endian: "FTREC1" then records of [byte kind][int32 elapsedMs][int32 length][payload].
    /// Kind 'D' = output bytes, 'R' = resize (payload: int32 cols, int32 rows).
    /// </summary>
    internal sealed class PtyRecorder : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private BinaryWriter _writer;

        public string Path { get; }

        public PtyRecorder(string path, int cols, int rows)
        {
            Path = path;
            _writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
            _writer.Write(new[] { (byte)'F', (byte)'T', (byte)'R', (byte)'E', (byte)'C', (byte)'1' });
            WriteResize(cols, rows);
        }

        public void WriteOutput(byte[] data, int count)
        {
            lock (_gate)
            {
                if (_writer == null) return;
                WriteHeader((byte)'D', count);
                _writer.Write(data, 0, count);
            }
        }

        public void WriteResize(int cols, int rows)
        {
            lock (_gate)
            {
                if (_writer == null) return;
                WriteHeader((byte)'R', 8);
                _writer.Write(cols);
                _writer.Write(rows);
                _writer.Flush();
            }
        }

        private void WriteHeader(byte kind, int length)
        {
            _writer.Write(kind);
            _writer.Write((int)_clock.ElapsedMilliseconds);
            _writer.Write(length);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
    }
}
