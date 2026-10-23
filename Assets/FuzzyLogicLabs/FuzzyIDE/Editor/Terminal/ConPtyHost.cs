using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Terminal
{
    /// <summary>
    /// Finds an OpenConsole.exe (the modern ConPTY host) for the bundled os64\conpty.dll to start.
    ///
    /// The package can't ship the exe (Asset Store rejects executables), so it reuses the one installed with
    /// Windows Terminal. conpty.dll starts the OpenConsole.exe next to it and falls back to the system conhost.exe
    /// when there is none, which still works but repaints the whole screen after every resize.
    /// </summary>
    internal static class ConPtyHost
    {
        private static readonly UIntPtr HKEY_CURRENT_USER = new UIntPtr(0x80000001u);
        private const int KEY_READ = 0x20019;

        // Every installed packaged app has a subkey named by its package full name, holding its install folder.
        // The WindowsApps folder itself can't be listed without admin rights, but files inside it can be read.
        private const string PackagesKey =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegOpenKeyEx(UIntPtr hKey, string subKey, int options, int samDesired, out UIntPtr result);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegEnumKeyEx(UIntPtr hKey, int index, StringBuilder name, ref int nameLength,
            IntPtr reserved, IntPtr className, IntPtr classLength, IntPtr lastWriteTime);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegQueryValueEx(UIntPtr hKey, string valueName, IntPtr reserved, out int type,
            StringBuilder data, ref int dataLength);

        [DllImport("advapi32.dll")]
        private static extern int RegCloseKey(UIntPtr hKey);

        /// <summary>Path of the OpenConsole.exe from the newest x64 Windows Terminal (stable or Preview), or null.</summary>
        public static string FindWindowsTerminalHost()
        {
            try
            {
                if (RegOpenKeyEx(HKEY_CURRENT_USER, PackagesKey, 0, KEY_READ, out UIntPtr packages) != 0) return null;
                try
                {
                    string best = null;
                    Version bestVersion = null;
                    var name = new StringBuilder(256);
                    for (int i = 0; ; i++)
                    {
                        name.Length = 0;
                        int nameLength = name.Capacity;
                        if (RegEnumKeyEx(packages, i, name, ref nameLength, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0) break;

                        // e.g. Microsoft.WindowsTerminal_1.24.12741.0_x64__8wekyb3d8bbwe
                        string[] parts = name.ToString().Split('_');
                        if (parts.Length < 3 || parts[2] != "x64") continue;
                        if (parts[0] != "Microsoft.WindowsTerminal" && parts[0] != "Microsoft.WindowsTerminalPreview") continue;
                        if (!Version.TryParse(parts[1], out Version version)) continue;
                        if (bestVersion != null && version <= bestVersion) continue;

                        string exe = ReadRootFolder(packages, name.ToString());
                        if (exe == null) continue;
                        exe = Path.Combine(exe, "OpenConsole.exe");
                        if (!File.Exists(exe)) continue;
                        best = exe;
                        bestVersion = version;
                    }
                    return best;
                }
                finally { RegCloseKey(packages); }
            }
            catch { return null; }
        }

        private static string ReadRootFolder(UIntPtr packages, string packageName)
        {
            if (RegOpenKeyEx(packages, packageName, 0, KEY_READ, out UIntPtr key) != 0) return null;
            try
            {
                var data = new StringBuilder(1024);
                int dataLength = data.Capacity * 2;
                if (RegQueryValueEx(key, "PackageRootFolder", IntPtr.Zero, out _, data, ref dataLength) != 0) return null;
                return data.Length > 0 ? data.ToString() : null;
            }
            finally { RegCloseKey(key); }
        }
    }
}
