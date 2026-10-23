using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Terminal
{
    /// <summary>
    /// Byte-level pass in front of XtermSharp that removes or rewrites sequences the emulator gets wrong.
    /// All of these were reproduced by feeding the real XtermSharp.dll:
    /// <list type="bullet">
    /// <item>Truecolor SGR (38;2 / 48;2) throws NotImplementedException and aborts the rest of the chunk.
    /// Rewritten to the nearest xterm-256 index; underline color (58) dropped; 4:n mapped to 4 / 24.</item>
    /// <item>Private CSI prefixes are ignored: CSI &gt;4;2m becomes SGR underline+dim and CSI &gt;1u becomes
    /// restore-cursor. These keyboard-protocol sequences are dropped.</item>
    /// <item>OSC strings (titles, hyperlinks, queries) are parsed byte by byte, so UTF-8 continuation bytes in
    /// 0x80-0x9F end the string early (0x9C is C1 ST) and the rest of the title is printed onto the screen.
    /// Even a plain ASCII title terminated by BEL swallows the following text. Claude Code updates the title
    /// with a spinner ("✳ Claude Code", bytes e2 9c b3) many times a second, which shifted the screen.
    /// OSC, DCS, APC, PM and SOS strings are therefore consumed here and never reach XtermSharp: titles are
    /// decoded and reported through <see cref="TitleChanged"/>, color queries are answered, the rest dropped.</item>
    /// </list>
    /// State is kept across calls, so sequences split between PTY reads are handled.
    /// </summary>
    internal sealed class VtStreamFilter
    {
        private const int MaxCsiLength = 512;
        private const int MaxStringLength = 8192;

        private enum State { Ground, Escape, Csi, String, StringEscape }

        private State _state = State.Ground;
        private readonly byte[] _csi = new byte[MaxCsiLength];
        private int _csiLength; // includes the leading ESC '['
        private byte _stringKind; // ']' OSC, 'P' DCS, '_' APC, '^' PM, 'X' SOS
        private readonly byte[] _string = new byte[MaxStringLength];
        private int _stringLength;
        private byte[] _output = new byte[16384];
        private int _outputLength;
        private readonly List<string> _params = new List<string>(16);

        /// <summary>Raised on the reader thread when the application sets the window title (OSC 0 / 2).</summary>
        public Action<string> TitleChanged;

        /// <summary>Replies the terminal owes the application (e.g. color queries); drained by the session.</summary>
        public readonly ConcurrentQueue<byte[]> PendingReplies = new ConcurrentQueue<byte[]>();

        public byte[] Output => _output;

        /// <summary>Processes a chunk; the filtered bytes are in <see cref="Output"/>[0..return value).</summary>
        public int Process(byte[] data, int count)
        {
            // Re-applied autowrap after alt-screen exits can make the output longer than the input.
            EnsureCapacity(count * 2 + MaxCsiLength + 16);
            _outputLength = 0;

            for (int i = 0; i < count; i++)
            {
                byte b = data[i];
                switch (_state)
                {
                    case State.Ground:
                        if (b == 0x1b) _state = State.Escape;
                        else Emit(b);
                        break;

                    case State.Escape:
                        if (b == (byte)'[')
                        {
                            _csi[0] = 0x1b;
                            _csi[1] = b;
                            _csiLength = 2;
                            _state = State.Csi;
                        }
                        else if (b == (byte)']' || b == (byte)'P' || b == (byte)'_' || b == (byte)'^' || b == (byte)'X')
                        {
                            _stringKind = b;
                            _stringLength = 0;
                            _state = State.String;
                        }
                        else if (b == 0x1b)
                        {
                            Emit(0x1b); // lone ESC; the new one starts another sequence
                        }
                        else
                        {
                            Emit(0x1b);
                            Emit(b);
                            if (b == (byte)'8') EmitAutowrap(); // DECRC, see EmitPrivateMode
                            else if (b == (byte)'c')
                            {
                                // RIS: XtermSharp keeps the scroll region, so line feeds at the bottom stop scrolling.
                                _autowrap = true;
                                Emit(0x1b);
                                Emit((byte)'[');
                                Emit((byte)'r');
                            }
                            _state = State.Ground;
                        }
                        break;

                    case State.Csi:
                        if (b == 0x1b)
                        {
                            // ESC aborts the sequence; pass the partial bytes through untouched.
                            Emit(_csi, _csiLength);
                            _state = State.Escape;
                            break;
                        }
                        _csi[_csiLength++] = b;
                        if (b >= 0x40 && b <= 0x7e)
                        {
                            EmitCsi();
                            _state = State.Ground;
                        }
                        else if (_csiLength == MaxCsiLength)
                        {
                            Emit(_csi, _csiLength);
                            _state = State.Ground;
                        }
                        break;

                    case State.String:
                        if (b == 0x07)
                        {
                            DispatchString();
                            _state = State.Ground;
                        }
                        else if (b == 0x1b)
                        {
                            _state = State.StringEscape;
                        }
                        else if (b == 0x18 || b == 0x1a)
                        {
                            _state = State.Ground; // CAN / SUB cancel the string
                        }
                        else if (_stringLength < MaxStringLength)
                        {
                            _string[_stringLength++] = b;
                        }
                        break;

                    case State.StringEscape:
                        DispatchString();
                        if (b == (byte)'\\')
                        {
                            _state = State.Ground; // ST
                        }
                        else
                        {
                            // ESC + anything else ends the string and starts a new sequence.
                            _state = State.Escape;
                            i--;
                        }
                        break;
                }
            }

            return _outputLength;
        }

        public void Reset()
        {
            _state = State.Ground;
            _csiLength = 0;
            _stringLength = 0;
        }

        private void DispatchString()
        {
            if (_stringKind != (byte)']') return;

            string text = Encoding.UTF8.GetString(_string, 0, _stringLength);
            int separator = text.IndexOf(';');
            string command = separator >= 0 ? text.Substring(0, separator) : text;
            string argument = separator >= 0 ? text.Substring(separator + 1) : string.Empty;

            switch (command)
            {
                case "0":
                case "2":
                    TitleChanged?.Invoke(argument);
                    break;
                case "10":
                    if (argument == "?") Reply("\x1b]10;" + ToXParseColor(ForegroundRgb) + "\x1b\\");
                    break;
                case "11":
                    if (argument == "?") Reply("\x1b]11;" + ToXParseColor(BackgroundRgb) + "\x1b\\");
                    break;
                case "12":
                    if (argument == "?") Reply("\x1b]12;" + ToXParseColor(CursorRgb) + "\x1b\\");
                    break;
            }
        }

        private void Reply(string text) => PendingReplies.Enqueue(Encoding.ASCII.GetBytes(text));

        /// <summary>Theme colors (0xRRGGBB) reported to OSC 10/11/12 queries; apps such as Claude Code pick light or dark output from them.</summary>
        public static volatile uint ForegroundRgb = 0xcccccc, BackgroundRgb = 0x0c0c0c, CursorRgb = 0xcccccc;

        private static string ToXParseColor(uint rgb)
        {
            uint r = (rgb >> 16) & 0xff, g = (rgb >> 8) & 0xff, b = rgb & 0xff;
            return $"rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}";
        }

        private void EmitCsi()
        {
            byte final = _csi[_csiLength - 1];
            if (IsMisreadPrivateSequence(final)) return;
            if ((final == (byte)'h' || final == (byte)'l') && _csiLength > 3 && _csi[2] == (byte)'?')
            {
                EmitPrivateMode(final == (byte)'h');
                return;
            }
            if (final != (byte)'m' || !HasOnlySgrParameterBytes())
            {
                Emit(_csi, _csiLength);
                if (final == (byte)'u' && _csiLength == 3) EmitAutowrap(); // SCORC restores like DECRC
                return;
            }

            string parameters = Encoding.ASCII.GetString(_csi, 2, _csiLength - 3);
            if (parameters.IndexOf(':') < 0 && parameters.IndexOf("8;", StringComparison.Ordinal) < 0)
            {
                Emit(_csi, _csiLength);
                return;
            }

            string rewritten = RewriteParameters(parameters);
            if (rewritten == null) return; // every parameter was dropped; emitting ESC[m would reset attributes

            Emit(0x1b);
            Emit((byte)'[');
            for (int i = 0; i < rewritten.Length; i++) Emit((byte)rewritten[i]);
            Emit((byte)'m');
        }

        /// <summary>
        /// Passes DEC private mode set/reset through, tracking autowrap (DECAWM, mode 7). XtermSharp's
        /// cursor restore (DECRC / ESC 8, CSI u, and leaving the alternate screen with CSI ?1049l / ?1047l /
        /// ?47l) also restores a saved autowrap flag that its cursor save never fills in, so autowrap ends up
        /// off. After a full-screen app exits (Claude Code sends ESC 7 ESC [r ESC 8), long lines then overwrite
        /// their last column instead of wrapping and the shell output no longer matches ConPTY's. The tracked
        /// state is re-applied right after every cursor restore.
        /// </summary>
        private void EmitPrivateMode(bool set)
        {
            Emit(_csi, _csiLength);

            bool leftAltScreen = false;
            int value = 0;
            bool hasDigits = false;
            for (int i = 3; i < _csiLength; i++)
            {
                byte c = _csi[i];
                if (c >= (byte)'0' && c <= (byte)'9')
                {
                    value = value * 10 + (c - '0');
                    hasDigits = true;
                    continue;
                }
                if (hasDigits)
                {
                    if (value == 7) _autowrap = set;
                    else if (!set && (value == 1049 || value == 1047 || value == 47)) leftAltScreen = true;
                }
                value = 0;
                hasDigits = false;
            }

            if (leftAltScreen) EmitAutowrap();
        }

        private void EmitAutowrap()
        {
            foreach (char ch in _autowrap ? "\x1b[?7h" : "\x1b[?7l") Emit((byte)ch);
        }

        private bool _autowrap = true;

        /// <summary>
        /// XtermSharp ignores the private prefix of a CSI sequence, so keyboard-protocol negotiation is
        /// executed as something else: CSI &gt; 4;2 m (modifyOtherKeys) becomes SGR underline+dim, and
        /// CSI &gt; 1 u / CSI = 1 u / CSI ? u (kitty keyboard) become "restore cursor", which makes the
        /// next text land at a stale position. Neither protocol is supported, so these are dropped.
        /// </summary>
        private bool IsMisreadPrivateSequence(byte final)
        {
            if (_csiLength < 4) return false;
            byte prefix = _csi[2];
            bool isPrivate = prefix == (byte)'>' || prefix == (byte)'=' || prefix == (byte)'<' || prefix == (byte)'?';
            if (!isPrivate) return false;
            return final == (byte)'u' || final == (byte)'m';
        }

        private bool HasOnlySgrParameterBytes()
        {
            for (int i = 2; i < _csiLength - 1; i++)
            {
                byte c = _csi[i];
                if (!((c >= (byte)'0' && c <= (byte)'9') || c == (byte)';' || c == (byte)':')) return false;
            }
            return true;
        }

        /// <summary>Returns the rewritten parameter string, or null when nothing is left.</summary>
        internal string RewriteParameters(string parameters)
        {
            string[] tokens = parameters.Split(';');
            _params.Clear();

            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];

                if (token.IndexOf(':') >= 0)
                {
                    string[] sub = token.Split(':');
                    string head = sub[0];
                    if (head == "38" || head == "48" || head == "58")
                    {
                        int index = -1;
                        if (sub.Length >= 3 && sub[1] == "5") index = ParseInt(sub[2]);
                        else if (sub.Length >= 5 && sub[1] == "2")
                        {
                            // 38:2:<colorspace>:r:g:b or the common 38:2:r:g:b
                            int o = sub.Length >= 6 ? sub.Length - 3 : 2;
                            index = QuantizeRgb(ParseInt(sub[o]), ParseInt(sub[o + 1]), ParseInt(sub[o + 2]));
                        }
                        if (head != "58" && index >= 0) _params.Add(head + ";5;" + index);
                    }
                    else if (head == "4")
                    {
                        _params.Add(sub.Length > 1 && sub[1] == "0" ? "24" : "4");
                    }
                    else
                    {
                        _params.Add(head);
                    }
                    continue;
                }

                if (token == "38" || token == "48" || token == "58")
                {
                    string mode = i + 1 < tokens.Length ? tokens[i + 1] : null;
                    int index = -1;
                    if (mode == "5" && i + 2 < tokens.Length)
                    {
                        index = ParseInt(tokens[i + 2]);
                        i += 2;
                    }
                    else if (mode == "2" && i + 4 < tokens.Length)
                    {
                        index = QuantizeRgb(ParseInt(tokens[i + 2]), ParseInt(tokens[i + 3]), ParseInt(tokens[i + 4]));
                        i += 4;
                    }
                    else
                    {
                        // Malformed extended color: drop the introducer and whatever follows it.
                        i = tokens.Length;
                    }
                    if (token != "58" && index >= 0) _params.Add(token + ";5;" + index);
                    continue;
                }

                _params.Add(token);
            }

            return _params.Count == 0 ? null : string.Join(";", _params);
        }

        private static int ParseInt(string s)
        {
            return int.TryParse(s, out int v) ? Math.Max(0, Math.Min(255, v)) : 0;
        }

        /// <summary>Maps an RGB color to the nearest entry of the xterm 256-color cube or grayscale ramp.</summary>
        internal static int QuantizeRgb(int r, int g, int b)
        {
            int cr = CubeIndex(r), cg = CubeIndex(g), cb = CubeIndex(b);
            int cubeIndex = 16 + 36 * cr + 6 * cg + cb;
            int cubeDist = Distance(r, g, b, CubeLevel(cr), CubeLevel(cg), CubeLevel(cb));

            int average = (r + g + b) / 3;
            int grayStep = Math.Max(0, Math.Min(23, (average - 3) / 10));
            int grayLevel = 8 + grayStep * 10;
            int grayDist = Distance(r, g, b, grayLevel, grayLevel, grayLevel);

            return grayDist < cubeDist ? 232 + grayStep : cubeIndex;
        }

        private static int CubeIndex(int v) => v < 48 ? 0 : v < 115 ? 1 : (v - 35) / 40;
        private static int CubeLevel(int i) => i == 0 ? 0 : 55 + i * 40;

        private static int Distance(int r1, int g1, int b1, int r2, int g2, int b2)
        {
            int dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
            return dr * dr * 3 + dg * dg * 4 + db * db * 2;
        }

        private void Emit(byte b) => _output[_outputLength++] = b;

        private void Emit(byte[] bytes, int count)
        {
            Buffer.BlockCopy(bytes, 0, _output, _outputLength, count);
            _outputLength += count;
        }

        private void EnsureCapacity(int size)
        {
            if (_output.Length < size) _output = new byte[Math.Max(size, _output.Length * 2)];
        }
    }
}
