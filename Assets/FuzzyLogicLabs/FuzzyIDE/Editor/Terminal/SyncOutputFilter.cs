using System.Diagnostics;
using System.Threading;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Terminal
{
    /// <summary>
    /// Tracks DEC private mode 2026 (synchronized output) in the raw PTY byte stream.
    /// TUIs such as Claude Code wrap every frame in CSI ?2026h ... CSI ?2026l; while the mode is
    /// set the renderer must not paint, otherwise half-drawn frames show up as stale text.
    /// XtermSharp does not know the mode, so the stream is scanned before it is fed to the emulator.
    /// The bytes are passed through unchanged.
    /// </summary>
    internal sealed class SyncOutputFilter
    {
        // A frame that never ends (crashed app, lost bytes) must not freeze the screen.
        private const double MaxHoldSeconds = 0.2;

        private static readonly byte[] Prefix = { 0x1b, (byte)'[', (byte)'?', (byte)'2', (byte)'0', (byte)'2', (byte)'6' };

        private int _matched;
        private bool _sawDollar;
        private volatile bool _active;
        private long _activeSince;
        private int _pendingQueries;

        public bool Active => _active;

        /// <summary>True while a synchronized frame is in progress and has not timed out.</summary>
        public bool IsHolding
        {
            get
            {
                if (!_active) return false;
                long elapsed = Stopwatch.GetTimestamp() - Interlocked.Read(ref _activeSince);
                return elapsed < (long)(MaxHoldSeconds * Stopwatch.Frequency);
            }
        }

        /// <summary>Scans a chunk; state carries over between calls so split sequences are handled.</summary>
        public void Scan(byte[] data, int count)
        {
            for (int i = 0; i < count; i++)
            {
                byte b = data[i];
                if (_matched == Prefix.Length)
                {
                    if (_sawDollar)
                    {
                        // DECRQM: CSI ? 2026 $ p
                        if (b == (byte)'p') Interlocked.Increment(ref _pendingQueries);
                    }
                    else if (b == (byte)'$')
                    {
                        _sawDollar = true;
                        continue;
                    }
                    else if (b == (byte)'h')
                    {
                        Interlocked.Exchange(ref _activeSince, Stopwatch.GetTimestamp());
                        _active = true;
                    }
                    else if (b == (byte)'l')
                    {
                        _active = false;
                    }
                    _matched = 0;
                    _sawDollar = false;
                }

                if (b == Prefix[_matched]) _matched++;
                else _matched = b == 0x1b ? 1 : 0;
            }
        }

        /// <summary>Returns and clears the number of DECRQM queries for mode 2026 seen so far.</summary>
        public int TakePendingQueries() => Interlocked.Exchange(ref _pendingQueries, 0);

        /// <summary>DECRPM reply: mode 2026 is recognized and currently reset.</summary>
        public static readonly byte[] QueryReply = System.Text.Encoding.ASCII.GetBytes("\x1b[?2026;2$y");

        public void Reset()
        {
            _matched = 0;
            _sawDollar = false;
            _active = false;
        }
    }
}
