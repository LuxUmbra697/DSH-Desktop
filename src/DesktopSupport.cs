// DSH Desktop - support layer: configuration, plugins, logging, process supervision.
//
// The launcher compiles with the in-box C# 5 compiler (csc.exe 4.8), so this
// file avoids syntax newer than C# 5: no interpolated strings, no null
// conditional operator, no expression bodied members, no nameof.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DshDesktop
{
    /// <summary>User editable launcher settings, read from config.json next to the executable.</summary>
    [DataContract]
    public sealed class DesktopConfig
    {
        [DataMember(Name = "title")] public string Title = "DSH Desktop";
        [DataMember(Name = "host")] public string Host = "127.0.0.1";
        [DataMember(Name = "port")] public int Port = 0;
        [DataMember(Name = "trustedHosts")] public string[] TrustedHosts = new string[0];
        [DataMember(Name = "dshHome")] public string DshHome = "";
        [DataMember(Name = "workspace")] public string Workspace = "";
        [DataMember(Name = "width")] public int Width = 1280;
        [DataMember(Name = "height")] public int Height = 840;
        [DataMember(Name = "minWidth")] public int MinWidth = 420;
        [DataMember(Name = "minHeight")] public int MinHeight = 320;
        [DataMember(Name = "maximizeOnSmallScreen")] public bool MaximizeOnSmallScreen = true;
        [DataMember(Name = "lan")] public bool Lan = false;
        [DataMember(Name = "devTools")] public bool DevTools = true;
        [DataMember(Name = "openExternalLinks")] public bool OpenExternalLinks = true;
        [DataMember(Name = "pluginsDisabled")] public bool PluginsDisabled = false;
        [DataMember(Name = "startupTimeoutSeconds")] public int StartupTimeoutSeconds = 240;
        [DataMember(Name = "extraArgs")] public string[] ExtraArgs = new string[0];
        [DataMember(Name = "env")] public Dictionary<string, string> Env = new Dictionary<string, string>();
        [DataMember(Name = "zoomFactor")] public double ZoomFactor = 1.0;
        /// <summary>"auto" (bundled first, then system), "bundled", or "system".</summary>
        [DataMember(Name = "nodeMode")] public string NodeMode = "auto";
        /// <summary>Explicit node.exe to use; empty means detect.</summary>
        [DataMember(Name = "nodePath")] public string NodePath = "";
        /// <summary>npm dist-tag channel to follow: "next" (includes prereleases) or "latest".</summary>
        [DataMember(Name = "channel")] public string Channel = "next";
        /// <summary>Null means the key was omitted, which checks on startup.</summary>
        [DataMember(Name = "checkUpdatesOnStartup")] public bool? CheckUpdatesOnStartup;
        /// <summary>Null means the key was omitted, which never installs without asking.</summary>
        [DataMember(Name = "autoUpdate")] public bool? AutoUpdate;
        /// <summary>Null means the key was omitted, which shows the menu bar.</summary>
        [DataMember(Name = "showMenuBar")] public bool? ShowMenuBar;
        /// <summary>Null means the key was omitted, which shows the status bar.</summary>
        [DataMember(Name = "showStatusBar")] public bool? ShowStatusBar;
    }

    /// <summary>One plugin directory's launcher.json. Every field is optional.</summary>
    [DataContract]
    public sealed class PluginManifest
    {
        [DataMember(Name = "name")] public string Name = "";
        [DataMember(Name = "description")] public string Description = "";
        /// <summary>Null means the manifest omitted the key, which is enabled.</summary>
        [DataMember(Name = "enabled")] public bool? Enabled;
        [DataMember(Name = "title")] public string Title = "";
        [DataMember(Name = "width")] public int Width = -1;
        [DataMember(Name = "height")] public int Height = -1;
        [DataMember(Name = "minWidth")] public int MinWidth = -1;
        [DataMember(Name = "minHeight")] public int MinHeight = -1;
        [DataMember(Name = "zoomFactor")] public double ZoomFactor = -1;
        [DataMember(Name = "injectCss")] public string[] InjectCss = new string[0];
        [DataMember(Name = "injectJs")] public string[] InjectJs = new string[0];
        [DataMember(Name = "env")] public Dictionary<string, string> Env = new Dictionary<string, string>();
        [DataMember(Name = "args")] public string[] Args = new string[0];
        [DataMember(Name = "trustedHosts")] public string[] TrustedHosts = new string[0];
        [DataMember(Name = "dshPatch")] public string[] DshPatch = new string[0];
        [DataMember(Name = "dshPlugins")] public string[] DshPlugins = new string[0];
        /// <summary>Plugin-relative directory whose node_modules links the bundled runtime tree.</summary>
        [DataMember(Name = "dshLinkModules")] public string DshLinkModules = "";
        [DataMember(Name = "userAgentSuffix")] public string UserAgentSuffix = "";
    }

    /// <summary>A plugin directory with a loaded manifest and resolved file paths.</summary>
    public sealed class LoadedPlugin
    {
        public string Id;
        public string Directory;
        public PluginManifest Manifest;
    }

    /// <summary>Parses JSON into a contract type without pulling in an external serializer.</summary>
    public static class Json
    {
        public static T Read<T>(string path) where T : class
        {
            byte[] bytes = File.ReadAllBytes(path);
            // Editors on Windows happily write a UTF-8 BOM; strip it so a
            // hand-edited config.json or launcher.json still loads.
            int offset = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                offset = 3;
            }
            using (MemoryStream stream = new MemoryStream(bytes, offset, bytes.Length - offset))
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                return (T)serializer.ReadObject(stream);
            }
        }

        public static void Write<T>(string path, T value)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (FileStream fs = File.Create(path))
            {
                serializer.WriteObject(fs, value);
            }
        }
    }

    /// <summary>Append only log with size rotation, kept next to the executable.</summary>
    public sealed class Logger
    {
        private readonly string directory;
        private readonly object gate = new object();
        private const long MaxBytes = 4L * 1024L * 1024L;
        private const int KeepFiles = 3;

        public Logger(string directory)
        {
            this.directory = directory;
            Directory.CreateDirectory(directory);
        }

        public string CurrentFile
        {
            get { return Path.Combine(directory, "dsh-desktop.log"); }
        }

        public void Info(string message)
        {
            Write("INFO ", message);
        }

        public void Warn(string message)
        {
            Write("WARN ", message);
        }

        public void Error(string message)
        {
            Write("ERROR", message);
        }

        public void Write(string level, string message)
        {
            lock (gate)
            {
                try
                {
                    Rotate();
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + " [" + level + "] " + message + Environment.NewLine;
                    File.AppendAllText(CurrentFile, line, new UTF8Encoding(false));
                }
                catch (IOException)
                {
                    // Logging never blocks startup; a locked file loses the line.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private void Rotate()
        {
            FileInfo info = new FileInfo(CurrentFile);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }
            for (int i = KeepFiles - 1; i >= 1; i--)
            {
                string from = CurrentFile + "." + i.ToString(CultureInfo.InvariantCulture);
                string to = CurrentFile + "." + (i + 1).ToString(CultureInfo.InvariantCulture);
                if (File.Exists(from))
                {
                    if (File.Exists(to))
                    {
                        File.Delete(to);
                    }
                    File.Move(from, to);
                }
            }
            File.Move(CurrentFile, CurrentFile + ".1");
        }
    }

    /// <summary>
    /// Owns the bundled Node runtime: builds the argv and environment, streams
    /// stdout for the serving URL, and guarantees the whole process tree dies
    /// with the launcher.
    /// </summary>
    public sealed class DshServer : IDisposable
    {
        private static readonly Regex UrlPattern = new Regex(@"https?://[^\s""']+", RegexOptions.Compiled);

        private readonly Logger log;
        private readonly JobObject job = new JobObject();
        private Process process;
        private readonly StringBuilder tail = new StringBuilder();
        private readonly object tailGate = new object();
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);
        private string url;

        public DshServer(Logger log)
        {
            this.log = log;
        }

        public string Url
        {
            get { return url; }
        }

        public string OutputTail
        {
            get { lock (tailGate) { return tail.ToString(); } }
        }

        public bool HasExited
        {
            get { return process == null || process.HasExited; }
        }

        public int ExitCode
        {
            get
            {
                try
                {
                    return process == null ? -1 : process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    return -1;
                }
            }
        }

        /// <summary>Starts node with the given arguments and waits for the serving URL line.</summary>
        public bool Start(string nodeExe, string entryScript, string workingDirectory, string[] arguments,
            Dictionary<string, string> environment, int timeoutSeconds)
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = nodeExe;
            info.Arguments = Quote(entryScript) + " " + Join(arguments);
            info.WorkingDirectory = workingDirectory;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = new UTF8Encoding(false);
            info.StandardErrorEncoding = new UTF8Encoding(false);
            if (environment != null)
            {
                foreach (KeyValuePair<string, string> pair in environment)
                {
                    info.EnvironmentVariables[pair.Key] = pair.Value;
                }
            }

            log.Info("spawn: " + nodeExe + " " + info.Arguments);
            process = new Process();
            process.StartInfo = info;
            process.OutputDataReceived += OnStdout;
            process.ErrorDataReceived += OnStderr;
            process.EnableRaisingEvents = true;
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                log.Error("failed to start node: " + ex.Message);
                AppendTail("failed to start node: " + ex.Message);
                return false;
            }

            job.Attach(process);
            log.Info("spawn pid=" + process.Id.ToString(CultureInfo.InvariantCulture));
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            bool ok = ready.Wait(TimeSpan.FromSeconds(timeoutSeconds));
            if (!ok)
            {
                log.Error("timed out waiting for the serving URL after " + timeoutSeconds + "s");
            }
            return ok && !string.IsNullOrEmpty(url);
        }

        private void OnStdout(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
            {
                return;
            }
            log.Info("dsh: " + e.Data);
            AppendTail(e.Data);
            Match match = UrlPattern.Match(e.Data);
            if (!match.Success || url != null)
            {
                return;
            }
            url = match.Value;
            ready.Set();
        }

        private void OnStderr(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
            {
                return;
            }
            log.Warn("dsh!: " + e.Data);
            AppendTail(e.Data);
        }

        private void AppendTail(string line)
        {
            lock (tailGate)
            {
                tail.AppendLine(line);
                if (tail.Length > 8000)
                {
                    tail.Remove(0, tail.Length - 8000);
                }
            }
        }

        /// <summary>Terminates the process tree, preferring the job object over taskkill.</summary>
        public void Stop()
        {
            if (process == null)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    log.Info("stopping dsh server pid=" + process.Id.ToString(CultureInfo.InvariantCulture));
                    job.Terminate();
                    if (!process.WaitForExit(4000))
                    {
                        KillTree(process.Id);
                        process.WaitForExit(4000);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch (InvalidOperationException)
                {
                }
                process = null;
            }
        }

        public static void KillTree(int pid)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("taskkill", "/PID " + pid.ToString(CultureInfo.InvariantCulture) + " /T /F");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                Process child = Process.Start(info);
                if (child != null)
                {
                    child.WaitForExit(6000);
                    child.Dispose();
                }
            }
            catch (Exception)
            {
                // Best effort: the job object already covered the common case.
            }
        }

        public void Dispose()
        {
            Stop();
            job.Dispose();
            ready.Dispose();
        }

        public static string Quote(string value)
        {
            if (value.IndexOf(' ') < 0 && value.IndexOf('"') < 0)
            {
                return value;
            }
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        public static string Join(string[] values)
        {
            List<string> parts = new List<string>();
            foreach (string value in values)
            {
                parts.Add(Quote(value));
            }
            return string.Join(" ", parts.ToArray());
        }
    }

    /// <summary>
    /// Windows job object with kill-on-close, so closing the launcher never
    /// leaves an orphaned node, shell, or subagent process behind.
    /// </summary>
    public sealed class JobObject : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private IntPtr handle = IntPtr.Zero;
        private bool usable;

        public JobObject()
        {
            try
            {
                handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                {
                    return;
                }
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                int size = Marshal.SizeOf(info);
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, buffer, false);
                    usable = SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception)
            {
                usable = false;
            }
        }

        public void Attach(Process process)
        {
            if (!usable || handle == IntPtr.Zero || process == null)
            {
                return;
            }
            try
            {
                AssignProcessToJobObject(handle, process.Handle);
            }
            catch (Exception)
            {
                // Nested jobs are unavailable in some hosts; Stop() falls back to taskkill.
            }
        }

        public void Terminate()
        {
            if (handle != IntPtr.Zero)
            {
                TerminateJobObject(handle, 1);
            }
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }

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
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>Window geometry remembered between runs.</summary>
    [DataContract]
    public sealed class WindowState
    {
        [DataMember(Name = "x")] public int X = int.MinValue;
        [DataMember(Name = "y")] public int Y = int.MinValue;
        [DataMember(Name = "width")] public int Width = 0;
        [DataMember(Name = "height")] public int Height = 0;
        [DataMember(Name = "maximized")] public bool Maximized = false;
    }

    /// <summary>Path and environment resolution for one launcher installation.</summary>
    public sealed class AppPaths
    {
        public string Root;
        public string NodeExe;
        public string DshEntry;
        public string WebView2Directory;
        public string PluginsDirectory;
        public string DataDirectory;
        public string LogsDirectory;
        public string CacheDirectory;

        public static AppPaths Resolve()
        {
            AppPaths paths = new AppPaths();
            paths.Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            paths.NodeExe = Path.Combine(paths.Root, Path.Combine("runtime", "node.exe"));
            paths.DshEntry = Path.Combine(paths.Root, Path.Combine("runtime", Path.Combine("app", Path.Combine("node_modules", Path.Combine("@deepseek-ai", Path.Combine("dsh", Path.Combine("lib", "bin.js")))))));
            paths.WebView2Directory = Path.Combine(paths.Root, "webview2");
            paths.PluginsDirectory = Path.Combine(paths.Root, "plugins");
            paths.DataDirectory = Path.Combine(paths.Root, "data");
            paths.LogsDirectory = Path.Combine(paths.Root, "logs");
            paths.CacheDirectory = Path.Combine(paths.Root, Path.Combine("data", "cache"));
            return paths;
        }

        public string ResolveHome(DesktopConfig config)
        {
            string value = config.DshHome;
            if (string.IsNullOrEmpty(value))
            {
                return Path.Combine(DataDirectory, "dsh-home");
            }
            if (string.Equals(value, "user", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            }
            return Environment.ExpandEnvironmentVariables(value);
        }

        public string ResolveWorkspace(DesktopConfig config)
        {
            if (!string.IsNullOrEmpty(config.Workspace))
            {
                return Environment.ExpandEnvironmentVariables(config.Workspace);
            }
            return Path.Combine(DataDirectory, "workspace");
        }
    }

    /// <summary>Reads plugin directories and merges their manifests into one effective profile.</summary>
    public static class PluginLoader
    {
        public static List<LoadedPlugin> Load(string pluginsDirectory, Logger log)
        {
            List<LoadedPlugin> plugins = new List<LoadedPlugin>();
            if (!Directory.Exists(pluginsDirectory))
            {
                return plugins;
            }
            string[] directories = Directory.GetDirectories(pluginsDirectory);
            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            foreach (string directory in directories)
            {
                string manifestPath = Path.Combine(directory, "launcher.json");
                string id = Path.GetFileName(directory);
                if (!File.Exists(manifestPath))
                {
                    if (File.Exists(Path.Combine(directory, "README.md")))
                    {
                        log.Info("plugin " + id + ": no launcher.json, ignored");
                    }
                    continue;
                }
                try
                {
                    PluginManifest manifest = Json.Read<PluginManifest>(manifestPath);
                    if (manifest == null)
                    {
                        log.Warn("plugin " + id + ": empty launcher.json");
                        continue;
                    }
                    Normalize(manifest, id);
                    if (manifest.Enabled.HasValue && !manifest.Enabled.Value)
                    {
                        log.Info("plugin " + manifest.Name + ": disabled");
                        continue;
                    }
                    LoadedPlugin plugin = new LoadedPlugin();
                    plugin.Id = id;
                    plugin.Directory = directory;
                    plugin.Manifest = manifest;
                    plugins.Add(plugin);
                    log.Info("plugin " + manifest.Name + ": loaded from " + directory);
                }
                catch (Exception ex)
                {
                    log.Warn("plugin " + id + ": invalid launcher.json (" + ex.Message + ")");
                }
            }
            return plugins;
        }

        /// <summary>
        /// Fills in the defaults a manifest omitted. DataContractJsonSerializer
        /// builds the instance without running field initializers, so every value
        /// the JSON left out arrives as null, zero, or false.
        /// </summary>
        /// <param name="manifest">The deserialized manifest to normalize in place.</param>
        /// <param name="id">Directory name, used when the manifest names nothing.</param>
        public static void Normalize(PluginManifest manifest, string id)
        {
            if (string.IsNullOrEmpty(manifest.Name)) manifest.Name = id;
            if (manifest.Description == null) manifest.Description = "";
            if (manifest.Title == null) manifest.Title = "";
            if (manifest.UserAgentSuffix == null) manifest.UserAgentSuffix = "";
            if (manifest.DshLinkModules == null) manifest.DshLinkModules = "";
            if (manifest.InjectCss == null) manifest.InjectCss = new string[0];
            if (manifest.InjectJs == null) manifest.InjectJs = new string[0];
            if (manifest.Args == null) manifest.Args = new string[0];
            if (manifest.TrustedHosts == null) manifest.TrustedHosts = new string[0];
            if (manifest.DshPatch == null) manifest.DshPatch = new string[0];
            if (manifest.DshPlugins == null) manifest.DshPlugins = new string[0];
            if (manifest.Env == null) manifest.Env = new Dictionary<string, string>();
        }

        /// <summary>Resolves a manifest relative path, refusing anything outside the plugin directory.</summary>
        public static string ResolveInside(LoadedPlugin plugin, string relative)
        {
            if (string.IsNullOrEmpty(relative))
            {
                return null;
            }
            string combined = Path.GetFullPath(Path.Combine(plugin.Directory, relative));
            string root = Path.GetFullPath(plugin.Directory);
            if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return combined;
        }

        /// <summary>
        /// Links the bundled dependency tree into a plugin's own node_modules so a
        /// drop-in plugin resolves <c>@deepseek-ai/dsh-*</c> imports without an
        /// install step. A directory junction needs no elevated privilege.
        /// </summary>
        /// <param name="linkDirectory">The node_modules path to create.</param>
        /// <param name="runtimeNodeModules">The bundled tree to link to.</param>
        /// <param name="log">Launcher log.</param>
        /// <returns>True when the link resolves to the bundled tree.</returns>
        public static bool EnsureModuleLink(string linkDirectory, string runtimeNodeModules, Logger log)
        {
            if (string.IsNullOrEmpty(linkDirectory) || string.IsNullOrEmpty(runtimeNodeModules))
            {
                return false;
            }
            string marker = Path.Combine(linkDirectory,
                Path.Combine("@deepseek-ai", Path.Combine("dsh-tools", "package.json")));
            if (File.Exists(marker))
            {
                return true;
            }
            if (!Directory.Exists(runtimeNodeModules))
            {
                log.Warn("module link target missing: " + runtimeNodeModules);
                return false;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(linkDirectory));
                if (Directory.Exists(linkDirectory))
                {
                    RunCmd("rmdir \"" + linkDirectory + "\"");
                }
                RunCmd("mklink /J \"" + linkDirectory + "\" \"" + runtimeNodeModules + "\"");
                bool ok = File.Exists(marker);
                if (ok)
                {
                    log.Info("module link: " + linkDirectory + " -> " + runtimeNodeModules);
                }
                else
                {
                    log.Warn("module link failed: " + linkDirectory);
                }
                return ok;
            }
            catch (Exception ex)
            {
                log.Warn("module link error for " + linkDirectory + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Runs one cmd.exe builtin (mklink, rmdir) and returns its output.</summary>
        private static string RunCmd(string arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo("cmd.exe", "/c " + arguments);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            Process process = Process.Start(info);
            if (process == null)
            {
                return "";
            }
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            process.Dispose();
            return output;
        }
    }

    /// <summary>Network helpers for the optional LAN mode.</summary>
    public static class Network
    {
        public static string[] LocalIPv4()
        {
            List<string> addresses = new List<string>();
            try
            {
                IPHostEntry entry = Dns.GetHostEntry(Dns.GetHostName());
                foreach (IPAddress address in entry.AddressList)
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                    {
                        addresses.Add(address.ToString());
                    }
                }
            }
            catch (SocketException)
            {
            }
            return addresses.ToArray();
        }

        public static int ParsePortFromUrl(string url)
        {
            try
            {
                Uri uri = new Uri(url);
                return uri.Port;
            }
            catch (UriFormatException)
            {
                return 0;
            }
        }
    }
}
