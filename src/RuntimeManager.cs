// DSH Desktop - runtime discovery and the actions that act on it.
//
// The launcher can run on the bundled Node, on a Node already installed on the
// machine, or on both: this file answers what exists, whether it satisfies the
// DSH engine range, and whether the bundled copy is therefore removable.
//
// Compiled with the in-box C# 5 compiler; see DesktopSupport.cs for the syntax
// ceiling this file obeys.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace DshDesktop
{
    /// <summary>A Node.js installation the launcher could execute.</summary>
    public sealed class NodeProbe
    {
        public string Path = "";
        public string Version = "";
        public bool Found;
        public bool Usable;
        /// <summary>Why it is unusable, phrased for the runtime panel.</summary>
        public string Reason = "";
        public int Major;
        public int Minor;
        public int Patch;

        public string Label
        {
            get
            {
                if (!Found)
                {
                    return "未检测到";
                }
                string text = Version;
                if (!Usable && !string.IsNullOrEmpty(Reason))
                {
                    text = text + "（" + Reason + "）";
                }
                return text;
            }
        }
    }

    /// <summary>The WebView2 runtime that hosts the window.</summary>
    public sealed class WebView2Probe
    {
        public string Version = "";
        public bool Found;
        public string Source = "";
        public string Reason = "";
    }

    /// <summary>Detection for Node, npm, and WebView2 on this machine.</summary>
    public static class RuntimeProbe
    {
        /// <summary>DSH requires ^22.19.0 || >=24.0.0.</summary>
        public static bool NodeSatisfiesEngines(int major, int minor, int patch)
        {
            if (major >= 24)
            {
                return true;
            }
            if (major == 22)
            {
                return minor >= 19;
            }
            return false;
        }

        public static string EngineRangeText()
        {
            return "^22.19.0 || >=24.0.0";
        }

        /// <summary>Parses "v24.21.0", "24.21.0", or a dotted build number.</summary>
        public static bool TryParseVersion(string raw, out int major, out int minor, out int patch)
        {
            major = 0;
            minor = 0;
            patch = 0;
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }
            string text = raw.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(1);
            }
            string[] parts = text.Split('.');
            if (parts.Length < 1 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major))
            {
                return false;
            }
            if (parts.Length > 1)
            {
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor);
            }
            if (parts.Length > 2)
            {
                string third = parts[2];
                int cut = third.IndexOfAny(new char[] { '-', '+' });
                if (cut >= 0)
                {
                    third = third.Substring(0, cut);
                }
                int.TryParse(third, NumberStyles.Integer, CultureInfo.InvariantCulture, out patch);
            }
            return true;
        }

        /// <summary>Runs a program and returns its stdout, or null on failure.</summary>
        public static string Capture(string fileName, string arguments, int timeoutMs)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(fileName, arguments);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                Process process = Process.Start(info);
                if (process == null)
                {
                    return null;
                }
                string output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    return null;
                }
                return output.Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static NodeProbe ProbeNode(string exePath)
        {
            NodeProbe probe = new NodeProbe();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                return probe;
            }
            string raw = Capture(exePath, "--version", 15000);
            if (string.IsNullOrEmpty(raw))
            {
                probe.Found = true;
                probe.Path = exePath;
                probe.Usable = false;
                probe.Reason = "无法执行";
                return probe;
            }
            int major;
            int minor;
            int patch;
            if (!TryParseVersion(raw, out major, out minor, out patch))
            {
                probe.Found = true;
                probe.Path = exePath;
                probe.Usable = false;
                probe.Reason = "版本无法识别";
                return probe;
            }
            probe.Found = true;
            probe.Path = exePath;
            probe.Version = "v" + major.ToString(CultureInfo.InvariantCulture)
                + "." + minor.ToString(CultureInfo.InvariantCulture)
                + "." + patch.ToString(CultureInfo.InvariantCulture);
            probe.Major = major;
            probe.Minor = minor;
            probe.Patch = patch;
            probe.Usable = NodeSatisfiesEngines(major, minor, patch);
            if (!probe.Usable)
            {
                probe.Reason = "低于 DSH 要求的 " + EngineRangeText();
            }
            return probe;
        }

        /// <summary>Searches PATH, the registry install location, and nvm-style directories.</summary>
        public static NodeProbe FindSystemNode()
        {
            List<string> candidates = new List<string>();

            string whereResult = Capture("where.exe", "node.exe", 10000);
            if (!string.IsNullOrEmpty(whereResult))
            {
                foreach (string line in whereResult.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length > 0 && !candidates.Contains(trimmed))
                    {
                        candidates.Add(trimmed);
                    }
                }
            }

            AddIfPresent(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs\\node.exe"));
            AddIfPresent(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs\\node.exe"));
            AddIfPresent(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs\\nodejs\\node.exe"));

            foreach (string root in new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
            })
            {
                try
                {
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }
                    string[] versions = Directory.GetDirectories(root);
                    Array.Sort(versions);
                    Array.Reverse(versions);
                    foreach (string version in versions)
                    {
                        AddIfPresent(candidates, Path.Combine(version, "node.exe"));
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            NodeProbe best = new NodeProbe();
            foreach (string candidate in candidates)
            {
                NodeProbe probe = ProbeNode(candidate);
                if (!probe.Found)
                {
                    continue;
                }
                if (!best.Found)
                {
                    best = probe;
                }
                if (probe.Usable)
                {
                    return probe;
                }
            }
            return best;
        }

        private static void AddIfPresent(List<string> list, string path)
        {
            if (File.Exists(path) && !list.Contains(path))
            {
                list.Add(path);
            }
        }

        /// <summary>Finds npm relative to a Node installation, preferring the bundled copy.</summary>
        public static string FindNpmCli(string appRoot, string nodeExe)
        {
            string bundled = Path.Combine(appRoot, Path.Combine("runtime", Path.Combine("npm", Path.Combine("bin", "npm-cli.js"))));
            if (File.Exists(bundled))
            {
                return bundled;
            }
            if (!string.IsNullOrEmpty(nodeExe) && File.Exists(nodeExe))
            {
                string beside = Path.Combine(Path.GetDirectoryName(nodeExe), Path.Combine("node_modules", Path.Combine("npm", Path.Combine("bin", "npm-cli.js"))));
                if (File.Exists(beside))
                {
                    return beside;
                }
            }
            string whereResult = Capture("where.exe", "npm.cmd", 10000);
            if (!string.IsNullOrEmpty(whereResult))
            {
                string first = whereResult.Split('\n')[0].Trim();
                if (first.Length > 0)
                {
                    return first;
                }
            }
            return "";
        }

        /// <summary>Asks the WebView2 loader for the installed runtime, then falls back to the install folder.</summary>
        public static WebView2Probe ProbeWebView2Runtime()
        {
            WebView2Probe probe = new WebView2Probe();
            try
            {
                string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (!string.IsNullOrEmpty(version))
                {
                    probe.Found = true;
                    probe.Version = version;
                    probe.Source = "WebView2 加载器";
                    return probe;
                }
            }
            catch (Exception ex)
            {
                probe.Reason = ex.GetType().Name;
            }

            foreach (RegistryKey root in new RegistryKey[]
            {
                Registry.LocalMachine, Registry.CurrentUser,
            })
            {
                try
                {
                    using (RegistryKey key = root.OpenSubKey(
                        "SOFTWARE\\WOW6432Node\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"))
                    {
                        if (key != null)
                        {
                            object value = key.GetValue("pv");
                            if (value != null)
                            {
                                probe.Found = true;
                                probe.Version = Convert.ToString(value, CultureInfo.InvariantCulture);
                                probe.Source = "注册表 EdgeUpdate";
                                return probe;
                            }
                        }
                    }
                }
                catch (System.Security.SecurityException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine("Microsoft", Path.Combine("EdgeWebView", "Application")));
            try
            {
                if (Directory.Exists(folder))
                {
                    string best = "";
                    int bestMajor = -1;
                    foreach (string candidate in Directory.GetDirectories(folder))
                    {
                        string name = Path.GetFileName(candidate);
                        int major;
                        int minor;
                        int patch;
                        if (!TryParseVersion(name, out major, out minor, out patch))
                        {
                            continue;
                        }
                        if (major > bestMajor)
                        {
                            bestMajor = major;
                            best = name;
                        }
                    }
                    if (best.Length > 0)
                    {
                        probe.Found = true;
                        probe.Version = best;
                        probe.Source = "安装目录";
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return probe;
        }
    }

    /// <summary>Result of one mutating runtime action.</summary>
    public sealed class RuntimeActionResult
    {
        public bool Ok;
        public string Message = "";
    }

    /// <summary>
    /// Changes the runtime layout: dropping the bundled Node when a suitable system
    /// copy exists, restoring it from nodejs.org, or refreshing the bundled npm.
    /// </summary>
    public static class RuntimeActions
    {
        /// <summary>Deletes runtime\node.exe after the caller has confirmed usability.</summary>
        public static RuntimeActionResult RemoveBundledNode(AppPaths paths, Logger log)
        {
            RuntimeActionResult result = new RuntimeActionResult();
            try
            {
                if (!File.Exists(paths.NodeExe))
                {
                    result.Message = "内置 Node 已经不存在。";
                    result.Ok = true;
                    return result;
                }
                File.Delete(paths.NodeExe);
                log.Info("removed bundled node: " + paths.NodeExe);
                result.Ok = true;
                result.Message = "已删除内置 Node，启动器将使用系统 Node。";
            }
            catch (Exception ex)
            {
                log.Error("failed to remove bundled node: " + ex.Message);
                result.Message = "删除失败：" + ex.Message;
            }
            return result;
        }

        /// <summary>Copies a system Node over the bundled path, for offline restoration.</summary>
        public static RuntimeActionResult RestoreBundledNodeFrom(string sourceNode, AppPaths paths, Logger log)
        {
            RuntimeActionResult result = new RuntimeActionResult();
            try
            {
                if (string.IsNullOrEmpty(sourceNode) || !File.Exists(sourceNode))
                {
                    result.Message = "没有可复制的系统 Node。";
                    return result;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(paths.NodeExe));
                File.Copy(sourceNode, paths.NodeExe, true);
                log.Info("restored bundled node from " + sourceNode);
                result.Ok = true;
                result.Message = "已从系统 Node 复制回内置位置。";
            }
            catch (Exception ex)
            {
                log.Error("failed to restore bundled node: " + ex.Message);
                result.Message = "复制失败：" + ex.Message;
            }
            return result;
        }

        /// <summary>Reads the Node version recorded at build time.</summary>
        public static string ReadRecordedNodeVersion(AppPaths paths)
        {
            try
            {
                string file = Path.Combine(Path.GetDirectoryName(paths.NodeExe), "node-version.txt");
                if (File.Exists(file))
                {
                    return File.ReadAllText(file).Trim();
                }
            }
            catch (IOException)
            {
            }
            return "";
        }

        /// <summary>
        /// Downloads the recorded Node release from nodejs.org and extracts node.exe.
        /// Used when the bundled copy was removed and no suitable system Node exists.
        /// </summary>
        public static RuntimeActionResult DownloadBundledNode(AppPaths paths, Logger log, Action<string> progress)
        {
            RuntimeActionResult result = new RuntimeActionResult();
            string version = ReadRecordedNodeVersion(paths);
            if (string.IsNullOrEmpty(version))
            {
                result.Message = "缺少 runtime\\node-version.txt，无法确定要下载的版本。";
                return result;
            }
            string folder = "node-" + version + "-win-x64";
            string url = "https://nodejs.org/dist/" + version + "/" + folder + ".zip";
            string archive = Path.Combine(paths.CacheDirectory, folder + ".zip");
            try
            {
                Directory.CreateDirectory(paths.CacheDirectory);
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                if (progress != null) progress("正在下载 " + url);
                log.Info("downloading " + url);
                using (WebClient client = new WebClient())
                {
                    client.Headers.Add("user-agent", "DSH-Desktop");
                    client.DownloadFile(url, archive);
                }
                if (progress != null) progress("正在解压 node.exe");
                using (ZipArchive zip = ZipFile.OpenRead(archive))
                {
                    bool extracted = false;
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase)
                            && entry.FullName.IndexOf('/') == entry.FullName.LastIndexOf('/'))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(paths.NodeExe));
                            entry.ExtractToFile(paths.NodeExe, true);
                            extracted = true;
                            break;
                        }
                    }
                    if (!extracted)
                    {
                        result.Message = "压缩包里没有找到 node.exe。";
                        return result;
                    }
                }
                try
                {
                    File.Delete(archive);
                }
                catch (IOException)
                {
                }
                log.Info("restored bundled node " + version);
                result.Ok = true;
                result.Message = "已重新下载内置 Node " + version + "。";
            }
            catch (Exception ex)
            {
                log.Error("node download failed: " + ex.Message);
                result.Message = "下载失败：" + ex.Message + "（可改用系统 Node，或重新运行 build.ps1）";
            }
            return result;
        }

        /// <summary>Human readable byte count for the runtime panel.</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
            {
                return Math.Round(bytes / 1024.0 / 1024.0 / 1024.0, 2).ToString(CultureInfo.InvariantCulture) + " GB";
            }
            if (bytes >= 1024L * 1024L)
            {
                return Math.Round(bytes / 1024.0 / 1024.0, 1).ToString(CultureInfo.InvariantCulture) + " MB";
            }
            return Math.Round(bytes / 1024.0, 0).ToString(CultureInfo.InvariantCulture) + " KB";
        }

        /// <summary>Sizes one file or directory tree, following no reparse points twice.</summary>
        public static long MeasureSize(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return new FileInfo(path).Length;
                }
                if (!Directory.Exists(path))
                {
                    return 0;
                }
                long total = 0;
                foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (IOException)
                    {
                    }
                }
                return total;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
