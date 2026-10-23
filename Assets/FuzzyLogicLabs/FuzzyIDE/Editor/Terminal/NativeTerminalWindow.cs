using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using XtermSharp;
using Pty.Net;
using FuzzyLogicLabs.FuzzyIDE.Editor.Terminal.Rendering;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Terminal
{
    [InitializeOnLoad]
    public class NativeTerminalWindow : EditorWindow
    {
        static NativeTerminalWindow()
        {
            EditorApplication.quitting -= StaticCleanupAllSessions;
            EditorApplication.quitting += StaticCleanupAllSessions;
        }

        private VisualElement _viewport;
        private TerminalGridView _grid;
        private Scroller _scroller;
        private ScrollView _tabScroll;
        private Button _btnNewTab;
        private Button _btnClearTab;
        private Button _btnRestartTab;
        private Label _statusShellInfo;
        private Label _statusSizeInfo;
        
        private bool _isRunning;
        private string _historyDir;

        private TerminalFonts _fonts;
        private int _fontSize = 13;
        private float _charWidth = 7.85f;
        private float _charHeight = 18f;
        private double _lastKeyPressTime = 0;

        // Text Selection state
        private bool _isSelecting = false;
        private int _selStartCol = -1, _selStartRow = -1;
        private int _selEndCol = -1, _selEndRow = -1;
        private double _lastClickTime = 0;
        private int _clickCount = 0;
        // Pointer position while drag-selecting, in grid space and unclamped, for auto-scrolling past the edges.
        private float _selDragLocalY = float.NaN;
        private int _selDragCol;
        private double _lastAutoScrollTime;
        private double _lastReturnTime = 0;

        private double _lastRenderTime = 0;

        private const int ScrollbackLines = 10000;

        // XtermSharp only follows output when this is false; it is set by Terminal.ScrollLines but not by
        // writes to Buffer.YDisp, so every viewport change goes through SetViewportTop.
        private static readonly FieldInfo _userScrollingField =
            typeof(XtermSharp.Terminal).GetField("userScrolling", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo _scrollbackField =
            typeof(TerminalOptions).GetField("<Scrollback>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>Moves the viewport and keeps XtermSharp's follow-output state consistent. Call under lock(term).</summary>
        private static void SetViewportTop(XtermSharp.Terminal term, int yDisp)
        {
            var buffer = term.Buffer;
            buffer.YDisp = Mathf.Clamp(yDisp, 0, buffer.YBase);
            try { _userScrollingField?.SetValue(term, buffer.YDisp < buffer.YBase); } catch { }
        }

        private string GetSnapshotPath(int tabId) => Path.Combine(_historyDir, $"Tab_{tabId}.snapshot");

        private class TabSession
        {
            public int Id;
            public string ShellName;
            public string ShellApp;
            public XtermSharp.Terminal Term;
            public VisualElement UIButton;
            public Button TitleButton;
            public volatile bool IsDirty = true;
            public volatile string PendingTitle = null;
            public bool IsCustomTitle = false;
            
            public IPtyConnection Pty; 
            public Stream ReaderStream;
            public Stream WriterStream;
            public Thread ReadThread;
            
            public long ReadHandle;
            public long WriteHandle;
            public long PtyHandle;
            public int Pid;
            public IntPtr JobHandle;
            public volatile bool IsClosing;
            
            public readonly VtStreamFilter Filter = new VtStreamFilter();
            public readonly SyncOutputFilter Sync = new SyncOutputFilter();
            public readonly object WriteLock = new object();
            public volatile PtyRecorder Recorder;
        }

        private Dictionary<int, TabSession> _sessions = new Dictionary<int, TabSession>();
        private int _activeTabId = -1;
        private int _nextTabId = 1;

        #region Windows Job Object & Process Tree Interop
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryLimit;
            public UIntPtr PeakJobMemoryLimit;
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        [DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        // Pty.Net creates pseudo consoles with the bundled os64\conpty.dll (see FixConPtyPath), so handles
        // kept across domain reloads must be resized/closed by that same library, not kernel32's copy.
        [DllImport("os64\\conpty.dll", EntryPoint = "ResizePseudoConsole", SetLastError = true)]
        private static extern int ConPtyResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("os64\\conpty.dll", EntryPoint = "ClosePseudoConsole", SetLastError = true)]
        private static extern void ConPtyClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", EntryPoint = "ResizePseudoConsole", SetLastError = true)]
        private static extern int Kernel32ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", EntryPoint = "ClosePseudoConsole", SetLastError = true)]
        private static extern void Kernel32ClosePseudoConsole(IntPtr hPC);

        private static int ResizePseudoConsole(IntPtr hPC, COORD size)
        {
            try { return ConPtyResizePseudoConsole(hPC, size); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return Kernel32ResizePseudoConsole(hPC, size);
        }

        private static void ClosePseudoConsole(IntPtr hPC)
        {
            try { ConPtyClosePseudoConsole(hPC); return; }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            Kernel32ClosePseudoConsole(hPC);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(
            IntPtr hSourceProcessHandle,
            IntPtr hSourceHandle,
            IntPtr hTargetProcessHandle,
            out IntPtr lpTargetHandle,
            uint dwDesiredAccess,
            bool bInheritHandle,
            uint dwOptions);

        private const uint DUPLICATE_SAME_ACCESS = 0x00000002;

        private static IntPtr CreateKillOnCloseJob()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) return IntPtr.Zero;
            try
            {
                IntPtr hJob = CreateJobObject(IntPtr.Zero, null);
                if (hJob == IntPtr.Zero) return IntPtr.Zero;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

                int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr pInfo = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, pInfo, false);
                    if (!SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, pInfo, (uint)length))
                    {
                        CloseHandle(hJob);
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pInfo);
                }
                return hJob;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static void AssignPidToJob(IntPtr hJob, int pid)
        {
            if (hJob == IntPtr.Zero || pid <= 0) return;
            try
            {
                var proc = Process.GetProcessById(pid);
                if (proc != null && !proc.HasExited)
                {
                    AssignProcessToJobObject(hJob, proc.Handle);
                }
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        private static void KillProcessTree(int rootPid)
        {
            if (rootPid <= 0) return;

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = $"/PID {rootPid} /T /F",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (var p = Process.Start(psi))
                    {
                        p?.WaitForExit(800);
                    }
                }
                catch { }

                try
                {
                    KillChildProcessesToolhelp(rootPid);
                }
                catch { }
            }
            else
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "pkill",
                        Arguments = $"-P {rootPid}",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using (var p = Process.Start(psi))
                    {
                        p?.WaitForExit(500);
                    }
                }
                catch { }
            }

            try
            {
                var proc = Process.GetProcessById(rootPid);
                if (proc != null && !proc.HasExited)
                {
                    proc.Kill();
                }
            }
            catch { }
        }

        private static void KillChildProcessesToolhelp(int parentPid)
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS = 2
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return;

            try
            {
                var children = new List<int>();
                PROCESSENTRY32 pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

                if (Process32First(snapshot, ref pe))
                {
                    do
                    {
                        if (pe.th32ParentProcessID == (uint)parentPid)
                        {
                            children.Add((int)pe.th32ProcessID);
                        }
                    } while (Process32Next(snapshot, ref pe));
                }

                foreach (int childPid in children)
                {
                    KillChildProcessesToolhelp(childPid);
                    try
                    {
                        var childProc = Process.GetProcessById(childPid);
                        if (childProc != null && !childProc.HasExited)
                        {
                            childProc.Kill();
                        }
                    }
                    catch { }
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }
        #endregion

        [MenuItem("Window/Fuzzy Logic Labs/Fuzzy IDE/Native Terminal", false, 30)]
        public static void ShowWindow()
        {
            var window = GetWindow<NativeTerminalWindow>();
            window.titleContent = new GUIContent("Terminal");
            window.Show();
        }

        public static void ExecuteCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            var window = GetWindow<NativeTerminalWindow>();
            if (window == null) return;
            window.Show();
            window.Focus();
            string cmdWithEnter = command.EndsWith("\n") ? command : command + "\r\n";
            window.WriteToActivePty(cmdWithEnter);
        }


        private static string[] Init256Palette()
        {
            string[] palette = new string[256];
            // Modern VS Code / Campbell high-contrast palette
            string[] basic16 = {
                "#181818", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#d4d4d4",
                "#7f848e", "#f07178", "#a6e22e", "#ffe66d", "#82aaff", "#c792ea", "#7fdbca", "#ffffff"
            };
            for (int i = 0; i < 16; i++) palette[i] = basic16[i];

            // 16-231: 6x6x6 color cube
            int[] steps = { 0x00, 0x5f, 0x87, 0xaf, 0xd7, 0xff };
            int idx = 16;
            for (int r = 0; r < 6; r++)
            {
                for (int g = 0; g < 6; g++)
                {
                    for (int b = 0; b < 6; b++)
                    {
                        palette[idx++] = $"#{steps[r]:X2}{steps[g]:X2}{steps[b]:X2}";
                    }
                }
            }

            // 232-255: Grayscale ramp
            for (int i = 0; i < 24; i++)
            {
                int gray = 8 + i * 10;
                palette[232 + i] = $"#{gray:X2}{gray:X2}{gray:X2}";
            }
            return palette;
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Terminal");
            _historyDir = Path.Combine(Application.temporaryCachePath, "FuzzyTerminal");
            if (!Directory.Exists(_historyDir)) Directory.CreateDirectory(_historyDir);
            // Raw output logs from older versions; replaced by screen snapshots.
            try { foreach (var log in Directory.GetFiles(_historyDir, "Tab_*.log")) File.Delete(log); } catch { }

            FixConPtyPath();
            EditorApplication.quitting -= StaticCleanupAllSessions;
            EditorApplication.quitting += StaticCleanupAllSessions;
        }

        private void CreateGUI()
        {
            InitFont();
            InitUI();
            RestoreTabs();
        }

        private void OnDisable()
        {
            _isRunning = false;
            SaveTabs();
            SaveSnapshots();
            EditorApplication.update -= RenderTerminal;
        }

        /// <summary>
        /// Writes each tab's screen to disk so ReattachTab can restore it after a domain reload.
        /// Reader threads are stopped first so no output is consumed after the snapshot is taken;
        /// anything still in the pipe is read by the reattached session.
        /// </summary>
        private void SaveSnapshots()
        {
            if (_sessions == null || string.IsNullOrEmpty(_historyDir)) return;
            foreach (var session in _sessions.Values)
            {
                try { session.ReadThread?.Join(250); } catch { }
            }
            foreach (var session in _sessions.Values)
            {
                if (session.Term == null || session.IsClosing) continue;
                try
                {
                    lock (session.Term)
                    {
                        TerminalSnapshot.Save(GetSnapshotPath(session.Id), session.Term);
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[FuzzyIDE Terminal] Could not save screen of tab {session.Id}: {ex.Message}");
                }
            }
        }

        private void OnDestroy()
        {
            _isRunning = false;
            SaveTabs();
        }

        private static void StaticCleanupAllSessions()
        {
            int currentPid = Process.GetCurrentProcess().Id;
            int savedPid = SessionState.GetInt("TerminalTabs_Pid", 0);
            string savedTabs = SessionState.GetString("TerminalTabs", "");

            if (!string.IsNullOrEmpty(savedTabs) && savedPid == currentPid)
            {
                string[] tabs = savedTabs.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var tabStr in tabs)
                {
                    var parts = tabStr.Split(',');
                    if (parts.Length >= 4 && 
                        int.TryParse(parts[0], out int id) &&
                        long.TryParse(parts[1], out long readHandle) &&
                        long.TryParse(parts[2], out long writeHandle) &&
                        int.TryParse(parts[3], out int pid))
                    {
                        long ptyHandle = parts.Length >= 5 && long.TryParse(parts[4], out long ph) ? ph : 0;
                        long jobHandle = parts.Length >= 8 && long.TryParse(parts[7], out long jh) ? jh : 0;

                        if (pid != 0) KillProcessTree(pid);
                        if (jobHandle != 0 && Application.platform == RuntimePlatform.WindowsEditor)
                        {
                            try { CloseHandle(new IntPtr(jobHandle)); } catch { }
                        }
                        if (ptyHandle != 0 && Application.platform == RuntimePlatform.WindowsEditor)
                        {
                            try { ClosePseudoConsole(new IntPtr(ptyHandle)); } catch { }
                        }
                        if (readHandle != 0 && Application.platform == RuntimePlatform.WindowsEditor)
                        {
                            try { CloseHandle(new IntPtr(readHandle)); } catch { }
                        }
                        if (writeHandle != 0 && Application.platform == RuntimePlatform.WindowsEditor)
                        {
                            try { CloseHandle(new IntPtr(writeHandle)); } catch { }
                        }
                    }
                }
            }

            SessionState.SetString("TerminalTabs", "");
            SessionState.SetInt("TerminalTabs_Pid", 0);
            SessionState.SetInt("TerminalTabs_ActiveId", -1);
            EditorPrefs.SetString("FuzzyTerminal_ActiveTabs", "");
            EditorPrefs.SetInt("FuzzyTerminal_ActiveTabId", -1);
        }
        
        [DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
        
        private void FixConPtyPath()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                string sourcePath = Path.GetFullPath("Assets/FuzzyLogicLabs/FuzzyIDE/Editor/Terminal/Libs/Assemblies");
                string tempPath = Path.Combine(Application.temporaryCachePath, "FuzzyTerminalNative");
                
                if (!Directory.Exists(tempPath))
                {
                    Directory.CreateDirectory(tempPath);
                }

                // Copy native DLLs to temp path to prevent Unity from locking them in the Assets folder
                // This allows the user to cleanly delete the package folder without "file in use" errors.
                string winptySrc = Path.Combine(sourcePath, "winpty.dll");
                string winptyDest = Path.Combine(tempPath, "winpty.dll");
                if (File.Exists(winptySrc))
                {
                    try { File.Copy(winptySrc, winptyDest, true); } catch { }
                }

                string os64SrcDir = Path.Combine(sourcePath, "os64");
                string os64DestDir = Path.Combine(tempPath, "os64");
                if (Directory.Exists(os64SrcDir))
                {
                    if (!Directory.Exists(os64DestDir)) Directory.CreateDirectory(os64DestDir);
                    // conpty.dll starts the OpenConsole.exe next to it (the modern ConPTY host). Without it, it
                    // falls back to the system conhost.exe, which repaints the whole screen after every resize
                    // and moves the prompt. The package doesn't ship the exe (see ConPtyHost), so a local
                    // os64\OpenConsole.exe is used when present, otherwise Windows Terminal's copy. A copy fails
                    // while the file is in use (DLL loaded, host running); the next editor start picks it up.
                    string hostSrc = Path.Combine(os64SrcDir, "OpenConsole.exe");
                    if (!File.Exists(hostSrc)) hostSrc = ConPtyHost.FindWindowsTerminalHost();

                    var copies = new[]
                    {
                        (src: Path.Combine(os64SrcDir, "conpty.dll"), dest: Path.Combine(os64DestDir, "conpty.dll")),
                        (src: hostSrc, dest: Path.Combine(os64DestDir, "OpenConsole.exe")),
                    };
                    foreach (var (src, dest) in copies)
                    {
                        if (src == null || !File.Exists(src)) continue;
                        try
                        {
                            if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(src).Length ||
                                File.GetLastWriteTimeUtc(dest) < File.GetLastWriteTimeUtc(src))
                            {
                                File.Copy(src, dest, true);
                            }
                        }
                        catch { }
                    }
                }

                SetDllDirectory(tempPath);
            }
        }

        private void InitFont()
        {
            _fontSize = EditorPrefs.GetInt("FuzzyTerminal_FontSize", 13);
            TerminalPalette.Apply((TerminalTheme)EditorPrefs.GetInt("FuzzyTerminal_Theme", (int)TerminalTheme.Campbell));
            MeasureFont();
        }

        private static bool CrispText
        {
            get => EditorPrefs.GetBool("FuzzyTerminal_CrispText", true);
            set => EditorPrefs.SetBool("FuzzyTerminal_CrispText", value);
        }

        /// <summary>Builds the font set for the current size; cell metrics come from the same font assets the grid draws with.</summary>
        private void MeasureFont()
        {
            _fonts = TerminalFonts.Create(_fontSize, rootVisualElement, CrispText);
            _charWidth = _fonts.CellWidth;
            _charHeight = _fonts.CellHeight;
        }

        private void SetCrispText(bool crisp)
        {
            CrispText = crisp;
            ApplyFontSize();
        }

        private void SetTheme(TerminalTheme theme)
        {
            EditorPrefs.SetInt("FuzzyTerminal_Theme", (int)theme);
            TerminalPalette.Apply(theme);
            ApplyThemeToUI();
        }

        private static uint ToRgb(Color32 c) => ((uint)c.r << 16) | ((uint)c.g << 8) | c.b;

        /// <summary>Paints the areas around and behind the grid in the theme's background color.</summary>
        private void ApplyThemeToUI()
        {
            VtStreamFilter.ForegroundRgb = ToRgb(TerminalPalette.DefaultForeground);
            VtStreamFilter.BackgroundRgb = ToRgb(TerminalPalette.DefaultBackground);
            VtStreamFilter.CursorRgb = ToRgb(TerminalPalette.CursorBackground);

            var background = (UnityEngine.Color)TerminalPalette.DefaultBackground;
            foreach (string name in new[] { "terminal-container", "terminal-body", "terminal-viewport" })
            {
                var element = rootVisualElement.Q<VisualElement>(name);
                if (element != null) element.style.backgroundColor = background;
            }
            if (_grid != null)
            {
                _grid.style.backgroundColor = background;
                _grid.MarkAllDirty();
            }
            if (_scroller != null) _scroller.style.backgroundColor = background;
            _lastRenderTime = 0;
        }

        private void ApplyFontSize()
        {
            MeasureFont();
            if (_grid != null) _grid.SetFonts(_fonts);
            UpdateRowsIfNeeded();
        }

        private void ChangeFontSize(int delta)
        {
            int newSize = Mathf.Clamp(_fontSize + delta, 9, 24);
            if (newSize != _fontSize)
            {
                _fontSize = newSize;
                EditorPrefs.SetInt("FuzzyTerminal_FontSize", _fontSize);
                ApplyFontSize();
            }
        }

        private void ResetFontSize()
        {
            if (_fontSize != 13)
            {
                _fontSize = 13;
                EditorPrefs.SetInt("FuzzyTerminal_FontSize", _fontSize);
                ApplyFontSize();
            }
        }

        private const int MaxRows = 500;
        private int _currentCols = -1;
        private int _currentRows = -1;
        private int _pendingResizeCols = -1;
        private int _pendingResizeRows = -1;
        private double _lastResizeRequestTime = 0;

        private void InitUI()
        {
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/FuzzyLogicLabs/FuzzyIDE/Editor/Terminal/TerminalView.uxml");
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/FuzzyLogicLabs/FuzzyIDE/Editor/Terminal/TerminalView.uss");

            if (visualTree != null)
            {
                rootVisualElement.Clear();
                visualTree.CloneTree(rootVisualElement);
                if (styleSheet != null) rootVisualElement.styleSheets.Add(styleSheet);

                _viewport = rootVisualElement.Q<VisualElement>("terminal-viewport");
                _scroller = rootVisualElement.Q<Scroller>("terminal-scroller");
                _tabScroll = rootVisualElement.Q<ScrollView>("tab-scroll");
                _btnNewTab = rootVisualElement.Q<Button>("btn-new-tab");
                _btnClearTab = rootVisualElement.Q<Button>("btn-clear-tab");
                _btnRestartTab = rootVisualElement.Q<Button>("btn-restart-tab");
                _statusShellInfo = rootVisualElement.Q<Label>("status-shell-info");
                _statusSizeInfo = rootVisualElement.Q<Label>("status-size-info");

                if (_btnNewTab != null)
                {
                    _btnNewTab.focusable = false;
                    _btnNewTab.clicked += ShowShellSelectorMenu;
                }

                if (_btnClearTab != null)
                {
                    _btnClearTab.focusable = false;
                    _btnClearTab.clicked += ClearActiveTerminal;
                }

                if (_btnRestartTab != null)
                {
                    _btnRestartTab.focusable = false;
                    _btnRestartTab.clicked += RestartActiveTab;
                }

                if (_scroller != null)
                {
                    _scroller.focusable = false;
                    _scroller.valueChanged += OnScrollbarValueChanged;
                    _scroller.style.visibility = Visibility.Hidden;
                }

                rootVisualElement.focusable = true;
                rootVisualElement.RegisterCallback<KeyDownEvent>(OnTerminalKeyDown, TrickleDown.TrickleDown);
                rootVisualElement.RegisterCallback<PointerDownEvent>(e => rootVisualElement.Focus());
                rootVisualElement.RegisterCallback<FocusInEvent>(e => OnTerminalFocusChanged(true));
                rootVisualElement.RegisterCallback<FocusOutEvent>(e => OnTerminalFocusChanged(false));

                if (_viewport != null)
                {
                    _viewport.Clear();
                    _grid = new TerminalGridView();
                    _grid.SetFonts(_fonts);
                    _viewport.Add(_grid);
                    _viewport.RegisterCallback<WheelEvent>(OnScrollWheel, TrickleDown.TrickleDown);

                    _grid.RegisterCallback<GeometryChangedEvent>(OnTerminalGeometryChanged);
                    _grid.RegisterCallback<PointerDownEvent>(OnPointerDown);
                    _grid.RegisterCallback<PointerMoveEvent>(OnPointerMove);
                    _grid.RegisterCallback<PointerUpEvent>(OnPointerUp);

                    _currentRows = GetActiveRows();
                    _currentCols = GetActiveCols();
                    _grid.SetRowCount(_currentRows);
                }

                ApplyThemeToUI();
            }
        }

        private string FindExecutableInPath(string exeName)
        {
            if (File.Exists(exeName)) return Path.GetFullPath(exeName);
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv)) return null;

            char sep = Application.platform == RuntimePlatform.WindowsEditor ? ';' : ':';
            string[] paths = pathEnv.Split(sep);
            foreach (string p in paths)
            {
                try
                {
                    string full = Path.Combine(p.Trim(), exeName);
                    if (File.Exists(full)) return full;
                    if (Application.platform == RuntimePlatform.WindowsEditor && !exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        string fullExe = full + ".exe";
                        if (File.Exists(fullExe)) return fullExe;
                    }
                }
                catch { }
            }
            return null;
        }

        private string GetDefaultShellApp(out string defaultName)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                string pwsh = FindExecutableInPath("pwsh.exe") ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
                if (File.Exists(pwsh)) { defaultName = "PowerShell 7"; return pwsh; }
                defaultName = "PowerShell";
                return "powershell.exe";
            }
            else if (Application.platform == RuntimePlatform.OSXEditor)
            {
                string shell = Environment.GetEnvironmentVariable("SHELL");
                if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) shell = "/bin/zsh";
                if (!File.Exists(shell)) shell = "/bin/bash";
                defaultName = Path.GetFileName(shell);
                return shell;
            }
            else // Linux
            {
                string shell = Environment.GetEnvironmentVariable("SHELL");
                if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) shell = "/bin/bash";
                if (!File.Exists(shell)) shell = "/bin/sh";
                defaultName = Path.GetFileName(shell);
                return shell;
            }
        }

        private void ShowShellSelectorMenu()
        {
            GenericMenu menu = new GenericMenu();

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                string pwsh = FindExecutableInPath("pwsh.exe") ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
                if (File.Exists(pwsh))
                {
                    menu.AddItem(new GUIContent("PowerShell 7 (Core)"), false, () => SpawnTab(pwsh, "PowerShell 7"));
                }

                menu.AddItem(new GUIContent("Windows PowerShell"), false, () => SpawnTab("powershell.exe", "PowerShell"));
                menu.AddItem(new GUIContent("Command Prompt (cmd.exe)"), false, () => SpawnTab("cmd.exe", "CMD"));

                string gitBash = GetGitBashPath();
                if (!string.IsNullOrEmpty(gitBash) && File.Exists(gitBash))
                {
                    menu.AddItem(new GUIContent("Git Bash"), false, () => SpawnTab(gitBash, "Git Bash"));
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent("Git Bash (Not Installed)"));
                }
            }
            else if (Application.platform == RuntimePlatform.OSXEditor)
            {
                if (File.Exists("/bin/zsh")) menu.AddItem(new GUIContent("Zsh (/bin/zsh)"), false, () => SpawnTab("/bin/zsh", "Zsh"));
                if (File.Exists("/bin/bash")) menu.AddItem(new GUIContent("Bash (/bin/bash)"), false, () => SpawnTab("/bin/bash", "Bash"));
                
                string fish = FindExecutableInPath("fish") ?? "/opt/homebrew/bin/fish";
                if (File.Exists(fish)) menu.AddItem(new GUIContent("Fish Shell"), false, () => SpawnTab(fish, "Fish"));

                string pwsh = FindExecutableInPath("pwsh");
                if (!string.IsNullOrEmpty(pwsh)) menu.AddItem(new GUIContent("PowerShell Core (pwsh)"), false, () => SpawnTab(pwsh, "PowerShell"));
            }
            else // Linux
            {
                if (File.Exists("/bin/bash")) menu.AddItem(new GUIContent("Bash (/bin/bash)"), false, () => SpawnTab("/bin/bash", "Bash"));
                if (File.Exists("/bin/zsh")) menu.AddItem(new GUIContent("Zsh (/bin/zsh)"), false, () => SpawnTab("/bin/zsh", "Zsh"));
                if (File.Exists("/bin/sh")) menu.AddItem(new GUIContent("Sh (/bin/sh)"), false, () => SpawnTab("/bin/sh", "Sh"));
                
                string fish = FindExecutableInPath("fish");
                if (!string.IsNullOrEmpty(fish)) menu.AddItem(new GUIContent("Fish Shell"), false, () => SpawnTab(fish, "Fish"));
            }

            menu.ShowAsContext();
        }

        private void ShowTabContextMenu(int tabId)
        {
            if (!_sessions.TryGetValue(tabId, out var session)) return;
            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("Rename Tab..."), false, () => PromptRenameTab(session));
            menu.AddItem(new GUIContent("Duplicate Tab"), false, () => SpawnTab(session.ShellApp, session.ShellName));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Close Tab"), false, () => CloseTab(tabId));
            menu.ShowAsContext();
        }

        private void PromptRenameTab(TabSession session)
        {
            RenameTabPopup.ShowPopup(session.TitleButton.text, (newTitle) =>
            {
                if (!string.IsNullOrEmpty(newTitle) && session.TitleButton != null)
                {
                    session.TitleButton.text = newTitle;
                    session.PendingTitle = newTitle;
                    session.IsCustomTitle = true;
                    session.IsDirty = true;
                    SaveTabs();
                }
            });
        }

        private string GetGitBashPath()
        {
            string path = @"C:\Program Files\Git\bin\bash.exe";
            if (File.Exists(path)) return path;
            path = @"C:\Program Files (x86)\Git\bin\bash.exe";
            if (File.Exists(path)) return path;
            path = FindExecutableInPath("bash.exe");
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            return null;
        }

        private void RestoreTabs()
        {
            _isRunning = true;
            EditorApplication.update -= RenderTerminal;
            EditorApplication.update += RenderTerminal;
            
            int currentPid = Process.GetCurrentProcess().Id;
            int savedPid = SessionState.GetInt("TerminalTabs_Pid", 0);
            string savedTabs = SessionState.GetString("TerminalTabs", "");
            int savedActiveId = SessionState.GetInt("TerminalTabs_ActiveId", -1);
            int savedNextId = SessionState.GetInt("TerminalTabs_NextId", 1);
            if (savedNextId > _nextTabId) _nextTabId = savedNextId;

            bool restoredFromSession = false;

            // 1. Domain Reload recovery: handles are valid ONLY within the same Unity process
            if (!string.IsNullOrEmpty(savedTabs) && savedPid == currentPid)
            {
                string[] tabs = savedTabs.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var tabStr in tabs)
                {
                    var parts = tabStr.Split(',');
                    if (parts.Length >= 4 && 
                        int.TryParse(parts[0], out int id) &&
                        long.TryParse(parts[1], out long readHandle) &&
                        long.TryParse(parts[2], out long writeHandle) &&
                        int.TryParse(parts[3], out int pid))
                    {
                        long ptyHandle = parts.Length >= 5 ? (long.TryParse(parts[4], out long ph) ? ph : 0) : 0;
                        string shellName = parts.Length >= 6 && !string.IsNullOrEmpty(parts[5]) ? parts[5] : "PowerShell";
                        string tabTitle = parts.Length >= 7 && !string.IsNullOrEmpty(parts[6]) ? parts[6] : null;
                        long jobHandle = parts.Length >= 8 ? (long.TryParse(parts[7], out long jh) ? jh : 0) : 0;
                        string shellApp = parts.Length >= 9 && !string.IsNullOrEmpty(parts[8]) ? parts[8] : null;
                        bool bracketedPaste = parts.Length >= 10 && parts[9] == "1";
                        bool isCustomTitle = parts.Length >= 11 && parts[10] == "1";

                        if (id >= _nextTabId) _nextTabId = id + 1;
                        
                        try 
                        {
                            var proc = Process.GetProcessById(pid);
                            if (proc != null && !proc.HasExited)
                            {
                                ReattachTab(id, readHandle, writeHandle, pid, ptyHandle, shellName, tabTitle, jobHandle, shellApp, bracketedPaste, isCustomTitle);
                                restoredFromSession = true;
                                continue;
                            }
                        }
                        catch { }

                        // Process was killed externally; spawn a clean replacement tab
                        int newId = SpawnTab(shellApp, shellName);
                        if (!string.IsNullOrEmpty(tabTitle) && _sessions.TryGetValue(newId, out var s) && s.TitleButton != null)
                        {
                            s.TitleButton.text = tabTitle;
                            s.PendingTitle = tabTitle;
                            s.IsCustomTitle = isCustomTitle;
                        }
                        restoredFromSession = true;
                    }
                }
            }

            // 2. Fresh Unity Editor run (or previous run crashed/closed).
            // Any old handles are completely invalid. Restore tabs cleanly by launching fresh shells.
            if (!restoredFromSession)
            {
                SessionState.SetString("TerminalTabs", "");
                SessionState.SetInt("TerminalTabs_Pid", 0);

                string savedPrefs = EditorPrefs.GetString("FuzzyTerminal_ActiveTabs", "");
                int prefsActiveId = EditorPrefs.GetInt("FuzzyTerminal_ActiveTabId", -1);
                if (savedActiveId == -1) savedActiveId = prefsActiveId;

                if (!string.IsNullOrEmpty(savedPrefs))
                {
                    string[] tabs = savedPrefs.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var tabStr in tabs)
                    {
                        var parts = tabStr.Split(',');
                        if (parts.Length >= 2)
                        {
                            string shellName = parts[1];
                            string shellApp = parts.Length >= 3 ? parts[2] : null;
                            string tabTitle = parts.Length >= 4 ? parts[3] : null;
                            bool isCustomTitle = parts.Length >= 5 && parts[4] == "1";

                            // Backward compatibility check
                            if (long.TryParse(shellName, out _))
                            {
                                shellName = parts.Length >= 6 && !string.IsNullOrEmpty(parts[5]) ? parts[5] : "PowerShell";
                                shellApp = null;
                            }

                            int newTabId = SpawnTab(string.IsNullOrEmpty(shellApp) ? null : shellApp, shellName);
                            if (!string.IsNullOrEmpty(tabTitle) && _sessions.TryGetValue(newTabId, out var s) && s.TitleButton != null)
                            {
                                s.TitleButton.text = tabTitle;
                                s.PendingTitle = tabTitle;
                                s.IsCustomTitle = isCustomTitle;
                            }
                        }
                    }
                }
            }
            
            if (_sessions.Count == 0) 
            {
                SpawnTab();
            }
            else if (savedActiveId != -1 && _sessions.ContainsKey(savedActiveId))
            {
                SwitchTab(savedActiveId);
            }
            else 
            {
                int firstId = -1;
                foreach (var k in _sessions.Keys) { firstId = k; break; }
                SwitchTab(firstId);
            }
        }

        private void SaveTabs()
        {
            if (_sessions == null || _sessions.Count == 0)
            {
                SessionState.SetString("TerminalTabs", "");
                SessionState.SetInt("TerminalTabs_Pid", 0);
                SessionState.SetInt("TerminalTabs_ActiveId", -1);
                SessionState.SetInt("TerminalTabs_NextId", _nextTabId);
                EditorPrefs.SetString("FuzzyTerminal_ActiveTabs", "");
                EditorPrefs.SetInt("FuzzyTerminal_ActiveTabId", -1);
                return;
            }

            List<string> sessionData = new List<string>();
            List<string> prefsData = new List<string>();
            int currentPid = Process.GetCurrentProcess().Id;

            // Preserve tab visual order if UI containers exist
            var orderedSessions = new List<TabSession>(_sessions.Values);
            if (_tabScroll != null)
            {
                orderedSessions.Sort((a, b) =>
                {
                    int idxA = a.UIButton != null ? _tabScroll.IndexOf(a.UIButton) : a.Id;
                    int idxB = b.UIButton != null ? _tabScroll.IndexOf(b.UIButton) : b.Id;
                    return idxA.CompareTo(idxB);
                });
            }

            foreach (var s in orderedSessions)
            {
                if (s.ReadHandle != 0 && s.ReaderStream != null)
                {
                    string shellName = string.IsNullOrEmpty(s.ShellName) ? "PowerShell" : s.ShellName;
                    string tabTitle = (s.TitleButton != null && !string.IsNullOrEmpty(s.TitleButton.text)) ? s.TitleButton.text : (s.PendingTitle ?? shellName);
                    string shellApp = string.IsNullOrEmpty(s.ShellApp) ? "" : s.ShellApp;
                    int bp = (s.Term != null && s.Term.BracketedPasteMode) ? 1 : 0;
                    int custom = s.IsCustomTitle ? 1 : 0;

                    // SessionState is in-memory per-process: keeps handles for domain reloads within same Unity process
                    sessionData.Add($"{s.Id},{s.ReadHandle},{s.WriteHandle},{s.Pid},{s.PtyHandle},{shellName},{tabTitle},{s.JobHandle.ToInt64()},{shellApp},{bp},{custom}");

                    // EditorPrefs persists across process restarts: store ONLY metadata (shell, title), never raw pointers/handles!
                    prefsData.Add($"{s.Id},{shellName},{shellApp},{tabTitle},{custom}");
                }
            }

            SessionState.SetString("TerminalTabs", string.Join("|", sessionData));
            SessionState.SetInt("TerminalTabs_Pid", currentPid);
            SessionState.SetInt("TerminalTabs_ActiveId", _activeTabId);
            SessionState.SetInt("TerminalTabs_NextId", _nextTabId);

            EditorPrefs.SetString("FuzzyTerminal_ActiveTabs", string.Join("|", prefsData));
            EditorPrefs.SetInt("FuzzyTerminal_ActiveTabId", _activeTabId);
        }

        private class TerminalDelegate : ITerminalDelegate
        {
            public Action<byte[]> OnSend;
            public Action<string> OnTitleChanged;
            public Func<string> GetTitle;

            public void ShowCursor(XtermSharp.Terminal source) { }
            public void SetTerminalTitle(XtermSharp.Terminal source, string title) { OnTitleChanged?.Invoke(title); }
            public void SetTerminalIconTitle(XtermSharp.Terminal source, string title) { }
            public void SizeChanged(XtermSharp.Terminal source) { }
            public void Send(byte[] data) { OnSend?.Invoke(data); }
            public string WindowCommand(XtermSharp.Terminal source, WindowManipulationCommand command, params int[] args)
            {
                return GetTitle?.Invoke() ?? "Terminal";
            }
            public bool IsProcessTrusted() { return true; }
        }

        private void CreateTabUI(int id, string shellName = "PowerShell", bool loadHistory = true, string customTitle = null, bool isCustomTitle = false)
        {
            var session = new TabSession { Id = id, ShellName = shellName, PendingTitle = customTitle, IsCustomTitle = isCustomTitle };
            var del = new TerminalDelegate { 
                OnSend = (b) => WriteToActivePty(id, b),
                GetTitle = () => session.PendingTitle ?? session.ShellName ?? "Terminal",
                OnTitleChanged = (title) => {
                    if (!string.IsNullOrEmpty(title) && !session.IsCustomTitle)
                    {
                        session.PendingTitle = title;
                        session.IsDirty = true;
                        try
                        {
                            var titleF = typeof(XtermSharp.Terminal).GetField("<Title>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                            if (titleF != null) titleF.SetValue(session.Term, title);
                            var iconF = typeof(XtermSharp.Terminal).GetField("<IconTitle>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                            if (iconF != null) iconF.SetValue(session.Term, title);
                        }
                        catch { }
                    }
                }
            };
            // Scrollback must be set before construction: the buffers size their line ring from it.
            // ConvertEol defaults to true in XtermSharp, which turns every LF into CR+LF. ConPTY moves the
            // cursor down with a bare LF and expects the column to stay, so it must be off.
            var options = new TerminalOptions { Cols = GetActiveCols(), Rows = GetActiveRows(), ConvertEol = false };
            try { _scrollbackField?.SetValue(options, (int?)ScrollbackLines); } catch { }
            session.Term = new XtermSharp.Terminal(del, options);
            // OSC strings never reach XtermSharp (see VtStreamFilter); titles arrive through the filter.
            session.Filter.TitleChanged = title => del.OnTitleChanged?.Invoke(title);
            string displayTitle = !string.IsNullOrEmpty(customTitle) ? customTitle : $"{shellName} #{id}";
            try
            {
                var titleF = typeof(XtermSharp.Terminal).GetField("<Title>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                if (titleF != null) titleF.SetValue(session.Term, displayTitle);
                var iconF = typeof(XtermSharp.Terminal).GetField("<IconTitle>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                if (iconF != null) iconF.SetValue(session.Term, displayTitle);
            }
            catch { }

            var tabContainer = new VisualElement { focusable = false };
            tabContainer.AddToClassList("terminal-tab");
            
            var btn = new Button(() => SwitchTab(id)) { text = displayTitle, focusable = false };
            btn.AddToClassList("terminal-tab-btn");
            session.TitleButton = btn;

            var closeBtn = new Button(() => CloseTab(id)) { text = "×", focusable = false };
            closeBtn.AddToClassList("terminal-tab-close");
            
            tabContainer.Add(btn);
            tabContainer.Add(closeBtn);

            tabContainer.RegisterCallback<PointerDownEvent>(e => {
                if (e.button == 2) // Middle-click (wheel click) closes tab
                {
                    CloseTab(id);
                    e.StopPropagation();
                }
                else if (e.button == 1) // Right-click tab context menu
                {
                    ShowTabContextMenu(id);
                    e.StopPropagation();
                }
            });

            btn.RegisterCallback<PointerDownEvent>(e => {
                if (e.button == 0 && e.clickCount == 2) // Double-click renames tab
                {
                    PromptRenameTab(session);
                    e.StopPropagation();
                }
            });
            
            session.UIButton = tabContainer;
            if (_tabScroll != null) _tabScroll.Add(tabContainer);
            _sessions[id] = session;

            string snapshotPath = GetSnapshotPath(id);
            if (loadHistory)
            {
                if (File.Exists(snapshotPath))
                {
                    try
                    {
                        string snapshot = File.ReadAllText(snapshotPath, Encoding.UTF8);
                        lock (session.Term)
                        {
                            TerminalSnapshot.Restore(session.Term, snapshot);
                            SetViewportTop(session.Term, session.Term.Buffer.YBase);
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[FuzzyIDE Terminal] Could not restore screen of tab {id}: {ex.Message}");
                    }
                    try { File.Delete(snapshotPath); } catch { }
                }

                // Preserve restored or custom tab title after history replay
                string finalTitle = !string.IsNullOrEmpty(customTitle) ? customTitle : displayTitle;
                session.PendingTitle = null;
                if (session.TitleButton != null)
                {
                    session.TitleButton.text = finalTitle;
                }
            }
            else
            {
                // A fresh shell must not inherit the screen of an earlier tab that used the same id.
                try { if (File.Exists(snapshotPath)) File.Delete(snapshotPath); } catch { }
            }

            SwitchTab(id);
        }

        private int GetActiveCols(float widthOverride = -1f)
        {
            // The grid fills the viewport's content box, so its width is exactly the drawable area.
            float w = widthOverride > 0 ? widthOverride : (_grid != null ? _grid.layout.width : float.NaN);
            if (float.IsNaN(w) || w < 50) w = (!float.IsNaN(position.width) && position.width > 50) ? position.width - 16f : 0;

            if (!float.IsNaN(w) && w > 50)
            {
                return Mathf.Max(20, Mathf.FloorToInt(w / _charWidth));
            }
            return EditorPrefs.GetInt("FuzzyTerminal_LastCols", 80);
        }

        private int GetActiveRows(float heightOverride = -1f)
        {
            float h = heightOverride > 0 ? heightOverride : (_grid != null ? _grid.layout.height : float.NaN);
            if (float.IsNaN(h) || h < 50) h = (!float.IsNaN(position.height) && position.height > 64f) ? (position.height - 64f) : 0;
            if (!float.IsNaN(h) && h > 50)
            {
                return Mathf.Clamp(Mathf.FloorToInt(h / _charHeight), 5, MaxRows);
            }
            return EditorPrefs.GetInt("FuzzyTerminal_LastRows", 24);
        }

        private void SwitchTab(int id)
        {
            _activeTabId = id;
            _selStartRow = _selStartCol = _selEndRow = _selEndCol = -1;

            foreach (var s in _sessions.Values)
            {
                if (s.UIButton != null)
                {
                    if (s.Id == id)
                    {
                        s.UIButton.AddToClassList("active");
                        s.IsDirty = true;
                    }
                    else s.UIButton.RemoveFromClassList("active");
                }
            }
            rootVisualElement.Focus();
        }

        private void ClearActiveTerminal()
        {
            if (_activeTabId != -1 && _sessions.TryGetValue(_activeTabId, out var session))
            {
                lock (session.Term)
                {
                    session.Term.SoftReset();
                    session.Term.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[3J\x1b[H"));
                    SetViewportTop(session.Term, session.Term.Buffer.YBase);
                }
                WriteToActivePty("\x0c"); // Ctrl+L to shell
                session.IsDirty = true;
            }
        }

        private void RestartActiveTab()
        {
            if (_activeTabId != -1 && _sessions.TryGetValue(_activeTabId, out var s))
            {
                string shellApp = s.ShellApp;
                string shellName = s.ShellName;
                CloseTab(_activeTabId, forceImmediate: true);
                SpawnTab(shellApp, shellName);
            }
        }

        private void CloseTab(int id, bool forceImmediate = false)
        {
            if (!_sessions.TryGetValue(id, out var session)) return;
            session.IsClosing = true;
            session.Recorder?.Dispose();
            session.Recorder = null;
            
            _sessions.Remove(id);
            if (session.UIButton != null && session.UIButton.parent != null)
            {
                session.UIButton.parent.Remove(session.UIButton);
            }
            
            try { File.Delete(GetSnapshotPath(id)); } catch { }

            // 1. Graceful exit attempt (allow agy / node / git to flush locks and exit cleanly)
            if (!forceImmediate && session.WriterStream != null && session.WriterStream.CanWrite)
            {
                try
                {
                    session.WriterStream.Write(new byte[] { 0x03 }, 0, 1); // SIGINT (\x03)
                    session.WriterStream.Flush();
                    Thread.Sleep(80);
                }
                catch { }
            }

            // 2. Kill the entire process tree recursively (including agy, node, conhost, pwsh, cmd, python, etc.)
            if (session.Pid != 0)
            {
                KillProcessTree(session.Pid);
            }

            // 3. Close the Windows Job Object handle (kernel-level cleanup guarantee)
            if (session.JobHandle != IntPtr.Zero)
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    try { CloseHandle(session.JobHandle); } catch { }
                }
                session.JobHandle = IntPtr.Zero;
            }

            // 4. Close PseudoConsole handle if any
            if (session.PtyHandle != 0)
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    try { ClosePseudoConsole(new IntPtr(session.PtyHandle)); } catch { }
                }
                session.PtyHandle = 0;
            }

            // 5. Close pipe handles
            if (session.ReadHandle != 0)
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    try { CloseHandle(new IntPtr(session.ReadHandle)); } catch { }
                }
                session.ReadHandle = 0;
            }
            if (session.WriteHandle != 0)
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    try { CloseHandle(new IntPtr(session.WriteHandle)); } catch { }
                }
                session.WriteHandle = 0;
            }

            // 6. Dispose PTY connection and streams
            try { session.Pty?.Dispose(); } catch { }
            try { session.ReaderStream?.Close(); } catch { }
            try { session.WriterStream?.Close(); } catch { }

            if (_activeTabId == id)
            {
                if (_sessions.Count > 0)
                {
                    int firstId = -1;
                    foreach (var k in _sessions.Keys) { firstId = k; break; }
                    SwitchTab(firstId);
                }
                else
                {
                    _activeTabId = -1;
                    _grid?.Clear();
                }
            }
            if (_sessions.Count == 0)
            {
                SessionState.SetString("TerminalTabs", "");
                SessionState.SetInt("TerminalTabs_Pid", 0);
                SessionState.SetInt("TerminalTabs_ActiveId", -1);
                EditorPrefs.SetString("FuzzyTerminal_ActiveTabs", "");
                EditorPrefs.SetInt("FuzzyTerminal_ActiveTabId", -1);
            }
            else
            {
                SaveTabs();
            }
        }

        private bool LaunchShellForSession(TabSession session, string shellApp = null, string shellName = null)
        {
            if (session == null) return false;

            if (string.IsNullOrEmpty(shellApp))
            {
                shellApp = GetDefaultShellApp(out string defName);
                if (string.IsNullOrEmpty(shellName)) shellName = defName;
            }
            if (string.IsNullOrEmpty(shellName)) shellName = Path.GetFileNameWithoutExtension(shellApp);

            session.ShellApp = shellApp;
            session.ShellName = shellName;
            if (session.TitleButton != null) session.TitleButton.text = session.PendingTitle ?? shellName;

            int cols = GetActiveCols();
            int rows = GetActiveRows();
            string cwd = Application.dataPath.Replace("/Assets", "");

            var env = new Dictionary<string, string>();
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string k = entry.Key?.ToString();
                string v = entry.Value?.ToString();
                if (!string.IsNullOrEmpty(k)) env[k] = v;
            }
            env["TERM"] = "xterm-256color";
            env["COLORTERM"] = "truecolor";
            env["FORCE_COLOR"] = "3";

            IPtyConnection pty = null;
            try
            {
                var opts = new PtyOptions { App = shellApp, Cols = cols, Rows = rows, Cwd = cwd, ForceWinPty = false, Environment = env };
                pty = PtyProvider.SpawnAsync(opts, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[FuzzyIDE Terminal] Shell '{shellApp}' failed to launch: {ex.Message}. Attempting OS default shell fallback...");
                string fallbackApp = GetDefaultShellApp(out string fallbackName);
                if (fallbackApp != shellApp)
                {
                    try
                    {
                        var opts = new PtyOptions { App = fallbackApp, Cols = cols, Rows = rows, Cwd = cwd, ForceWinPty = false, Environment = env };
                        pty = PtyProvider.SpawnAsync(opts, CancellationToken.None).GetAwaiter().GetResult();
                        session.ShellApp = fallbackApp;
                        session.ShellName = fallbackName;
                        if (session.TitleButton != null) session.TitleButton.text = session.PendingTitle ?? fallbackName;
                    }
                    catch (Exception ex2)
                    {
                        UnityEngine.Debug.LogError($"[FuzzyIDE Terminal] Fallback shell '{fallbackApp}' also failed: {ex2.Message}");
                    }
                }
            }

            if (pty == null)
            {
                lock (session.Term)
                {
                    string errMsg = $"\r\n\x1b[31m[FuzzyIDE Terminal Error]\x1b[0m Could not launch shell '{shellApp}'.\r\nPlease verify that the executable is installed and accessible in PATH.\r\n\r\n";
                    session.Term.Feed(Encoding.UTF8.GetBytes(errMsg));
                    session.IsDirty = true;
                }
                return false;
            }
            
            session.Pty = pty;
            session.Pid = pty.Pid;
            session.ReaderStream = pty.ReaderStream;
            session.WriterStream = pty.WriterStream;
            
            // Assign process to Windows Job Object for kernel-level termination guarantee
            session.JobHandle = CreateKillOnCloseJob();
            AssignPidToJob(session.JobHandle, session.Pid);

            if (!string.IsNullOrEmpty(shellApp))
            {
                EditorPrefs.SetString("FuzzyTerminal_LastShell", shellApp);
            }

            ExtractAndProtectHandles(session);
            StartReading(session);

            // Layout may not be resolved yet when a tab is first spawned (UIToolkit
            // reports 0 size until the first geometry pass). Schedule a one-frame-later
            // resize so the PTY and XtermSharp terminal learn the real dimensions before
            // the shell starts drawing (fixes the "screen shifted on first TUI open" bug).
            int launchedId = session.Id;
            EditorApplication.delayCall += () =>
            {
                if (!_isRunning || !_sessions.TryGetValue(launchedId, out var s) || s.Term == null) return;
                int cols = GetActiveCols();
                int rows = GetActiveRows();
                if (cols > 20 && rows > 5 && (cols != s.Term.Cols || rows != s.Term.Rows))
                {
                    // Emulator before PTY, see ApplyPendingResize.
                    s.Recorder?.WriteResize(cols, rows);
                    lock (s.Term)
                    {
                        ConPtyResize.Resize(s.Term, cols, rows);
                        SetViewportTop(s.Term, s.Term.Buffer.YBase);
                        s.IsDirty = true;
                    }
                    if (s.Pty != null) try { s.Pty.Resize(cols, rows); } catch { }
                    else if (s.PtyHandle != 0 && Application.platform == RuntimePlatform.WindowsEditor)
                    {
                        try { ResizePseudoConsole(new IntPtr(s.PtyHandle), new COORD { X = (short)cols, Y = (short)rows }); } catch { }
                    }
                    _lastRenderTime = 0;
                }
            };

            SaveTabs();
            return true;
        }

        private int SpawnTab(string shellApp = null, string shellName = null)
        {
            int id = _nextTabId++;
            if (string.IsNullOrEmpty(shellApp))
            {
                shellApp = GetDefaultShellApp(out string defName);
                if (string.IsNullOrEmpty(shellName)) shellName = defName;
            }
            if (string.IsNullOrEmpty(shellName)) shellName = Path.GetFileNameWithoutExtension(shellApp);

            CreateTabUI(id, shellName, loadHistory: false);
            var session = _sessions[id];
            LaunchShellForSession(session, shellApp, shellName);
            return id;
        }

        private static void SetBracketedPasteMode(XtermSharp.Terminal term, bool enabled)
        {
            if (term == null) return;
            try
            {
                var bpField = typeof(XtermSharp.Terminal).GetField("<BracketedPasteMode>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                if (bpField != null)
                {
                    bpField.SetValue(term, enabled);
                    return;
                }
                var bpProp = typeof(XtermSharp.Terminal).GetProperty("BracketedPasteMode");
                var setter = bpProp?.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(term, new object[] { enabled });
                    return;
                }
            }
            catch { }
            try
            {
                term.Feed(Encoding.UTF8.GetBytes(enabled ? "\x1b[?2004h" : "\x1b[?2004l"));
            }
            catch { }
        }

        private void ReattachTab(int id, long readHandle, long writeHandle, int pid, long ptyHandle, string shellName = "PowerShell", string title = null, long jobHandle = 0, string shellApp = null, bool bracketedPaste = false, bool isCustomTitle = false)
        {
            CreateTabUI(id, shellName, loadHistory: true, customTitle: title, isCustomTitle: isCustomTitle);
            var session = _sessions[id];
            session.Pid = pid;
            session.ShellApp = shellApp;
            session.PtyHandle = ptyHandle;
            session.JobHandle = new IntPtr(jobHandle);
            session.IsCustomTitle = isCustomTitle;

            if (!string.IsNullOrEmpty(title) && session.TitleButton != null)
            {
                session.TitleButton.text = title;
                session.PendingTitle = null;
            }

            if (bracketedPaste && session.Term != null)
            {
                SetBracketedPasteMode(session.Term, true);
            }

            lock (session.Term)
            {
                // The snapshot restores the grid at its saved size; the PTY is resized to the
                // current size below, so the emulator has to match it.
                if (_currentCols > 0 && _currentRows > 0 &&
                    (session.Term.Cols != _currentCols || session.Term.Rows != _currentRows))
                {
                    ConPtyResize.Resize(session.Term, _currentCols, _currentRows);
                }
                SetViewportTop(session.Term, session.Term.Buffer.YBase);
            }

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                IntPtr curProc = GetCurrentProcess();
                if (readHandle != 0 && DuplicateHandle(curProc, new IntPtr(readHandle), curProc, out IntPtr dupRead, 0, false, DUPLICATE_SAME_ACCESS))
                {
                    session.ReadHandle = dupRead.ToInt64();
                }
                else
                {
                    session.ReadHandle = readHandle;
                }

                if (writeHandle != 0 && DuplicateHandle(curProc, new IntPtr(writeHandle), curProc, out IntPtr dupWrite, 0, false, DUPLICATE_SAME_ACCESS))
                {
                    session.WriteHandle = dupWrite.ToInt64();
                }
                else
                {
                    session.WriteHandle = writeHandle;
                }
            }
            else
            {
                session.ReadHandle = readHandle;
                session.WriteHandle = writeHandle;
            }

            try
            {
                var safeReader = new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(session.ReadHandle), false);
                var safeWriter = new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(session.WriteHandle), false);
                
                session.ReaderStream = new FileStream(safeReader, FileAccess.Read);
                session.WriterStream = new FileStream(safeWriter, FileAccess.Write);
                
                ProtectAllSafeHandles(safeReader);
                ProtectAllSafeHandles(safeWriter);
                ProtectAllSafeHandles(session.ReaderStream);
                ProtectAllSafeHandles(session.WriterStream);

                StartReading(session);

                if (session.PtyHandle != 0 && Application.platform == RuntimePlatform.WindowsEditor)
                {
                    try
                    {
                        ResizePseudoConsole(new IntPtr(session.PtyHandle), new COORD { X = (short)_currentCols, Y = (short)_currentRows });
                    }
                    catch { }
                }
                session.IsDirty = true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[FuzzyIDE Terminal] Failed to reattach to tab {id}: {ex.Message}. Recovering with fresh shell...");
                session.PtyHandle = 0;
                session.ReadHandle = 0;
                session.WriteHandle = 0;
                session.Pid = 0;
                session.ReaderStream = null;
                session.WriterStream = null;
                session.Pty = null;

                if (!LaunchShellForSession(session, shellApp, shellName))
                {
                    CloseTab(id);
                }
            }
        }

        private void ExtractAndProtectHandles(TabSession session)
        {
            IntPtr rawRead = GetHandle(session.ReaderStream);
            IntPtr rawWrite = GetHandle(session.WriterStream);

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                IntPtr curProc = GetCurrentProcess();
                if (rawRead != IntPtr.Zero && DuplicateHandle(curProc, rawRead, curProc, out IntPtr dupRead, 0, false, DUPLICATE_SAME_ACCESS))
                {
                    session.ReadHandle = dupRead.ToInt64();
                }
                else
                {
                    session.ReadHandle = rawRead.ToInt64();
                }

                if (rawWrite != IntPtr.Zero && DuplicateHandle(curProc, rawWrite, curProc, out IntPtr dupWrite, 0, false, DUPLICATE_SAME_ACCESS))
                {
                    session.WriteHandle = dupWrite.ToInt64();
                }
                else
                {
                    session.WriteHandle = rawWrite.ToInt64();
                }
            }
            else
            {
                session.ReadHandle = rawRead.ToInt64();
                session.WriteHandle = rawWrite.ToInt64();
            }
            
            if (session.Pty != null)
            {
                session.PtyHandle = FindPseudoConsoleHandle(session.Pty, new HashSet<object>(), 0);
            }
            
            ProtectAllSafeHandles(session.Pty);
            ProtectAllSafeHandles(session.ReaderStream);
            ProtectAllSafeHandles(session.WriterStream);
        }

        private long FindPseudoConsoleHandle(object obj, HashSet<object> visited, int depth)
        {
            if (obj == null || depth > 6 || !visited.Add(obj)) return 0;
            
            if (obj is SafeHandle sh && !sh.IsInvalid && !sh.IsClosed)
            {
                if (sh.GetType().Name.IndexOf("PseudoConsole", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return sh.DangerousGetHandle().ToInt64();
                }
                return 0;
            }

            var type = obj.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(IntPtr) || type == typeof(UIntPtr)) return 0;

            var curType = type;
            while (curType != null && curType != typeof(object))
            {
                foreach (var field in curType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    object val = null;
                    try { val = field.GetValue(obj); } catch { }
                    if (val == null) continue;

                    if (val is SafeHandle handle && !handle.IsInvalid && !handle.IsClosed)
                    {
                        if (handle.GetType().Name.IndexOf("PseudoConsole", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return handle.DangerousGetHandle().ToInt64();
                        }
                        continue;
                    }
                    
                    long result = FindPseudoConsoleHandle(val, visited, depth + 1);
                    if (result != 0) return result;
                }
                curType = curType.BaseType;
            }
            return 0;
        }

        private void ProtectAllSafeHandles(object obj, HashSet<object> visited = null)
        {
            if (obj == null) return;
            if (visited == null) visited = new HashSet<object>();
            if (!visited.Add(obj)) return;

            try { GC.SuppressFinalize(obj); } catch { }

            if (obj is SafeHandle sh && !sh.IsInvalid && !sh.IsClosed)
            {
                try
                {
                    bool success = false;
                    sh.DangerousAddRef(ref success);
                }
                catch { }
                return;
            }

            var type = obj.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(IntPtr) || type == typeof(UIntPtr)) return;

            var curType = type;
            while (curType != null && curType != typeof(object))
            {
                foreach (var field in curType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    try
                    {
                        var val = field.GetValue(obj);
                        if (val != null) ProtectAllSafeHandles(val, visited);
                    }
                    catch { }
                }
                curType = curType.BaseType;
            }
        }

        private IntPtr GetHandle(object obj)
        {
            if (obj == null) return IntPtr.Zero;
            var type = obj.GetType();
            
            var prop = type.GetProperty("SafeFileHandle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                       type.GetProperty("SafePipeHandle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            
            if (prop != null)
            {
                var safeHandle = (SafeHandle)prop.GetValue(obj);
                if (safeHandle != null && !safeHandle.IsInvalid)
                {
                    return safeHandle.DangerousGetHandle();
                }
            }

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (typeof(SafeHandle).IsAssignableFrom(field.FieldType))
                {
                    var handleObj = (SafeHandle)field.GetValue(obj);
                    if (handleObj != null && !handleObj.IsInvalid)
                    {
                        return handleObj.DangerousGetHandle();
                    }
                }
                else if (field.FieldType == typeof(IntPtr) && field.Name.IndexOf("handle", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    IntPtr ptr = (IntPtr)field.GetValue(obj);
                    if (ptr != IntPtr.Zero) return ptr;
                }
                else if (typeof(Stream).IsAssignableFrom(field.FieldType))
                {
                    var inner = field.GetValue(obj);
                    if (inner != null && inner != obj)
                    {
                        var handle = GetHandle(inner);
                        if (handle != IntPtr.Zero) return handle;
                    }
                }
            }
            return IntPtr.Zero;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PeekNamedPipe(IntPtr handle, IntPtr buffer, uint nBufferSize, IntPtr bytesRead, out uint bytesAvail, IntPtr bytesLeftThisMessage);

        [StructLayout(LayoutKind.Sequential)]
        private struct pollfd
        {
            public int fd;
            public short events;
            public short revents;
        }

        [DllImport("libc")]
        private static extern int poll(ref pollfd fds, int nfds, int timeout);

        private void StartReading(TabSession session)
        {
            session.ReadThread = new Thread(() =>
            {
                byte[] buf = new byte[16384];
                int carry = 0; // incomplete UTF-8 tail kept at the start of buf
                bool isWin = Application.platform == RuntimePlatform.WindowsEditor;
                
                while (_isRunning && !session.IsClosing && session.ReaderStream != null && session.ReaderStream.CanRead)
                {
                    try
                    {
                        uint available = 0;
                        if (isWin)
                        {
                            IntPtr handle = new IntPtr(session.ReadHandle);
                            if (!PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out available, IntPtr.Zero))
                            {
                                break;
                            }
                        }
                        else
                        {
                            pollfd pfd = new pollfd { fd = (int)session.ReadHandle, events = 1 };
                            int pollRes = poll(ref pfd, 1, 0);
                            if (pollRes < 0) break;
                            if (pollRes > 0) available = 8192;
                        }

                        if (available == 0)
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        if (session.ReaderStream == null || !session.ReaderStream.CanRead)
                        {
                            break;
                        }

                        int toRead = buf.Length - carry;
                        if (isWin) toRead = (int)Math.Min(toRead, available);
                        int read = session.ReaderStream.Read(buf, carry, toRead);
                        if (read <= 0) break;

                        // Feed only whole UTF-8 sequences; a code point split across reads waits for the rest.
                        int total = carry + read;
                        int complete = GetCompleteUtf8Length(buf, total);
                        if (complete > 0) FeedPtyOutput(session, buf, complete);
                        carry = total - complete;
                        if (carry > 0) Array.Copy(buf, complete, buf, 0, carry);
                    }
                    catch (ThreadAbortException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (NotSupportedException) { break; }
                    catch (IOException) { break; }
                    catch (NullReferenceException) { break; }
                    catch (Exception ex) { 
                        UnityEngine.Debug.LogError("[FuzzyIDE Terminal] ReadThread Exception: " + ex);
                        break; 
                    }
                }
            });
            session.ReadThread.IsBackground = true;
            session.ReadThread.Start();
        }

        /// <summary>Runs PTY output through the stream filters into the emulator. Called on the reader thread.</summary>
        private void FeedPtyOutput(TabSession session, byte[] data, int count)
        {
            session.Recorder?.WriteOutput(data, count);
            session.Sync.Scan(data, count);
            int length = session.Filter.Process(data, count);
            byte[] output = session.Filter.Output;

            bool enteredAlt;
            lock (session.Term)
            {
                bool wasAlt = session.Term.Buffers.IsAlternateBuffer;
                if (length > 0)
                {
                    try
                    {
                        session.Term.Feed(output, length);
                    }
                    catch (Exception feedEx)
                    {
                        UnityEngine.Debug.LogWarning("[FuzzyIDE Terminal] Feed warning: " + feedEx.Message);
                    }
                }

                bool isAlt = session.Term.Buffers.IsAlternateBuffer;
                enteredAlt = !wasAlt && isAlt;
                if (wasAlt && !isAlt)
                {
                    // Back from a full-screen app: show the prompt even if the user had scrolled up before.
                    SetViewportTop(session.Term, session.Term.Buffer.YBase);
                }
                session.IsDirty = true;
            }

            for (int i = session.Sync.TakePendingQueries(); i > 0; i--)
            {
                WriteToActivePty(session.Id, SyncOutputFilter.QueryReply);
            }
            while (session.Filter.PendingReplies.TryDequeue(out var reply))
            {
                WriteToActivePty(session.Id, reply);
            }

            // If entered alternate buffer (e.g. agy/vim), immediately sync PTY size so it never renders shifted
            if (enteredAlt && _currentCols > 0 && _currentRows > 0)
            {
                if (session.Pty != null)
                {
                    try { session.Pty.Resize(_currentCols, _currentRows); } catch { }
                }
                else if (session.PtyHandle != 0 && Application.platform == RuntimePlatform.WindowsEditor)
                {
                    try { ResizePseudoConsole(new IntPtr(session.PtyHandle), new COORD { X = (short)_currentCols, Y = (short)_currentRows }); } catch { }
                }
            }
        }

        /// <summary>Length of the prefix of <paramref name="data"/> that does not end inside a UTF-8 sequence.</summary>
        private static int GetCompleteUtf8Length(byte[] data, int count)
        {
            for (int back = 1; back <= 4 && back <= count; back++)
            {
                byte b = data[count - back];
                if ((b & 0xC0) == 0x80) continue; // continuation byte
                int needed = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
                return back < needed ? count - back : count;
            }
            return count;
        }



        // Mouse reporting state: the xterm button currently held down for the app, -1 when none.
        private int _reportedButton = -1;
        private int _lastReportedCol = -1, _lastReportedRow = -1;

        private static bool IsMouseTracking(TabSession session)
        {
            lock (session.Term) return session.Term.MouseMode != MouseMode.Off;
        }

        /// <summary>
        /// Sends a mouse report to the application. Buttons: 0 left, 1 middle, 2 right, 3 none (motion),
        /// 64/65 wheel up/down. Encoded as SGR (CSI &lt; b;x;y M/m) when the app asked for it, otherwise in
        /// the legacy CSI M form.
        /// </summary>
        private void SendMouseReport(TabSession session, int button, bool release, bool motion, int col, int row, EventModifiers modifiers)
        {
            MouseProtocolEncoding protocol;
            lock (session.Term) protocol = session.Term.MouseProtocol;

            int code = button;
            if ((modifiers & EventModifiers.Shift) != 0) code |= 4;
            if ((modifiers & EventModifiers.Alt) != 0) code |= 8;
            if ((modifiers & EventModifiers.Control) != 0) code |= 16;
            if (motion) code |= 32;

            byte[] report;
            if (protocol == MouseProtocolEncoding.SGR)
            {
                report = Encoding.ASCII.GetBytes($"\x1b[<{code};{col + 1};{row + 1}{(release ? 'm' : 'M')}");
            }
            else
            {
                if (col > 222 || row > 222) return; // not representable in the legacy encoding
                if (release) code = (code & ~3) | 3;
                report = new byte[] { 0x1b, (byte)'[', (byte)'M', (byte)(32 + code), (byte)(33 + col), (byte)(33 + row) };
            }
            WriteToActivePty(session.Id, report);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            rootVisualElement.Focus();
            if (_activeTabId == -1 || !_sessions.TryGetValue(_activeTabId, out var session)) return;
            GetCellAt(evt.position, out int col, out int row);

            // Ctrl+click (Cmd+click on macOS) opens the URL or file under the pointer.
            if (evt.button == 0 && evt.actionKey && !evt.shiftKey && TryOpenLinkAt(session, col, row))
            {
                evt.StopPropagation();
                return;
            }

            // Apps that track the mouse get left/middle clicks. Shift+click still selects text locally and
            // right click keeps the context menu (copy/paste), like Windows Terminal.
            if (evt.button != 1 && !evt.shiftKey && IsMouseTracking(session))
            {
                _reportedButton = evt.button == 2 ? 1 : 0; // Unity: 0 left, 1 right, 2 middle
                _lastReportedCol = col;
                _lastReportedRow = row;
                SendMouseReport(session, _reportedButton, false, false, col, row, evt.modifiers);
                _grid?.CapturePointer(evt.pointerId);
                evt.StopPropagation();
                return;
            }

            if (evt.button == 1) // Right click context menu
            {
                ShowContextMenu(evt.position);
                evt.StopPropagation();
                return;
            }

            if (evt.button == 0) // Left click selection
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - _lastClickTime < 0.35) _clickCount++;
                else _clickCount = 1;
                _lastClickTime = now;

                // Selection rows are buffer line indices, so the selection stays on its text while output scrolls.
                int line;
                lock (session.Term) line = row + session.Term.Buffer.YDisp;

                if (_clickCount == 1)
                {
                    _selStartCol = col; _selStartRow = line;
                    _selEndCol = col; _selEndRow = line;
                    _isSelecting = true;
                    _selDragLocalY = _grid != null ? _grid.WorldToLocal(evt.position).y : float.NaN;
                    _selDragCol = col;
                    _grid?.CapturePointer(evt.pointerId);
                }
                else if (_clickCount == 2)
                {
                    SelectWordAt(col, line);
                }
                else if (_clickCount >= 3)
                {
                    SelectLineAt(line);
                }
                evt.StopPropagation();
            }
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_activeTabId == -1 || !_sessions.TryGetValue(_activeTabId, out var session)) return;
            GetCellAt(evt.position, out int col, out int row);

            if (_reportedButton >= 0 || (!_isSelecting && IsMouseTracking(session)))
            {
                if (col == _lastReportedCol && row == _lastReportedRow) return;
                MouseMode mode;
                lock (session.Term) mode = session.Term.MouseMode;

                // 1002 reports drags, 1003 also reports motion with no button held.
                bool report = _reportedButton >= 0
                    ? mode == MouseMode.ButtonEventTracking || mode == MouseMode.AnyEvent
                    : mode == MouseMode.AnyEvent;
                if (report)
                {
                    SendMouseReport(session, _reportedButton >= 0 ? _reportedButton : 3, false, true, col, row, evt.modifiers);
                    _lastReportedCol = col;
                    _lastReportedRow = row;
                }
                if (_reportedButton >= 0) evt.StopPropagation();
                return;
            }

            if (_isSelecting && (evt.pressedButtons & 1) != 0)
            {
                int line;
                lock (session.Term) line = row + session.Term.Buffer.YDisp;
                _selEndCol = col;
                _selEndRow = line;
                _selDragLocalY = _grid != null ? _grid.WorldToLocal(evt.position).y : float.NaN;
                _selDragCol = col;
                evt.StopPropagation();
            }
        }

        /// <summary>
        /// While drag-selecting with the pointer above or below the grid, scrolls the viewport and extends the
        /// selection, faster the further away the pointer is. Called every editor update.
        /// </summary>
        private void UpdateSelectionAutoScroll(TabSession session)
        {
            if (!_isSelecting || float.IsNaN(_selDragLocalY) || _grid == null) return;

            float height = _currentRows * _charHeight;
            int direction = _selDragLocalY < 0 ? -1 : _selDragLocalY > height ? 1 : 0;
            if (direction == 0) return;

            float distance = direction < 0 ? -_selDragLocalY : _selDragLocalY - height;
            double interval = Mathf.Lerp(0.12f, 0.016f, Mathf.Clamp01(distance / (_charHeight * 6)));
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastAutoScrollTime < interval) return;
            _lastAutoScrollTime = now;

            ScrollTerminal(session, direction);
            lock (session.Term)
            {
                var buffer = session.Term.Buffer;
                _selEndRow = buffer.YDisp + (direction < 0 ? 0 : _currentRows - 1);
                _selEndCol = _selDragCol;
            }
        }

        /// <summary>Opens the URL or file reference under the given visible cell. Returns false when there is none.</summary>
        private bool TryOpenLinkAt(TabSession session, int col, int row)
        {
            string text;
            int index = -1;
            lock (session.Term)
            {
                var buffer = session.Term.Buffer;
                var lines = buffer.Lines;
                int cols = session.Term.Cols;
                int line = row + buffer.YDisp;
                if (line < 0 || line >= lines.Length) return false;

                // Soft-wrapped rows form one logical line, so links broken across rows still match.
                int first = line;
                while (first > 0 && lines[first] != null && lines[first].IsWrapped) first--;
                int last = line;
                while (last + 1 < lines.Length && lines[last + 1] != null && lines[last + 1].IsWrapped) last++;

                var sb = new StringBuilder();
                for (int y = first; y <= last; y++)
                {
                    var cells = lines[y];
                    if (cells == null) continue;
                    int width = Math.Min(cells.Length, cols);
                    for (int x = 0; x < width; x++)
                    {
                        var cell = cells[x];
                        if (cell.Width == 0 && x > 0 && cells[x - 1].Width == 2) continue;
                        if (y == line && x == col) index = sb.Length;
                        int code = cell.Code;
                        if (code <= 32 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) sb.Append(' ');
                        else sb.Append(char.ConvertFromUtf32(code));
                    }
                }
                text = sb.ToString();
            }

            var kind = TerminalLinks.Find(text, index, out string target, out int lineNumber);
            if (kind == TerminalLinks.Kind.Url)
            {
                Application.OpenURL(target);
                return true;
            }
            if (kind != TerminalLinks.Kind.File) return false;

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string fullPath = TerminalLinks.ResolvePath(target, projectRoot);
            if (fullPath == null) return false;

            if (Directory.Exists(fullPath))
            {
                EditorUtility.RevealInFinder(fullPath);
                return true;
            }

            // Project assets go through AssetDatabase so they open where the user opens scripts (FuzzyIDE or
            // the external editor); anything else in the external editor.
            string relative = fullPath.Replace('\\', '/');
            string root = projectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (relative.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                relative = relative.Substring(root.Length);
                if (relative.StartsWith("Assets/", StringComparison.Ordinal) || relative.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(relative);
                    if (asset != null)
                    {
                        AssetDatabase.OpenAsset(asset, lineNumber > 0 ? lineNumber : -1);
                        return true;
                    }
                }
            }
            UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(fullPath, lineNumber > 0 ? lineNumber : -1);
            return true;
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (_reportedButton >= 0)
            {
                if (_activeTabId != -1 && _sessions.TryGetValue(_activeTabId, out var session))
                {
                    MouseMode mode;
                    lock (session.Term) mode = session.Term.MouseMode;
                    GetCellAt(evt.position, out int col, out int row);
                    if (mode != MouseMode.Off && mode != MouseMode.X10)
                        SendMouseReport(session, _reportedButton, true, false, col, row, evt.modifiers);
                }
                _reportedButton = -1;
                _grid?.ReleasePointer(evt.pointerId);
                evt.StopPropagation();
                return;
            }

            if (evt.button == 0)
            {
                _isSelecting = false;
                _selDragLocalY = float.NaN;
                _grid?.ReleasePointer(evt.pointerId);
                evt.StopPropagation();
            }
        }

        /// <param name="line">Buffer line index (scrollback included).</param>
        private void SelectWordAt(int col, int line)
        {
            if (_activeTabId == -1 || !_sessions.TryGetValue(_activeTabId, out var session)) return;
            lock (session.Term)
            {
                var buffer = session.Term.Buffer;
                if (line < 0 || line >= buffer.Lines.Length) return;
                var cells = buffer.Lines[line];
                if (col >= cells.Length) return;

                int startC = col;
                while (startC > 0 && IsWordChar(cells[startC - 1].Code)) startC--;
                int endC = col;
                while (endC < cells.Length - 1 && IsWordChar(cells[endC + 1].Code)) endC++;

                _selStartRow = line; _selStartCol = startC;
                _selEndRow = line; _selEndCol = endC;
            }
        }

        private bool IsWordChar(int code)
        {
            if (code <= 32) return false;
            char c = (char)code;
            // Include path and URL separators for developer-friendly double-click selection
            return char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '/' || c == '\\' || c == '~' || c == ':';
        }

        private void SelectLineAt(int line)
        {
            _selStartRow = line; _selStartCol = 0;
            _selEndRow = line; _selEndCol = GetActiveCols() - 1;
        }

        private void ShowContextMenu(Vector2 mousePos)
        {
            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("Copy"), false, CopyToClipboard);
            menu.AddItem(new GUIContent("Paste"), false, PasteFromClipboard);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Select All"), false, SelectAll);
            menu.AddSeparator("");
            foreach (TerminalTheme theme in Enum.GetValues(typeof(TerminalTheme)))
            {
                string label = theme == TerminalTheme.OneHalfDark ? "One Half Dark" : theme == TerminalTheme.Fuzzy ? "Fuzzy (classic)" : theme.ToString();
                var selected = theme;
                menu.AddItem(new GUIContent("Appearance/Theme/" + label), TerminalPalette.Theme == theme, () => SetTheme(selected));
            }
            bool crisp = CrispText;
            menu.AddItem(new GUIContent("Appearance/Crisp Text"), crisp, () => SetCrispText(!crisp));
            bool recording = _activeTabId != -1 && _sessions.TryGetValue(_activeTabId, out var active) && active.Recorder != null;
            menu.AddItem(new GUIContent("Debug/Record Raw Output"), recording, ToggleRecording);
            menu.AddItem(new GUIContent("Reset Session"), false, () => {
                if (_activeTabId != -1)
                {
                    CloseTab(_activeTabId);
                    SpawnTab();
                }
            });
            menu.ShowAsContext();
        }

        /// <summary>Starts or stops recording the active tab's raw PTY output (for diagnosing rendering issues).</summary>
        private void ToggleRecording()
        {
            if (_activeTabId == -1 || !_sessions.TryGetValue(_activeTabId, out var session)) return;

            var recorder = session.Recorder;
            if (recorder != null)
            {
                session.Recorder = null;
                recorder.Dispose();
                UnityEngine.Debug.Log($"[FuzzyIDE Terminal] Recording saved: {recorder.Path}");
                EditorUtility.RevealInFinder(recorder.Path);
                return;
            }

            try
            {
                string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Logs", "FuzzyTerminal");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, $"tab{session.Id}_{DateTime.Now:yyyyMMdd_HHmmss}.ftrec");
                int cols, rows;
                lock (session.Term)
                {
                    cols = session.Term.Cols;
                    rows = session.Term.Rows;
                }
                session.Recorder = new PtyRecorder(path, cols, rows);
                UnityEngine.Debug.Log($"[FuzzyIDE Terminal] Recording tab {session.Id} to {path}");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[FuzzyIDE Terminal] Could not start recording: {ex.Message}");
            }
        }

        /// <summary>Selects the whole buffer, scrollback included.</summary>
        private void SelectAll()
        {
            if (_activeTabId == -1 || !_sessions.TryGetValue(_activeTabId, out var session)) return;
            lock (session.Term)
            {
                _selStartRow = 0; _selStartCol = 0;
                _selEndRow = session.Term.Buffer.Lines.Length - 1; _selEndCol = session.Term.Cols - 1;
            }
        }

        private string GetSelectedText()
        {
            if (_activeTabId == -1 || !_sessions.TryGetValue(_activeTabId, out var session)) return "";
            if (_selStartRow < 0 || _selEndRow < 0) return "";

            int startR = _selStartRow, startC = _selStartCol;
            int endR = _selEndRow, endC = _selEndCol;
            if (startR > endR || (startR == endR && startC > endC))
            {
                int tr = startR; startR = endR; endR = tr;
                int tc = startC; startC = endC; endC = tc;
            }

            StringBuilder sb = new StringBuilder();
            lock (session.Term)
            {
                var buffer = session.Term.Buffer;
                int lineCount = buffer.Lines.Length;
                int maxCols = session.Term.Cols;

                for (int r = Math.Max(0, startR); r <= endR && r < lineCount; r++)
                {
                    var line = buffer.Lines[r];
                    int cFrom = (r == startR) ? Mathf.Clamp(startC, 0, maxCols - 1) : 0;
                    int cTo = (r == endR) ? Mathf.Clamp(endC, 0, maxCols - 1) : maxCols - 1;

                    StringBuilder lineSb = new StringBuilder();
                    for (int c = cFrom; c <= cTo && c < line.Length; c++)
                    {
                        int code = line[c].Code;
                        if (code == 0) lineSb.Append(' ');
                        else lineSb.Append(char.ConvertFromUtf32(code));
                    }
                    string text = lineSb.ToString().TrimEnd();
                    sb.Append(text);

                    // IsWrapped marks a line that continues the previous one, so a soft wrap is detected on
                    // the next line; wrapped long commands then stay unbroken when copied.
                    bool continuesOnNextLine = r + 1 < lineCount && buffer.Lines[r + 1].IsWrapped;
                    if (!continuesOnNextLine && r < endR)
                    {
                        sb.AppendLine();
                    }
                }
            }
            return sb.ToString().TrimEnd('\r', '\n');
        }

        private void CopyToClipboard()
        {
            string text = GetSelectedText();
            if (!string.IsNullOrEmpty(text))
            {
                EditorGUIUtility.systemCopyBuffer = text;
            }
        }

        private void PasteFromClipboard()
        {
            string text = EditorGUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(text)) return;

            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (_activeTabId != -1 && _sessions.TryGetValue(_activeTabId, out var s) && s.Term != null && s.Term.BracketedPasteMode)
            {
                WriteToActivePty("\x1b[200~" + text + "\x1b[201~");
            }
            else
            {
                string formatted = text.Replace("\n", "\r");
                WriteToActivePty(formatted);
            }
        }

        private void GetRowSelectionBounds(int row, int cols, out int minX, out int maxX)
        {
            minX = -1; maxX = -1;
            if (_selStartRow < 0 || _selEndRow < 0) return;
            int startR = _selStartRow, startC = _selStartCol;
            int endR = _selEndRow, endC = _selEndCol;
            if (startR > endR || (startR == endR && startC > endC))
            {
                int tr = startR; startR = endR; endR = tr;
                int tc = startC; startC = endC; endC = tc;
            }

            if (row < startR || row > endR) return;
            if (row > startR && row < endR) { minX = 0; maxX = cols - 1; return; }
            if (row == startR && row == endR) { minX = startC; maxX = endC; return; }
            if (row == startR) { minX = startC; maxX = cols - 1; return; }
            if (row == endR) { minX = 0; maxX = endC; return; }
        }

        private bool _lastBlinkState = false;
        private int _lastActiveTabId = -1;
        private int _lastCursorRow = -1;
        private int _lastSelStartCol = -1, _lastSelStartRow = -1;
        private int _lastSelEndCol = -1, _lastSelEndRow = -1;

        private void UpdateRowsIfNeeded(float heightOverride = -1f, float widthOverride = -1f)
        {
            if (_grid == null) return;
            int targetCols = GetActiveCols(widthOverride);
            int targetRows = GetActiveRows(heightOverride);
            if (targetCols <= 0 || targetRows <= 0) return;

            // On initial launch or after domain reload, apply immediately without debouncing
            if (_currentCols == -1 || _currentRows == -1)
            {
                ApplyPendingResize(targetCols, targetRows);
                return;
            }

            // If dimensions changed, schedule debounced PTY resize
            if (targetCols != _currentCols || targetRows != _currentRows)
            {
                _pendingResizeCols = targetCols;
                _pendingResizeRows = targetRows;
                _lastResizeRequestTime = EditorApplication.timeSinceStartup;
            }
        }

        private void CheckPendingResize()
        {
            if (_pendingResizeCols <= 0 || _pendingResizeRows <= 0) return;

            double now = EditorApplication.timeSinceStartup;
            // 80ms debounce window prevents flooding ConPTY with 60 resize calls/sec while dragging
            if (now - _lastResizeRequestTime >= 0.08)
            {
                int cols = _pendingResizeCols;
                int rows = _pendingResizeRows;
                _pendingResizeCols = -1;
                _pendingResizeRows = -1;
                ApplyPendingResize(cols, rows);
            }
        }

        private void ApplyPendingResize(int targetCols, int targetRows)
        {
            if (targetCols <= 0 || targetRows <= 0) return;
            if (targetCols == _currentCols && targetRows == _currentRows) return;

            _currentCols = targetCols;
            _currentRows = targetRows;
            EditorPrefs.SetInt("FuzzyTerminal_LastCols", _currentCols);
            EditorPrefs.SetInt("FuzzyTerminal_LastRows", _currentRows);

            _grid?.SetRowCount(_currentRows);

            foreach (var session in _sessions.Values)
            {
                try
                {
                    // 1. Resize the emulator first. ConPTY repaints the screen as soon as it learns the new
                    //    size; if that repaint were fed into the old-size grid the prompt would land on the
                    //    wrong row/column until the next full redraw.
                    session.Recorder?.WriteResize(_currentCols, _currentRows);
                    lock (session.Term)
                    {
                        int prevYDisp = session.Term.Buffer.YDisp;
                        int prevYBase = session.Term.Buffer.YBase;
                        ConPtyResize.Resize(session.Term, _currentCols, _currentRows);
                        // Keep following output when at the bottom, otherwise keep the same distance from it.
                        int offsetFromBottom = Mathf.Max(0, prevYBase - prevYDisp);
                        SetViewportTop(session.Term, session.Term.Buffer.YBase - offsetFromBottom);
                    }

                    // 2. Then the PTY.
                    if (session.Pty != null)
                    {
                        try { session.Pty.Resize(_currentCols, _currentRows); } catch { }
                    }
                    else if (session.PtyHandle != 0 && Application.platform == RuntimePlatform.WindowsEditor)
                    {
                        try { ResizePseudoConsole(new IntPtr(session.PtyHandle), new COORD { X = (short)_currentCols, Y = (short)_currentRows }); } catch { }
                    }
                    session.IsDirty = true;
                }
                catch { }
            }
            // Bypass the 30 FPS render cap so the resized terminal paints immediately
            _lastRenderTime = 0;
        }


        private void OnTerminalGeometryChanged(GeometryChangedEvent evt)
        {
            // Moved to a display with another scale: crisp glyphs are rasterized per device pixel size.
            if (_fonts != null && Mathf.Abs(_fonts.PixelsPerPoint - EditorGUIUtility.pixelsPerPoint) > 0.001f)
            {
                ApplyFontSize();
                return;
            }
            UpdateRowsIfNeeded(evt.newRect.height, evt.newRect.width);
        }

        private void RenderTerminal()
        {
            if (_fonts != null && !_fonts.EnsureAlive())
            {
                ApplyFontSize();
            }

            CheckPendingResize();

            if (_activeTabId == -1 || !_sessions.TryGetValue(_activeTabId, out var session)) return;

            UpdateSelectionAutoScroll(session);

            // The app is in the middle of a synchronized frame (DEC 2026); painting now would show it half-drawn.
            if (session.Sync.IsHolding) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastRenderTime < 0.016) return; // ~60 FPS cap; rows only repaint when their cells change

            double timeSinceKey = now - _lastKeyPressTime;
            bool showBlinkCursor = (timeSinceKey < 0.6) || ((now % 1.0) < 0.5);
            bool blinkChanged = (showBlinkCursor != _lastBlinkState);
            bool selectionChanged = (_selStartCol != _lastSelStartCol || _selStartRow != _lastSelStartRow ||
                                     _selEndCol != _lastSelEndCol || _selEndRow != _lastSelEndRow);
            bool tabChanged = (_activeTabId != _lastActiveTabId);

            if (!session.IsDirty && !blinkChanged && !selectionChanged && !tabChanged && !_focusChanged)
            {
                return;
            }

            _lastRenderTime = now;
            session.IsDirty = false;
            _focusChanged = false;
            _lastBlinkState = showBlinkCursor;
            _lastActiveTabId = _activeTabId;
            _lastSelStartCol = _selStartCol; _lastSelStartRow = _selStartRow;
            _lastSelEndCol = _selEndCol; _lastSelEndRow = _selEndRow;
            if (!string.IsNullOrEmpty(session.PendingTitle))
            {
                if (session.TitleButton != null)
                {
                    session.TitleButton.text = session.PendingTitle;
                }
                session.PendingTitle = null;
            }

            int cols = 0;
            int termRows = 0;

            lock (session.Term)
            {
                var buffer = session.Term.Buffer;
                UpdateScroller(buffer);

                var lines = buffer.Lines;
                cols = session.Term.Cols;
                termRows = session.Term.Rows;
                bool cursorOnScreen = buffer.YDisp == buffer.YBase && !session.Term.CursorHidden;
                int cursorRow = cursorOnScreen ? buffer.Y : -1;
                bool focused = hasFocus;

                if (_grid != null)
                {
                    if (_grid.RowCount != termRows) _grid.SetRowCount(termRows);
                    for (int y = 0; y < termRows; y++)
                    {
                        int bufferRow = y + buffer.YDisp;
                        BufferLine line = bufferRow >= 0 && bufferRow < lines.Length ? lines[bufferRow] : null;

                        var overlay = RowOverlay.None;
                        GetRowSelectionBounds(bufferRow, cols, out overlay.SelectionStart, out overlay.SelectionEnd);
                        if (y == cursorRow)
                        {
                            overlay.CursorColumn = Mathf.Clamp(buffer.X, 0, cols - 1);
                            overlay.CursorFilled = focused && showBlinkCursor;
                        }
                        _grid.GetRow(y).Load(line, cols, overlay);
                    }
                }

                _lastCursorRow = cursorRow;
            }

            if (_statusShellInfo != null)
            {
                _statusShellInfo.text = $"{session.ShellName} (PID: {session.Pid})";
            }
            if (_statusSizeInfo != null)
            {
                _statusSizeInfo.text = $"{cols}x{termRows} ({_fontSize}px)";
            }
        }

        private void WriteToActivePty(int tabId, byte[] bytes)
        {
            if (_sessions.TryGetValue(tabId, out var session) && session.WriterStream != null)
            {
                try
                {
                    session.IsDirty = true;
                    // Keyboard input (main thread) and terminal replies (reader thread) share the stream.
                    lock (session.WriteLock)
                    {
                        session.WriterStream.Write(bytes, 0, bytes.Length);
                        session.WriterStream.Flush();
                    }
                }
                catch { }
            }
        }

        private bool _isUpdatingScrollbar = false;
        private bool _focusChanged = false;

        /// <summary>Repaints the cursor style and, when the app enabled focus reporting (CSI ?1004h), tells it.</summary>
        private void OnTerminalFocusChanged(bool focused)
        {
            _focusChanged = true;
            if (_activeTabId == -1 || !_sessions.TryGetValue(_activeTabId, out var session)) return;
            bool report;
            lock (session.Term) report = session.Term.SendFocus;
            if (report) WriteToActivePty(session.Id, Encoding.ASCII.GetBytes(focused ? "\x1b[I" : "\x1b[O"));
        }

        /// <summary>Mirrors the viewport position into the scrollbar. Call with the terminal locked.</summary>
        private void UpdateScroller(XtermSharp.Buffer buffer)
        {
            if (_scroller == null) return;
            int maxTopRow = buffer.YBase;
            var visibility = maxTopRow > 0 ? Visibility.Visible : Visibility.Hidden;
            if (_scroller.style.visibility != visibility) _scroller.style.visibility = visibility;
            if (maxTopRow <= 0) return;

            _isUpdatingScrollbar = true;
            try
            {
                _scroller.lowValue = 0;
                _scroller.highValue = maxTopRow;
                int rows = Mathf.Max(1, buffer.Rows);
                _scroller.Adjust(rows / (float)(rows + maxTopRow));
                _scroller.value = Mathf.Clamp(buffer.YDisp, 0, maxTopRow);
            }
            finally
            {
                _isUpdatingScrollbar = false;
            }
        }

        private void OnScrollbarValueChanged(float value)
        {
            if (_isUpdatingScrollbar) return;
            if (_activeTabId == -1 || !_sessions.TryGetValue(_activeTabId, out var session)) return;

            lock (session.Term)
            {
                int targetYDisp = Mathf.RoundToInt(value);
                if (session.Term.Buffer.YDisp != targetYDisp)
                {
                    SetViewportTop(session.Term, targetYDisp);
                    session.IsDirty = true;
                }
            }
        }

        private void ScrollTerminal(TabSession session, int deltaLines)
        {
            lock (session.Term)
            {
                var buffer = session.Term.Buffer;
                int newYDisp = Mathf.Clamp(buffer.YDisp + deltaLines, 0, buffer.YBase);
                if (newYDisp != buffer.YDisp)
                {
                    SetViewportTop(session.Term, newYDisp);
                    session.IsDirty = true;
                }
            }
        }

        private void OnScrollWheel(WheelEvent evt)
        {
            if (_activeTabId == -1 || !_sessions.TryGetValue(_activeTabId, out var session)) return;
            if (evt.delta.y == 0) return;
            bool down = evt.delta.y > 0;
            evt.StopPropagation();

            // Shift+wheel always scrolls the local scrollback, like most native terminals.
            if (!evt.shiftKey)
            {
                MouseMode mouseMode;
                bool alternateScreen, applicationCursor;
                lock (session.Term)
                {
                    mouseMode = session.Term.MouseMode;
                    alternateScreen = session.Term.Buffers.IsAlternateBuffer;
                    applicationCursor = session.Term.ApplicationCursor;
                }

                // The app tracks the mouse (Claude Code's full-screen mode, vim with mouse=a, htop...):
                // the wheel belongs to it and is reported as buttons 64 (up) / 65 (down).
                if (mouseMode != MouseMode.Off)
                {
                    GetCellAt(evt.mousePosition, out int col, out int row);
                    SendMouseReport(session, down ? 65 : 64, false, false, col, row, evt.modifiers);
                    return;
                }

                // Full-screen app without mouse support (less, man, git log): there is no scrollback in the
                // alternate screen, so the wheel is sent as arrow keys (xterm's alternate scroll mode).
                if (alternateScreen)
                {
                    string arrow = down ? (applicationCursor ? "\x1bOB" : "\x1b[B") : (applicationCursor ? "\x1bOA" : "\x1b[A");
                    WriteToActivePty(session.Id, Encoding.ASCII.GetBytes(arrow + arrow + arrow));
                    return;
                }
            }

            ScrollTerminal(session, down ? 3 : -3);
        }

        /// <summary>Maps a panel-space position to a 0-based cell of the visible grid.</summary>
        private void GetCellAt(Vector2 panelPosition, out int col, out int row)
        {
            Vector2 local = _grid != null ? _grid.WorldToLocal(panelPosition) : Vector2.zero;
            col = Mathf.Clamp(Mathf.FloorToInt(local.x / _charWidth), 0, Mathf.Max(0, _currentCols - 1));
            row = Mathf.Clamp(Mathf.FloorToInt(local.y / _charHeight), 0, Mathf.Max(0, _currentRows - 1));
        }

        private void WriteToActivePty(string data)
        {
            if (_activeTabId != -1 && _sessions.TryGetValue(_activeTabId, out var session))
            {
                lock (session.Term)
                {
                    // Typing returns to the live screen, like native terminals.
                    if (session.Term.Buffer.YDisp != session.Term.Buffer.YBase)
                    {
                        SetViewportTop(session.Term, session.Term.Buffer.YBase);
                        session.IsDirty = true;
                    }
                }
                WriteToActivePty(_activeTabId, Encoding.UTF8.GetBytes(data));
            }
        }

        private void OnTerminalKeyDown(KeyDownEvent evt)
        {
            _lastKeyPressTime = EditorApplication.timeSinceStartup;
            TabSession activeSession = null;
            if (_activeTabId != -1) _sessions.TryGetValue(_activeTabId, out activeSession);

            // Font zoom shortcuts: Ctrl + = (zoom in), Ctrl + - (zoom out), Ctrl + 0 (reset)
            if (evt.actionKey && (evt.keyCode == KeyCode.Equals || evt.keyCode == KeyCode.KeypadPlus))
            {
                ChangeFontSize(1);
                evt.StopImmediatePropagation();
                return;
            }
            if (evt.actionKey && (evt.keyCode == KeyCode.Minus || evt.keyCode == KeyCode.KeypadMinus))
            {
                ChangeFontSize(-1);
                evt.StopImmediatePropagation();
                return;
            }
            if (evt.actionKey && (evt.keyCode == KeyCode.Alpha0 || evt.keyCode == KeyCode.Keypad0))
            {
                ResetFontSize();
                evt.StopImmediatePropagation();
                return;
            }

            // Clear Screen: Ctrl + K
            if (evt.actionKey && evt.keyCode == KeyCode.K)
            {
                ClearActiveTerminal();
                evt.StopImmediatePropagation();
                return;
            }

            // Close Active Tab: Ctrl + Shift + W
            if (evt.actionKey && evt.shiftKey && evt.keyCode == KeyCode.W)
            {
                if (_activeTabId != -1) CloseTab(_activeTabId);
                evt.StopImmediatePropagation();
                return;
            }

            // New Tab: Ctrl + Shift + T
            if (evt.actionKey && evt.shiftKey && evt.keyCode == KeyCode.T)
            {
                SpawnTab();
                evt.StopImmediatePropagation();
                return;
            }

            // Shift + PageUp / PageDown -> Scroll Terminal History
            if (evt.shiftKey && evt.keyCode == KeyCode.PageUp)
            {
                if (activeSession != null)
                {
                    ScrollTerminal(activeSession, -activeSession.Term.Rows);
                }
                evt.StopImmediatePropagation();
                return;
            }
            if (evt.shiftKey && evt.keyCode == KeyCode.PageDown)
            {
                if (activeSession != null)
                {
                    ScrollTerminal(activeSession, activeSession.Term.Rows);
                }
                evt.StopImmediatePropagation();
                return;
            }

            // Copy: Ctrl+C or Ctrl+Shift+C
            if (evt.actionKey && evt.keyCode == KeyCode.C)
            {
                string sel = GetSelectedText();
                if (!string.IsNullOrEmpty(sel))
                {
                    EditorGUIUtility.systemCopyBuffer = sel;
                    evt.StopImmediatePropagation();
                    return;
                }
                if (!evt.shiftKey)
                {
                    WriteToActivePty("\x03"); // SIGINT
                    evt.StopImmediatePropagation();
                    return;
                }
            }

            // Paste: Ctrl+V or Ctrl+Shift+V
            if (evt.actionKey && evt.keyCode == KeyCode.V)
            {
                PasteFromClipboard();
                evt.StopImmediatePropagation();
                return;
            }

            // Shift + Tab -> ANSI Backtab (\x1b[Z)
            if (evt.keyCode == KeyCode.Tab)
            {
                WriteToActivePty(evt.shiftKey ? "\x1b[Z" : "\t");
                evt.StopImmediatePropagation();
                return;
            }

            // Ctrl + Letter Shortcuts (Ctrl+A..Ctrl+Z)
            if (evt.actionKey && !evt.altKey && evt.keyCode >= KeyCode.A && evt.keyCode <= KeyCode.Z)
            {
                int ctrlCode = (int)evt.keyCode - (int)KeyCode.A + 1;
                WriteToActivePty(char.ConvertFromUtf32(ctrlCode));
                evt.StopImmediatePropagation();
                return;
            }

            // Ctrl+Space sends NUL (set-mark in Emacs/readline, completion trigger in some TUIs).
            if (evt.ctrlKey && !evt.altKey && evt.keyCode == KeyCode.Space)
            {
                WriteToActivePty("\0");
                evt.StopImmediatePropagation();
                return;
            }

            bool appCursor = (activeSession != null && activeSession.Term != null && activeSession.Term.ApplicationCursor);
            // xterm modifier parameter: 1 + Shift(1) + Alt(2) + Ctrl(4). AltGr arrives as Ctrl+Alt and only
            // matters for printable characters, which are handled below.
            int modifier = 1 + (evt.shiftKey ? 1 : 0) + (evt.altKey ? 2 : 0) + (evt.ctrlKey ? 4 : 0);
            string Cursor(char final) => modifier > 1 ? $"\x1b[1;{modifier}{final}" : (appCursor ? $"\x1bO{final}" : $"\x1b[{final}");
            string Function(char final) => modifier > 1 ? $"\x1b[1;{modifier}{final}" : $"\x1bO{final}";
            string Tilde(int code) => modifier > 1 ? $"\x1b[{code};{modifier}~" : $"\x1b[{code}~";

            string sequence = null;
            switch (evt.keyCode)
            {
                case KeyCode.UpArrow: sequence = Cursor('A'); break;
                case KeyCode.DownArrow: sequence = Cursor('B'); break;
                case KeyCode.RightArrow: sequence = Cursor('C'); break;
                case KeyCode.LeftArrow: sequence = Cursor('D'); break;
                case KeyCode.Home: sequence = Cursor('H'); break;
                case KeyCode.End: sequence = Cursor('F'); break;
                case KeyCode.PageUp: sequence = Tilde(5); break;
                case KeyCode.PageDown: sequence = Tilde(6); break;
                case KeyCode.Delete: sequence = Tilde(3); break;
                case KeyCode.Insert: sequence = Tilde(2); break;
                case KeyCode.F1: sequence = Function('P'); break;
                case KeyCode.F2: sequence = Function('Q'); break;
                case KeyCode.F3: sequence = Function('R'); break;
                case KeyCode.F4: sequence = Function('S'); break;
                case KeyCode.F5: sequence = Tilde(15); break;
                case KeyCode.F6: sequence = Tilde(17); break;
                case KeyCode.F7: sequence = Tilde(18); break;
                case KeyCode.F8: sequence = Tilde(19); break;
                case KeyCode.F9: sequence = Tilde(20); break;
                case KeyCode.F10: sequence = Tilde(21); break;
                case KeyCode.F11: sequence = Tilde(23); break;
                case KeyCode.F12: sequence = Tilde(24); break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    double now = EditorApplication.timeSinceStartup;
                    if (now - _lastReturnTime < 0.08) return;
                    _lastReturnTime = now;
                    // Shift/Alt+Enter send ESC CR, the binding Claude Code's /terminal-setup installs for a
                    // newline without submitting; readline treats it as Meta+Enter.
                    sequence = evt.shiftKey || evt.altKey ? "\x1b\r" : "\r";
                    break;
                case KeyCode.Backspace: sequence = evt.ctrlKey ? "\x17" : (evt.altKey ? "\x1b\x7f" : "\x7f"); break;
                case KeyCode.Escape: sequence = "\x1b"; break;
            }

            if (sequence != null)
            {
                WriteToActivePty(sequence);
                evt.StopImmediatePropagation();
            }
            else if (!char.IsControl(evt.character) && evt.character != 0 && (!evt.actionKey || (evt.ctrlKey && evt.altKey)))
            {
                // Alt+key is sent as ESC + key (Meta), which readline, Emacs and most TUIs expect.
                // Ctrl+Alt is AltGr on many layouts (e.g. Turkish Q: AltGr+Q = '@') and types the character.
                bool meta = evt.altKey && !evt.ctrlKey;
                WriteToActivePty(meta ? "\x1b" + evt.character : evt.character.ToString());
                evt.StopImmediatePropagation();
            }
            else
            {
                evt.StopPropagation();
            }
        }
    }

    public class RenameTabPopup : EditorWindow
    {
        private string _titleText;
        private Action<string> _onConfirm;

        public static void ShowPopup(string currentTitle, Action<string> onConfirm)
        {
            var window = CreateInstance<RenameTabPopup>();
            window.titleContent = new GUIContent("Rename Tab");
            window._titleText = currentTitle ?? "";
            window._onConfirm = onConfirm;
            window.minSize = new Vector2(300, 80);
            window.maxSize = new Vector2(300, 80);
            window.ShowAuxWindow();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            _titleText = EditorGUILayout.TextField("Tab Name:", _titleText);
            EditorGUILayout.Space(10);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("OK") || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return))
            {
                _onConfirm?.Invoke(_titleText);
                Close();
            }
            if (GUILayout.Button("Cancel"))
            {
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}