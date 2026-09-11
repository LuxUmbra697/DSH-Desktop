// DSH Desktop - checks npm for newer DeepSeek Harness releases and installs them
// into the bundled runtime, the equivalent of what `npx @deepseek-ai/dsh@next web`
// does on every start, but user-controlled and pinned by default.
//
// Compiled with the in-box C# 5 compiler; see DesktopSupport.cs for the syntax
// ceiling this file obeys.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DshDesktop
{
    /// <summary>Outcome of one update check.</summary>
    public sealed class UpdateInfo
    {
        public bool Ok;
        public string Error = "";
        public string CurrentVersion = "";
        public string LatestVersion = "";
        public string NextVersion = "";
        public string TargetVersion = "";
        public string Channel = "next";
        public string PublishedAt = "";
        public bool UpdateAvailable;
    }

    /// <summary>Version comparison and the npm registry check.</summary>
    public static class Updates
    {
        public const string PackageName = "@deepseek-ai/dsh";
        public const string RegistryUrl = "https://registry.npmjs.org/@deepseek-ai/dsh";

        /// <summary>
        /// Reads one flat JSON object of string values starting at its opening brace.
        /// The npm packument is far too large to materialize, and the tags are all
        /// this launcher needs, so the object is scanned rather than deserialized.
        /// </summary>
        /// <param name="json">Raw response body.</param>
        /// <param name="key">Member name to locate, without quotes.</param>
        /// <returns>The parsed pairs, empty when the member is absent.</returns>
        public static Dictionary<string, string> ReadFlatObject(string json, string key)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(json))
            {
                return values;
            }
            int index = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (index < 0)
            {
                return values;
            }
            int open = json.IndexOf('{', index);
            if (open < 0)
            {
                return values;
            }
            int depth = 1;
            int position = open + 1;
            bool inString = false;
            bool escaped = false;
            while (position < json.Length && depth > 0)
            {
                char current = json[position];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }
                }
                else if (current == '"')
                {
                    inString = true;
                }
                else if (current == '{')
                {
                    depth += 1;
                }
                else if (current == '}')
                {
                    depth -= 1;
                }
                position += 1;
            }
            int close = position - 1;
            if (close <= open)
            {
                return values;
            }
            string body = json.Substring(open + 1, close - open - 1);
            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(body, "\"([^\"]+)\"\\s*:\\s*\"([^\"]*)\""))
            {
                values[match.Groups[1].Value] = match.Groups[2].Value;
            }
            return values;
        }

        /// <summary>Reads the version of the bundled DSH runtime.</summary>
        public static string InstalledVersion(AppPaths paths)
        {
            try
            {
                string manifest = Path.Combine(paths.Root,
                    Path.Combine("runtime", Path.Combine("app", Path.Combine("node_modules",
                        Path.Combine("@deepseek-ai", Path.Combine("dsh", "package.json"))))));
                if (!File.Exists(manifest))
                {
                    return "";
                }
                string text = File.ReadAllText(manifest);
                int index = text.IndexOf("\"version\"", StringComparison.Ordinal);
                if (index < 0)
                {
                    return "";
                }
                int colon = text.IndexOf(':', index);
                int first = text.IndexOf('"', colon + 1);
                int second = text.IndexOf('"', first + 1);
                if (first < 0 || second < 0)
                {
                    return "";
                }
                return text.Substring(first + 1, second - first - 1);
            }
            catch (IOException)
            {
                return "";
            }
        }

        /// <summary>
        /// Compares two semver strings, treating a release as newer than its own
        /// prereleases, which is what decides whether an update is offered.
        /// </summary>
        /// <param name="left">Left version, with or without a leading v.</param>
        /// <param name="right">Right version.</param>
        /// <returns>-1, 0, or 1.</returns>
        public static int CompareVersions(string left, string right)
        {
            string leftBase;
            string leftPre;
            string rightBase;
            string rightPre;
            SplitVersion(left, out leftBase, out leftPre);
            SplitVersion(right, out rightBase, out rightPre);

            string[] leftParts = leftBase.Split('.');
            string[] rightParts = rightBase.Split('.');
            int count = Math.Max(leftParts.Length, rightParts.Length);
            for (int i = 0; i < count; i += 1)
            {
                int a = i < leftParts.Length ? ToInt(leftParts[i]) : 0;
                int b = i < rightParts.Length ? ToInt(rightParts[i]) : 0;
                if (a != b)
                {
                    return a < b ? -1 : 1;
                }
            }
            bool leftHasPre = leftPre.Length > 0;
            bool rightHasPre = rightPre.Length > 0;
            if (leftHasPre && !rightHasPre)
            {
                return -1;
            }
            if (!leftHasPre && rightHasPre)
            {
                return 1;
            }
            if (!leftHasPre && !rightHasPre)
            {
                return 0;
            }
            string[] leftIds = leftPre.Split('.');
            string[] rightIds = rightPre.Split('.');
            int idCount = Math.Max(leftIds.Length, rightIds.Length);
            for (int i = 0; i < idCount; i += 1)
            {
                if (i >= leftIds.Length)
                {
                    return -1;
                }
                if (i >= rightIds.Length)
                {
                    return 1;
                }
                int leftNumber;
                int rightNumber;
                bool leftIsNumber = int.TryParse(leftIds[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out leftNumber);
                bool rightIsNumber = int.TryParse(rightIds[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out rightNumber);
                if (leftIsNumber && rightIsNumber)
                {
                    if (leftNumber != rightNumber)
                    {
                        return leftNumber < rightNumber ? -1 : 1;
                    }
                    continue;
                }
                if (leftIsNumber != rightIsNumber)
                {
                    return leftIsNumber ? -1 : 1;
                }
                int compare = string.CompareOrdinal(leftIds[i], rightIds[i]);
                if (compare != 0)
                {
                    return compare < 0 ? -1 : 1;
                }
            }
            return 0;
        }

        private static void SplitVersion(string value, out string basePart, out string prerelease)
        {
            string text = value == null ? "" : value.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(1);
            }
            int dash = text.IndexOf('-');
            int plus = text.IndexOf('+');
            int cut = dash;
            if (plus >= 0 && (cut < 0 || plus < cut))
            {
                cut = plus;
            }
            if (cut < 0)
            {
                basePart = text;
                prerelease = "";
                return;
            }
            basePart = text.Substring(0, cut);
            prerelease = dash >= 0 ? text.Substring(dash + 1) : "";
            int build = prerelease.IndexOf('+');
            if (build >= 0)
            {
                prerelease = prerelease.Substring(0, build);
            }
        }

        private static int ToInt(string value)
        {
            int result;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        /// <summary>Queries the npm registry and compares it with the bundled runtime.</summary>
        /// <param name="paths">Resolved installation paths.</param>
        /// <param name="channel">"next" or "latest".</param>
        /// <returns>The check outcome; never throws.</returns>
        public static UpdateInfo Check(AppPaths paths, string channel)
        {
            UpdateInfo info = new UpdateInfo();
            info.Channel = string.IsNullOrEmpty(channel) ? "next" : channel;
            info.CurrentVersion = InstalledVersion(paths);
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(RegistryUrl);
                request.UserAgent = "DSH-Desktop";
                request.Accept = "application/json";
                request.Timeout = 20000;
                string body;
                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }

                Dictionary<string, string> tags = ReadFlatObject(body, "dist-tags");
                if (tags.Count == 0)
                {
                    info.Error = "npm 返回的数据里没有 dist-tags。";
                    return info;
                }
                string next;
                string latest;
                tags.TryGetValue("next", out next);
                tags.TryGetValue("latest", out latest);
                info.NextVersion = next == null ? "" : next;
                info.LatestVersion = latest == null ? "" : latest;

                string target = info.Channel == "latest" ? info.LatestVersion : info.NextVersion;
                if (string.IsNullOrEmpty(target))
                {
                    target = info.LatestVersion;
                }
                if (string.IsNullOrEmpty(target))
                {
                    info.Error = "npm 上没有找到可用版本。";
                    return info;
                }
                info.TargetVersion = target;
                Dictionary<string, string> times = ReadFlatObject(body, "time");
                string published;
                if (times.TryGetValue(target, out published))
                {
                    info.PublishedAt = published;
                }
                if (string.IsNullOrEmpty(info.CurrentVersion))
                {
                    info.Ok = true;
                    info.UpdateAvailable = true;
                    return info;
                }
                info.UpdateAvailable = CompareVersions(info.CurrentVersion, target) < 0;
                info.Ok = true;
                return info;
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
                return info;
            }
        }

        /// <summary>
        /// Installs one DSH version into the bundled runtime with the bundled npm,
        /// then prunes the result so the payload stays small.
        /// </summary>
        /// <param name="paths">Resolved installation paths.</param>
        /// <param name="version">Exact version to install.</param>
        /// <param name="nodeExe">Node used to run npm.</param>
        /// <param name="log">Launcher log.</param>
        /// <param name="progress">Receives human readable progress lines.</param>
        /// <returns>Empty string on success, otherwise the failure message.</returns>
        public static string Install(AppPaths paths, string version, string nodeExe, Logger log, Action<string> progress)
        {
            if (string.IsNullOrEmpty(version))
            {
                return "没有指定要安装的版本。";
            }
            string npmCli = RuntimeProbe.FindNpmCli(paths.Root, nodeExe);
            if (string.IsNullOrEmpty(npmCli))
            {
                return "没有找到 npm：请安装 Node.js（自带 npm），或使用“重新下载内置 Node”后重试。";
            }
            string runtimeApp = Path.Combine(paths.Root, Path.Combine("runtime", "app"));
            Directory.CreateDirectory(runtimeApp);
            Directory.CreateDirectory(paths.CacheDirectory);

            string arguments = "\"" + npmCli + "\" install " + PackageName + "@" + version
                + " --prefix \"" + runtimeApp + "\""
                + " --omit=dev --ignore-scripts --no-audit --no-fund --loglevel=error"
                + " --cache \"" + Path.Combine(paths.CacheDirectory, "npm") + "\"";
            return Run(nodeExe, arguments, paths, log, progress, version);
        }

        private static string Run(string nodeExe, string arguments, AppPaths paths, Logger log, Action<string> progress, string version)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(nodeExe, arguments);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = new UTF8Encoding(false);
                info.StandardErrorEncoding = new UTF8Encoding(false);
                info.WorkingDirectory = paths.Root;
                log.Info("update: " + nodeExe + " " + arguments);
                if (progress != null) progress("正在安装 " + PackageName + "@" + version + " …");

                Process process = Process.Start(info);
                if (process == null)
                {
                    return "无法启动 npm。";
                }
                StringBuilder tail = new StringBuilder();
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    log.Info("npm: " + e.Data);
                    lock (tail)
                    {
                        tail.AppendLine(e.Data);
                        if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);
                    }
                    if (progress != null) progress(e.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    log.Warn("npm!: " + e.Data);
                    lock (tail)
                    {
                        tail.AppendLine(e.Data);
                        if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);
                    }
                    if (progress != null) progress(e.Data);
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(900000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    return "npm 安装超时（15 分钟）。";
                }
                if (process.ExitCode != 0)
                {
                    string detail;
                    lock (tail)
                    {
                        detail = tail.ToString().Trim();
                    }
                    if (detail.Length > 400)
                    {
                        detail = detail.Substring(detail.Length - 400);
                    }
                    return "npm 退出码 " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + "：" + detail;
                }

                string installed = InstalledVersion(paths);
                if (CompareVersions(installed, version) != 0)
                {
                    return "安装后版本仍为 " + installed + "，预期 " + version + "。";
                }
                Prune(paths, nodeExe, log, progress);
                return "";
            }
            catch (Exception ex)
            {
                log.Error("update failed: " + ex.Message);
                return ex.Message;
            }
        }

        /// <summary>Runs the shrink step so an updated runtime keeps the size budget.</summary>
        private static void Prune(AppPaths paths, string nodeExe, Logger log, Action<string> progress)
        {
            try
            {
                string script = Path.Combine(paths.Root, Path.Combine("scripts", "prune-runtime.mjs"));
                if (!File.Exists(script))
                {
                    return;
                }
                string target = Path.Combine(paths.Root, Path.Combine("runtime", Path.Combine("app", "node_modules")));
                if (progress != null) progress("正在精简运行时…");
                string output = RuntimeProbe.Capture(nodeExe, "\"" + script + "\" --root \"" + target + "\"", 600000);
                if (!string.IsNullOrEmpty(output))
                {
                    log.Info("prune after update: " + output.Replace("\n", " ").Replace("\r", ""));
                }
            }
            catch (Exception ex)
            {
                log.Warn("prune after update failed: " + ex.Message);
            }
        }
    }
}
