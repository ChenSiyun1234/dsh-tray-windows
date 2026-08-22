// DeepSeek Harness tray controller.
//
// Owns the whole local stack from one notification-area icon:
//
//   * the backend  — `node dsh/lib/bin.js web --no-open --port <port>`, started
//     hidden, its stdout/stderr tailed into logs\harness.log, and held inside a
//     Win32 job object so it cannot outlive this process under any exit path;
//   * the UI       — a dedicated Edge/Chrome app window (its own user-data-dir,
//     no tabs, no address bar) rather than a tab in the user's browser;
//   * the tray     — a DPI-correct icon that tracks service state, a menu, and
//     one-instance behaviour so a second launch focuses the window instead of
//     stacking another icon in the notification area.
//
// Exit contract: quitting from the tray closes the app window AND stops every
// backend process (port owner tree + any stray `dsh ... web` node tree), and
// the job object is the backstop if this process is force-killed instead.
//
// Build: build-tray.cmd  (csc /target:winexe /win32icon:app.ico)
// Language level is C# 5 — the .NET Framework 4 csc.exe is the compiler.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DeepSeekTray
{
    #region paths

    internal static class Paths
    {
        /// <summary>Wherever the executable lives — icons, logs, settings and the
        /// app-window browser profile all sit beside it, so the install location is
        /// the user's choice and nothing is hardcoded to one machine.</summary>
        public static readonly string AppDir =
            AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        /// <summary>Private browser profile for the app window: it makes the window
        /// a process tree we own, so closing it never touches the user's own browser.</summary>
        public static readonly string BrowserProfile = Path.Combine(AppDir, "app-window-profile");

        public static readonly string LogDir = Path.Combine(AppDir, "logs");
        public static readonly string LogFile = Path.Combine(LogDir, "harness.log");

        public static readonly string DshHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

        public static readonly string PatchFile = Path.Combine(DshHome, @"profiles\web\cordis.patch.yml");

        public static readonly string DshBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"npm\node_modules\@deepseek-ai\dsh\lib\bin.js");

        public static string In(string name) { return Path.Combine(AppDir, name); }

        /// <summary>node.exe from the usual install locations, then PATH.</summary>
        public static string NodeExe()
        {
            string[] candidates = new string[] {
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Program Files (x86)\nodejs\node.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\nodejs\node.exe"),
            };
            foreach (string c in candidates) { if (File.Exists(c)) return c; }
            return OnPath("node.exe");
        }

        /// <summary>Edge first (it is the Windows default and always present),
        /// then Chrome; both support the --app window mode we need.</summary>
        public static string BrowserExe()
        {
            string[] candidates = new string[] {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
            };
            foreach (string c in candidates) { if (File.Exists(c)) return c; }
            return null;
        }

        private static string OnPath(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (path == null) return null;
            foreach (string dir in path.Split(';'))
            {
                if (dir.Length == 0) continue;
                try
                {
                    string full = Path.Combine(dir.Trim('"'), exe);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }
    }

    #endregion

    #region win32

    internal static class Native
    {
        // ---- job object: kill-on-close ownership of every child we start ----

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint PROCESS_SET_QUOTA = 0x0100;

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryLimit, PeakJobMemoryLimit;
        }

        private static IntPtr job = IntPtr.Zero;

        public static void InitJob()
        {
            if (job != IntPtr.Zero) return;
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return;

            JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static void Adopt(Process process)
        {
            if (job == IntPtr.Zero || process == null) return;
            try { if (!process.HasExited) AssignProcessToJobObject(job, process.Handle); }
            catch { }
        }

        /// <summary>Adopt a process we did not start (a backend left behind by an
        /// earlier run) so the kill-on-close guarantee covers it too. Nested jobs
        /// are supported from Windows 8 on; failure is not fatal because
        /// <see cref="Backend.StopAll"/> still sweeps by command line.</summary>
        public static void AdoptById(int pid)
        {
            if (job == IntPtr.Zero) return;
            IntPtr handle = OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE, false, pid);
            if (handle == IntPtr.Zero) return;
            try { AssignProcessToJobObject(job, handle); }
            finally { CloseHandle(handle); }
        }

        public static void CloseJob()
        {
            if (job == IntPtr.Zero) return;
            CloseHandle(job);
            job = IntPtr.Zero;
        }

        // ---- window activation: focus the app window instead of opening a second one ----

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;
        private const uint GW_OWNER = 4;

        /// <summary>Restore and focus the app window belonging to one of
        /// <paramref name="pids"/>. Returns false when there is none.
        ///
        /// A browser process also owns popups — translate prompts, permission
        /// bubbles — which share the app's window class and would otherwise be
        /// matched first. Those have an owner window; the real app window does not,
        /// and is the largest of the candidates.</summary>
        public static bool FocusWindowOf(HashSet<int> pids)
        {
            if (pids == null || pids.Count == 0) return false;

            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd) || GetWindowTextLength(hWnd) == 0) return true;
                if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true; // a popup, not the app
                uint owner;
                GetWindowThreadProcessId(hWnd, out owner);
                if (!pids.Contains((int)owner)) return true;
                StringBuilder cls = new StringBuilder(64);
                GetClassName(hWnd, cls, cls.Capacity);
                // Chromium top-level windows; skip the hidden helper classes.
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;

                RECT rect;
                if (!GetWindowRect(hWnd, out rect)) return true;
                long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area <= bestArea) return true;
                bestArea = area;
                best = hWnd;
                return true;
            }, IntPtr.Zero);

            if (best == IntPtr.Zero) return false;
            ShowWindow(best, IsIconic(best) ? SW_RESTORE : SW_SHOW);
            ForceForeground(best);
            return true;
        }

        /// <summary>Raise a window past the foreground lock. Windows only lets the
        /// process that owns the foreground hand it over, so we briefly join that
        /// thread's input queue — the standard way for a tray app to bring its own
        /// window forward on demand.</summary>
        private static void ForceForeground(IntPtr hWnd)
        {
            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == hWnd) return;

                uint ignored;
                uint foregroundThread = GetWindowThreadProcessId(foreground, out ignored);
                uint ourThread = GetCurrentThreadId();
                bool attached = foregroundThread != 0
                                && foregroundThread != ourThread
                                && AttachThreadInput(ourThread, foregroundThread, true);
                try
                {
                    BringWindowToTop(hWnd);
                    SetForegroundWindow(hWnd);
                }
                finally
                {
                    if (attached) AttachThreadInput(ourThread, foregroundThread, false);
                }
            }
            catch { }
        }

        /// <summary>Let another process take the foreground. A second launch calls
        /// this for the running tray before signalling it, so the window it raises
        /// is allowed to come to the front.</summary>
        public static void AllowForeground(int processId)
        {
            try { AllowSetForegroundWindow(processId); } catch { }
        }

        // ---- who owns the listening port ----

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order,
            int addressFamily, int tableClass, int reserved);

        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

        /// <summary>PIDs listening on <paramref name="port"/> over IPv4 loopback.</summary>
        public static List<int> ListenerPids(int port)
        {
            List<int> pids = new List<int>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (size <= 0) return pids;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                    return pids;
                int rows = Marshal.ReadInt32(buffer);
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                int rowSize = 24;
                for (int i = 0; i < rows; i++)
                {
                    IntPtr row = new IntPtr(buffer.ToInt64() + 4 + i * rowSize);
                    byte[] portBytes = new byte[4];
                    Marshal.Copy(new IntPtr(row.ToInt64() + 8), portBytes, 0, 4);
                    int rowPort = (portBytes[0] << 8) + portBytes[1]; // network byte order
                    if (rowPort != port) continue;
                    int pid = Marshal.ReadInt32(new IntPtr(row.ToInt64() + 20));
                    if (!pids.Contains(pid)) pids.Add(pid);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return pids;
        }

        // ---- DPI: crisp tray icon and context menu on scaled displays ----

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int awareness);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        public static void MakeDpiAware()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { } // PER_MONITOR_AWARE_V2
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }                // PROCESS_PER_MONITOR_DPI_AWARE
            try { SetProcessDPIAware(); } catch { }
        }
    }

    #endregion

    #region process helpers

    internal struct ProcInfo
    {
        public int Pid;
        public int Parent;
        public string Name;
        public string CommandLine;
    }

    internal static class Procs
    {
        /// <summary>One WMI sweep of the process table; command lines included so
        /// callers can identify our own node/browser processes precisely.</summary>
        public static List<ProcInfo> Snapshot()
        {
            List<ProcInfo> list = new List<ProcInfo>();
            try
            {
                using (ManagementObjectSearcher search = new ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process"))
                using (ManagementObjectCollection results = search.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        ProcInfo info = new ProcInfo();
                        try
                        {
                            info.Pid = Convert.ToInt32(row["ProcessId"]);
                            info.Parent = Convert.ToInt32(row["ParentProcessId"]);
                            object name = row["Name"];
                            object cmd = row["CommandLine"];
                            info.Name = name == null ? "" : name.ToString();
                            info.CommandLine = cmd == null ? "" : cmd.ToString();
                            list.Add(info);
                        }
                        catch { }
                        finally { row.Dispose(); }
                    }
                }
            }
            catch { }
            return list;
        }

        public static List<int> Matching(List<ProcInfo> snapshot, string exeName, string cmdlineContains)
        {
            List<int> pids = new List<int>();
            foreach (ProcInfo p in snapshot)
            {
                if (!string.Equals(p.Name, exeName, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.CommandLine.IndexOf(cmdlineContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                pids.Add(p.Pid);
            }
            return pids;
        }

        /// <summary>Kill a process and every descendant, children first so nothing
        /// is reparented away mid-sweep. Tool shells, worker helpers and headless
        /// browsers the harness spawned all hang below the node process.</summary>
        public static void KillTree(List<ProcInfo> snapshot, int pid)
        {
            foreach (ProcInfo child in snapshot)
            {
                if (child.Parent == pid && child.Pid != pid) KillTree(snapshot, child.Pid);
            }
            Kill(pid);
        }

        public static void Kill(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (!p.HasExited) p.Kill();
                }
            }
            catch { }
        }

        /// <summary>Ask a process to close its window, then wait briefly. Processes
        /// without a window — a browser's renderer and GPU helpers, of which there
        /// are a dozen — return immediately; waiting on each of those would turn
        /// "quit" into a twenty-second stall. They go down with the tree kill.</summary>
        public static bool CloseGracefully(int pid, int waitMs)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (p.HasExited) return true;
                    if (p.MainWindowHandle == IntPtr.Zero) return false;
                    p.CloseMainWindow();
                    return p.WaitForExit(waitMs);
                }
            }
            catch { return true; }
        }
    }

    #endregion

    #region backend

    internal static class Backend
    {
        public const int DefaultPort = 3080;

        /// <summary>The port dsh serves on. Override it by putting a single
        /// `port=8080` line in `tray.settings` next to the executable, for people
        /// whose web profile is not on the stock port.</summary>
        public static readonly int Port = ReadPortSetting();

        public static readonly string Url = "http://127.0.0.1:" + Port;

        private static int ReadPortSetting()
        {
            try
            {
                string file = Paths.In("tray.settings");
                if (!File.Exists(file)) return DefaultPort;
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int equals = line.IndexOf('=');
                    if (equals <= 0) continue;
                    if (!string.Equals(line.Substring(0, equals).Trim(), "port", StringComparison.OrdinalIgnoreCase)) continue;
                    int value;
                    if (int.TryParse(line.Substring(equals + 1).Trim(), out value) && value > 0 && value <= 65535)
                        return value;
                }
            }
            catch { }
            return DefaultPort;
        }

        /// <summary>Substring that identifies a harness backend among all node.exe
        /// processes — deliberately the installed bin path, so unrelated node
        /// processes (editors, MCP servers, build tools) are never touched.</summary>
        private static readonly string Fingerprint = Path.Combine("@deepseek-ai", "dsh") + Path.DirectorySeparatorChar + "lib" + Path.DirectorySeparatorChar + "bin.js";

        private static readonly object LogLock = new object();
        private static StreamWriter logWriter;

        /// <summary>Held for the process lifetime: the async pipe readers below live
        /// on this object, and letting it be collected would stall the backend the
        /// moment its output buffer filled.</summary>
        private static Process backendProcess;

        public static bool IsListening()
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("127.0.0.1", Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(400, false)) return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>True when this command line is a dsh *web* server — the surface
        /// this tray owns and the only one it will ever allow a second copy of. A
        /// `dsh --profile headless` or `--profile tui` run is somebody else's work
        /// and is deliberately left alone.</summary>
        public static bool IsWebBackend(string commandLine)
        {
            if (commandLine == null) return false;
            int at = commandLine.IndexOf(Fingerprint, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;

            string args = commandLine.Substring(at + Fingerprint.Length);
            if (args.IndexOf("--profile web", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // `dsh web ...`: the alias is the first token after the script path.
            string[] tokens = args.Split(new char[] { ' ', '"', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 0 && string.Equals(tokens[0], "web", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Every dsh web backend currently running.</summary>
        private static List<int> WebBackends(List<ProcInfo> snapshot)
        {
            List<int> pids = new List<int>();
            foreach (ProcInfo p in snapshot)
            {
                if (!string.Equals(p.Name, "node.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsWebBackend(p.CommandLine)) continue;
                pids.Add(p.Pid);
            }
            return pids;
        }

        /// <summary>Collapse whatever is running down to a single backend on
        /// <see cref="Port"/> and adopt it. Anything else — a second instance, or one
        /// left on a different port — is stopped, so the machine can never end up
        /// carrying several harnesses. Returns the surviving pid, or 0 when nothing
        /// usable was found (in which case every stray instance is now stopped).</summary>
        public static int Reconcile()
        {
            List<ProcInfo> snapshot = Procs.Snapshot();
            List<int> backends = WebBackends(snapshot);
            if (backends.Count == 0) return 0;

            // The one holding the canonical port wins; without it there is no
            // instance this tray can address, so none of them are kept.
            int primary = 0;
            foreach (int pid in Native.ListenerPids(Port))
            {
                if (!backends.Contains(pid)) continue;
                primary = pid;
                break;
            }

            foreach (int pid in backends)
            {
                if (pid == primary) continue;
                Log("tray: stopping a duplicate dsh web backend (pid " + pid + ")");
                Procs.KillTree(snapshot, pid);
            }

            if (primary != 0) Native.AdoptById(primary);
            return primary;
        }

        /// <summary>Periodic guard: if a second `dsh web` appeared alongside ours —
        /// started from a terminal, a script, or a stale scheduled task — stop it and
        /// report how many were dropped. Ours (the port owner) is never the victim,
        /// and a run on another profile is not touched at all.</summary>
        public static int DropDuplicates()
        {
            List<ProcInfo> snapshot = Procs.Snapshot();
            List<int> backends = WebBackends(snapshot);
            if (backends.Count < 2) return 0;

            int primary = 0;
            foreach (int pid in Native.ListenerPids(Port))
            {
                if (!backends.Contains(pid)) continue;
                primary = pid;
                break;
            }
            // Nothing of ours on the port: this is a restart in flight, not a
            // duplicate. Leave it to Ensure/Reconcile rather than killing blind.
            if (primary == 0) return 0;

            int dropped = 0;
            foreach (int pid in backends)
            {
                if (pid == primary) continue;
                Log("tray: stopping a duplicate dsh web backend (pid " + pid + ")");
                Procs.KillTree(snapshot, pid);
                dropped++;
            }
            return dropped;
        }

        /// <summary>Bring up exactly one backend on the canonical port: adopt the
        /// running one if there is one, otherwise start it. Returns true once the
        /// port answers.</summary>
        public static bool Ensure()
        {
            if (Reconcile() != 0 && IsListening()) return true;

            string node = Paths.NodeExe();
            if (node == null || !File.Exists(Paths.DshBin)) return false;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = node;
                // --no-open: the app window is opened by this tray, never a browser tab.
                psi.Arguments = string.Format("\"{0}\" web --no-open --port {1}", Paths.DshBin, Port);
                psi.WorkingDirectory = Paths.AppDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                Process p = Process.Start(psi);
                if (p == null) return false;
                backendProcess = p;
                Native.Adopt(p);
                StartLogging(p);
            }
            catch (Exception ex)
            {
                Log("tray: could not start the backend: " + ex.Message);
                return false;
            }

            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(250);
                if (IsListening()) return true;
            }
            return false;
        }

        /// <summary>Stop every harness web backend: the port owner's tree first, then
        /// a command-line sweep that catches instances started by the update task or
        /// from a terminal.</summary>
        public static void StopAll()
        {
            List<ProcInfo> snapshot = Procs.Snapshot();
            List<int> targets = new List<int>();

            foreach (int pid in Native.ListenerPids(Port)) if (!targets.Contains(pid)) targets.Add(pid);
            foreach (int pid in WebBackends(snapshot)) if (!targets.Contains(pid)) targets.Add(pid);

            foreach (int pid in targets) Procs.KillTree(snapshot, pid);

            // Anything still holding the port after the sweep gets one more pass.
            for (int i = 0; i < 8 && IsListening(); i++)
            {
                Thread.Sleep(150);
                foreach (int pid in Native.ListenerPids(Port)) Procs.Kill(pid);
            }
            StopLogging();
        }

        public static void Restart()
        {
            StopAll();
            Thread.Sleep(600);
            Ensure();
        }

        // ---- log tail ----

        private static void StartLogging(Process p)
        {
            try
            {
                Directory.CreateDirectory(Paths.LogDir);
                FileInfo existing = new FileInfo(Paths.LogFile);
                if (existing.Exists && existing.Length > 4 * 1024 * 1024)
                {
                    string rolled = Paths.LogFile + ".1";
                    if (File.Exists(rolled)) File.Delete(rolled);
                    File.Move(Paths.LogFile, rolled);
                }
                lock (LogLock)
                {
                    if (logWriter != null) { logWriter.Dispose(); logWriter = null; }
                    FileStream stream = new FileStream(Paths.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    logWriter = new StreamWriter(stream, new UTF8Encoding(false));
                    logWriter.AutoFlush = true;
                }
                Log("=== dsh web started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " (pid " + p.Id + ") ===");
            }
            catch { }

            // The pipes must be drained or the backend blocks once they fill.
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Log(e.Data); };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Log(e.Data); };
            try { p.BeginOutputReadLine(); p.BeginErrorReadLine(); } catch { }
        }

        private static void StopLogging()
        {
            lock (LogLock)
            {
                if (logWriter == null) return;
                try { logWriter.Dispose(); } catch { }
                logWriter = null;
            }
        }

        public static void Log(string line)
        {
            lock (LogLock)
            {
                if (logWriter == null) return;
                try { logWriter.WriteLine(line); } catch { }
            }
        }
    }

    #endregion

    #region app window

    internal static class AppWindow
    {
        /// <summary>Unique enough to identify our windows among all browser
        /// processes without matching the user's ordinary browsing.</summary>
        private static string ProfileArgument
        {
            get { return "--user-data-dir=\"" + Paths.BrowserProfile + "\""; }
        }

        /// <summary>The browser process this tray launched. Lets a tray click focus
        /// the window without paying for a full process-table sweep.</summary>
        private static int lastBrowserPid;

        public static HashSet<int> Pids(List<ProcInfo> snapshot)
        {
            HashSet<int> pids = new HashSet<int>();
            foreach (ProcInfo p in snapshot)
            {
                bool browser = string.Equals(p.Name, "msedge.exe", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(p.Name, "chrome.exe", StringComparison.OrdinalIgnoreCase);
                if (!browser) continue;
                if (p.CommandLine.IndexOf(Paths.BrowserProfile, StringComparison.OrdinalIgnoreCase) < 0) continue;
                pids.Add(p.Pid);
            }
            return pids;
        }

        /// <summary>Focus the existing window, or open one. Never opens a second
        /// window for an app that is already on screen.</summary>
        public static void OpenOrFocus()
        {
            if (FocusKnownWindow()) return;
            if (Native.FocusWindowOf(Pids(Procs.Snapshot()))) return;

            string browser = Paths.BrowserExe();
            if (browser == null) { OpenInDefaultBrowser(); return; }

            try
            {
                Directory.CreateDirectory(Paths.BrowserProfile);
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = browser;
                psi.Arguments = string.Join(" ", new string[] {
                    "--app=" + Backend.Url,
                    ProfileArgument,
                    "--profile-directory=Default",
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--no-service-autorun",
                    "--disable-background-mode",          // no browser process lingering after close
                    "--disable-session-crashed-bubble",   // we may have force-closed it last time
                    "--hide-crash-restore-bubble",
                    // This is an app window, not a web page: no "translate this
                    // page?" prompt, no first-run promos over the UI.
                    "--disable-features=Translate,TranslateUI,msEdgeTranslate",
                    "--window-size=1440,940",
                });
                psi.UseShellExecute = false;
                Process p = Process.Start(psi);
                Native.Adopt(p);
                if (p != null) lastBrowserPid = p.Id;
            }
            catch { OpenInDefaultBrowser(); }
        }

        private static bool FocusKnownWindow()
        {
            int pid = lastBrowserPid;
            if (pid == 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (p.HasExited) { lastBrowserPid = 0; return false; }
                }
            }
            catch { lastBrowserPid = 0; return false; }

            HashSet<int> one = new HashSet<int>();
            one.Add(pid);
            return Native.FocusWindowOf(one);
        }

        public static void OpenInDefaultBrowser()
        {
            try { Process.Start(Backend.Url); } catch { }
        }

        /// <summary>Close our app windows: ask first, force after a grace period.
        /// Only processes carrying our private profile path are touched.</summary>
        public static void CloseAll()
        {
            lastBrowserPid = 0;
            List<ProcInfo> snapshot = Procs.Snapshot();
            HashSet<int> pids = Pids(snapshot);
            if (pids.Count == 0) return;

            foreach (int pid in pids) Procs.CloseGracefully(pid, 1200);

            List<ProcInfo> after = Procs.Snapshot();
            foreach (int pid in AppWindow.Pids(after)) Procs.KillTree(after, pid);
        }
    }

    #endregion

    #region autostart

    internal static class AutoStart
    {
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DeepSeek Harness";

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    {
                        if (key == null) return false;
                        object value = key.GetValue(ValueName);
                        return value != null && value.ToString().Length > 0;
                    }
                }
                catch { return false; }
            }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    if (enabled) key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" --tray");
                    else key.DeleteValue(ValueName, false);
                }
            }
            catch { }
        }
    }

    #endregion

    #region tray

    public class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon notifyIcon = new NotifyIcon();
        private readonly ToolStripLabel statusItem = new ToolStripLabel();
        private readonly ToolStripMenuItem restartItem = new ToolStripMenuItem("🔄  重启后台服务");
        private readonly ToolStripMenuItem autoStartItem = new ToolStripMenuItem("🚀  开机自动启动");
        private readonly System.Threading.Timer statusTimer;

        /// <summary>Slow guard that keeps the machine at one harness: it stops any
        /// second `dsh web` that appeared next to ours.</summary>
        private readonly System.Threading.Timer duplicateTimer;

        /// <summary>Never shown; it exists only to own a window handle this tray can
        /// marshal background-thread results onto.</summary>
        private readonly Form marshaller = new Form();

        private Icon iconOn, iconOff;
        private Image dotOn, dotOff, dotBusy;
        private volatile bool lastRunning;
        private volatile bool busy;
        private int cleaned;

        public TrayApp()
        {
            marshaller.ShowInTaskbar = false;
            IntPtr forceHandleCreation = marshaller.Handle;
            GC.KeepAlive(forceHandleCreation);

            Native.InitJob();
            LoadIcons();
            BuildMenu();

            ApplyState(false, "启动中…");
            notifyIcon.Visible = true;

            // Exit paths that bypass the menu still have to clean up.
            Application.ApplicationExit += delegate { Cleanup(); };
            SystemEvents.SessionEnding += delegate { Cleanup(); };
            AppDomain.CurrentDomain.ProcessExit += delegate { Cleanup(); };

            ListenForSecondInstance();

            statusTimer = new System.Threading.Timer(delegate { PollStatus(); }, null, Timeout.Infinite, Timeout.Infinite);
            duplicateTimer = new System.Threading.Timer(delegate { GuardSingleInstance(); }, null, Timeout.Infinite, Timeout.Infinite);

            RunInBackground(delegate
            {
                bool ok = Backend.Ensure();
                Post(delegate
                {
                    ApplyState(ok, null);
                    if (!ok)
                    {
                        notifyIcon.ShowBalloonTip(6000, "DeepSeek Harness",
                            "后台服务启动失败，请从托盘菜单查看运行日志。", ToolTipIcon.Error);
                    }
                });
                if (ok) AppWindow.OpenOrFocus();
                statusTimer.Change(2000, 5000);
                duplicateTimer.Change(60000, 60000);
            });
        }

        #region icons and menu

        private void LoadIcons()
        {
            iconOn = LoadIcon("tray.ico");
            iconOff = LoadIcon("tray-off.ico") ?? iconOn;
            dotOn = Dot(Color.FromArgb(46, 174, 94));
            dotOff = Dot(Color.FromArgb(214, 74, 74));
            dotBusy = Dot(Color.FromArgb(224, 160, 44));
        }

        /// <summary>Load the frame that matches the shell's small-icon metric, so
        /// the notification area shows a real 16/20/24 px rendering rather than a
        /// resampled 32 px one.</summary>
        private static Icon LoadIcon(string fileName)
        {
            string path = Paths.In(fileName);
            if (!File.Exists(path)) return null;
            try
            {
                Size wanted = SystemInformation.SmallIconSize;
                using (Icon full = new Icon(path))
                {
                    Icon sized = new Icon(full, wanted);
                    return sized;
                }
            }
            catch
            {
                try { return new Icon(path); } catch { return null; }
            }
        }

        private static Image Dot(Color color)
        {
            Bitmap bitmap = new Bitmap(14, 14);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(color)) g.FillEllipse(brush, 3, 3, 8, 8);
            }
            return bitmap;
        }

        private void BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = true;
            menu.Renderer = new ToolStripProfessionalRenderer();

            // A label, not a disabled menu item: the shell renders disabled item
            // images in grayscale, which would wash out the status dot.
            statusItem.Text = "启动中…";
            statusItem.Image = dotBusy;
            statusItem.ForeColor = SystemColors.GrayText;
            statusItem.Margin = new Padding(0, 2, 0, 2);

            ToolStripMenuItem open = new ToolStripMenuItem("🐳  打开 DeepSeek 界面");
            open.Font = new Font(open.Font, FontStyle.Bold);
            open.Click += delegate { RunInBackground(delegate { EnsureAndOpen(); }); };

            ToolStripMenuItem openInBrowser = new ToolStripMenuItem("🌐  在浏览器中打开");
            openInBrowser.Click += delegate { AppWindow.OpenInDefaultBrowser(); };

            restartItem.Click += delegate { RestartService(); };

            autoStartItem.CheckOnClick = true;
            autoStartItem.Checked = AutoStart.Enabled;
            autoStartItem.CheckedChanged += delegate { AutoStart.Set(autoStartItem.Checked); };

            ToolStripMenuItem config = new ToolStripMenuItem("⚙️  插件配置 (cordis.patch.yml)");
            config.Click += delegate { OpenWithShell(Paths.PatchFile, true); };

            ToolStripMenuItem home = new ToolStripMenuItem("📁  数据目录 (.dsh)");
            home.Click += delegate { OpenWithShell(Paths.DshHome, false); };

            ToolStripMenuItem log = new ToolStripMenuItem("📝  运行日志 (harness.log)");
            log.Click += delegate { OpenWithShell(Paths.LogFile, true); };

            ToolStripMenuItem exit = new ToolStripMenuItem("⏻  退出（关闭界面并停止后台）");
            exit.Click += delegate { ExitApp(); };

            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(open);
            menu.Items.Add(openInBrowser);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(restartItem);
            menu.Items.Add(autoStartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(config);
            menu.Items.Add(home);
            menu.Items.Add(log);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);

            // Refresh the checkmark if autostart was changed elsewhere.
            menu.Opening += delegate { autoStartItem.Checked = AutoStart.Enabled; };

            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) RunInBackground(delegate { EnsureAndOpen(); });
            };
        }

        #endregion

        #region state

        /// <summary>Reflect service state in the icon, the hover text and the menu
        /// header. NotifyIcon.Text is capped at 63 characters by the shell.</summary>
        private void ApplyState(bool running, string overrideLabel)
        {
            lastRunning = running;
            Icon icon = running ? iconOn : iconOff;
            if (icon != null) notifyIcon.Icon = icon;

            string label = overrideLabel != null
                ? overrideLabel
                : (running ? "服务运行中 · 127.0.0.1:" + Backend.Port : "服务已停止 · 端口 " + Backend.Port);

            statusItem.Text = label;
            statusItem.Image = overrideLabel != null ? dotBusy : (running ? dotOn : dotOff);

            string tip = "DeepSeek Harness · " + label;
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            notifyIcon.Text = tip;
        }

        private void PollStatus()
        {
            try
            {
                if (busy) return;
                bool running = Backend.IsListening();
                if (running == lastRunning) return;
                Post(delegate { if (!busy) ApplyState(running, null); });
            }
            catch { }
        }

        /// <summary>One harness per machine: stop any second `dsh web` that turned up
        /// beside ours and say so, rather than letting a forgotten instance sit there
        /// burning memory on a second port.</summary>
        private void GuardSingleInstance()
        {
            try
            {
                if (busy) return;
                int dropped = Backend.DropDuplicates();
                if (dropped == 0) return;
                Post(delegate
                {
                    notifyIcon.ShowBalloonTip(4000, "DeepSeek Harness",
                        dropped == 1
                            ? "检测到重复的后台服务，已自动关闭（只保留端口 " + Backend.Port + " 的这一个）。"
                            : "检测到 " + dropped + " 个重复的后台服务，已自动关闭（只保留端口 " + Backend.Port + " 的这一个）。",
                        ToolTipIcon.Info);
                });
            }
            catch { }
        }

        /// <summary>What every "open the app" gesture routes through: make sure the
        /// single backend is up, then focus its window — opening a second window, or
        /// a second backend, is never an outcome.</summary>
        private void EnsureAndOpen()
        {
            try
            {
                if (!busy && !Backend.IsListening())
                {
                    Post(delegate { if (!busy) ApplyState(false, "正在启动服务…"); });
                    bool ok = Backend.Ensure();
                    Post(delegate
                    {
                        if (busy) return;
                        ApplyState(ok, null);
                        if (!ok)
                        {
                            notifyIcon.ShowBalloonTip(6000, "DeepSeek Harness",
                                "后台服务启动失败，请从托盘菜单查看运行日志。", ToolTipIcon.Error);
                        }
                    });
                    if (!ok) return;
                }
                AppWindow.OpenOrFocus();
            }
            catch { }
        }

        private void RestartService()
        {
            if (busy) return;
            busy = true;
            restartItem.Enabled = false;
            ApplyState(false, "正在重启服务…");

            RunInBackground(delegate
            {
                Backend.Restart();
                bool ok = Backend.IsListening();
                Post(delegate
                {
                    busy = false;
                    restartItem.Enabled = true;
                    ApplyState(ok, null);
                    notifyIcon.ShowBalloonTip(3000, "DeepSeek Harness",
                        ok ? "后台服务已重启，插件配置已重新加载。" : "重启失败，请查看运行日志。",
                        ok ? ToolTipIcon.Info : ToolTipIcon.Error);
                });
            });
        }

        #endregion

        #region lifecycle

        /// <summary>A second launch signals this instance instead of adding another
        /// icon to the notification area.</summary>
        private void ListenForSecondInstance()
        {
            Thread thread = new Thread(delegate()
            {
                EventWaitHandle signal;
                try { signal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowUiEventName); }
                catch { return; }
                while (true)
                {
                    try { signal.WaitOne(); } catch { return; }
                    RunInBackground(delegate { EnsureAndOpen(); });
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void ExitApp()
        {
            // Icon first: the user asked to quit, so the notification area should
            // clear immediately rather than after the teardown sweep.
            try { notifyIcon.Visible = false; } catch { }
            Cleanup();
            try { notifyIcon.Dispose(); } catch { }
            try { marshaller.Dispose(); } catch { }
            ExitThread();
        }

        /// <summary>The exit contract: app window down, backend down, job closed.
        /// Safe to call from any exit path; runs once.</summary>
        private void Cleanup()
        {
            if (Interlocked.Exchange(ref cleaned, 1) == 1) return;
            try { if (statusTimer != null) statusTimer.Dispose(); } catch { }
            try { if (duplicateTimer != null) duplicateTimer.Dispose(); } catch { }
            try { AppWindow.CloseAll(); } catch { }
            try { Backend.StopAll(); } catch { }
            try { Native.CloseJob(); } catch { }
        }

        private static void OpenWithShell(string path, bool isFile)
        {
            try
            {
                if (isFile && !File.Exists(path)) return;
                if (!isFile && !Directory.Exists(path)) return;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>Hand a result back to the UI thread. Once teardown has started
        /// the result is dropped: a late status update touching a disposed
        /// NotifyIcon would throw on a pool thread and take the process with it.</summary>
        private void Post(ThreadStart action)
        {
            if (Interlocked.CompareExchange(ref cleaned, 0, 0) == 1) return;
            try
            {
                if (marshaller.IsHandleCreated && !marshaller.IsDisposed)
                {
                    marshaller.BeginInvoke(action);
                    return;
                }
            }
            catch { return; }
            try { action(); } catch { }
        }

        private static void RunInBackground(ThreadStart action)
        {
            Thread thread = new Thread(action);
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        #endregion
    }

    #endregion

    public class Program
    {
        public const string MutexName = @"Local\DeepSeekHarnessTray";
        public const string ShowUiEventName = @"Local\DeepSeekHarnessTrayShowUI";

        [STAThread]
        public static void Main()
        {
            Native.MakeDpiAware();

            // One tray, therefore one backend, therefore one port. A second launch
            // never starts anything — it hands the running instance a "show the
            // window" signal and exits. If the mutex exists but cannot be opened,
            // assume an instance is up rather than racing it into a second harness.
            bool createdNew = false;
            Mutex mutex = null;
            try { mutex = new Mutex(true, MutexName, out createdNew); }
            catch (UnauthorizedAccessException) { createdNew = false; }
            catch { createdNew = true; }

            if (!createdNew)
            {
                try
                {
                    // Hand our foreground rights to the instance that owns the
                    // window, or its raise attempt is silently ignored.
                    int self = Process.GetCurrentProcess().Id;
                    foreach (Process other in Process.GetProcessesByName("DeepSeekTray"))
                    {
                        using (other) { if (other.Id != self) Native.AllowForeground(other.Id); }
                    }
                    EventWaitHandle signal = EventWaitHandle.OpenExisting(ShowUiEventName);
                    signal.Set();
                }
                catch
                {
                    // The instance is there but not listening for the signal yet;
                    // opening the window directly still cannot spawn a backend.
                    AppWindow.OpenOrFocus();
                }
                if (mutex != null) mutex.Close();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Backend.Log("tray: " + e.Exception);
            };

            try { Application.Run(new TrayApp()); }
            finally { if (mutex != null) { try { mutex.ReleaseMutex(); } catch { } mutex.Close(); } }
        }
    }
}
