// DSH Desktop - the runtime manager and the update dialog.
//
// Both windows are plain WinForms built from docked and auto-sized containers so
// they stay usable when the launcher window is small or the display is scaled.
//
// Compiled with the in-box C# 5 compiler; see DesktopSupport.cs for the syntax
// ceiling this file obeys.

using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DshDesktop
{
    /// <summary>Write-once monospaced report surface shared by both dialogs.</summary>
    internal sealed class ReportBox : TextBox
    {
        public ReportBox()
        {
            Multiline = true;
            ReadOnly = true;
            ScrollBars = ScrollBars.Vertical;
            WordWrap = false;
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(22, 25, 32);
            ForeColor = Color.Gainsboro;
            BorderStyle = BorderStyle.None;
            Font = new Font("Consolas", 9.5F, FontStyle.Regular);
        }

        public void SetLines(string text)
        {
            Text = text.Replace("\n", Environment.NewLine);
            SelectionStart = 0;
            SelectionLength = 0;
        }
    }

    /// <summary>Bottom action bar that wraps its buttons when the window is narrow.</summary>
    internal sealed class ButtonBar : FlowLayoutPanel
    {
        public ButtonBar()
        {
            Dock = DockStyle.Bottom;
            FlowDirection = FlowDirection.LeftToRight;
            WrapContents = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10, 8, 10, 10);
            BackColor = Color.FromArgb(28, 32, 40);
        }

        public Button Add(string text, EventHandler handler)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(96, 30);
            button.Margin = new Padding(0, 0, 8, 6);
            button.FlatStyle = FlatStyle.System;
            button.Click += handler;
            Controls.Add(button);
            return button;
        }
    }

    /// <summary>
    /// Shows what the launcher can execute: bundled Node and npm, a Node already on
    /// the machine, and the WebView2 runtime. The bundled Node can be deleted only
    /// when a suitable system Node exists, and restored in two ways.
    /// </summary>
    public sealed class RuntimeForm : Form
    {
        private readonly AppPaths paths;
        private readonly Logger log;
        private readonly DesktopConfig config;
        private readonly Action persistConfig;
        private readonly Action restartServer;

        private readonly ReportBox report = new ReportBox();
        private Button removeButton;
        private Button copyButton;
        private Button downloadButton;
        private NodeProbe systemNode;
        private NodeProbe bundledNode;

        public RuntimeForm(AppPaths paths, Logger log, DesktopConfig config, Action persistConfig, Action restartServer)
        {
            this.paths = paths;
            this.log = log;
            this.config = config;
            this.persistConfig = persistConfig;
            this.restartServer = restartServer;

            Text = "运行环境管理 — DSH Desktop";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(560, 420);
            Size = new Size(760, 560);
            BackColor = Color.FromArgb(22, 25, 32);
            ForeColor = Color.Gainsboro;
            ShowInTaskbar = false;

            ButtonBar bar = new ButtonBar();
            bar.Add("重新检测", delegate { Refresh(true); })
                .Name = "refresh";
            removeButton = bar.Add("删除内置 Node", delegate { RemoveBundledNode(); });
            copyButton = bar.Add("从系统 Node 复制", delegate { CopyFromSystem(); });
            downloadButton = bar.Add("重新下载内置 Node", delegate { DownloadNode(); });
            bar.Add("打开安装目录", delegate { OpenRoot(); });
            bar.Add("关闭", delegate { Close(); });

            Controls.Add(report);
            Controls.Add(bar);

            Load += delegate { Refresh(false); };
        }

        private void OpenRoot()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(paths.Root) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                log.Warn("failed to open " + paths.Root + ": " + ex.Message);
            }
        }

        /// <summary>Re-probes the machine and rebuilds the report and button states.</summary>
        public void Refresh(bool announce)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                bundledNode = RuntimeProbe.ProbeNode(paths.NodeExe);
                systemNode = RuntimeProbe.FindSystemNode();
                WebView2Probe webview = RuntimeProbe.ProbeWebView2Runtime();

                long nodeSize = RuntimeActions.MeasureSize(paths.NodeExe);
                long runtimeSize = RuntimeActions.MeasureSize(Path.Combine(paths.Root, Path.Combine("runtime", Path.Combine("app", "node_modules"))));
                long npmSize = RuntimeActions.MeasureSize(Path.Combine(paths.Root, Path.Combine("runtime", "npm")));
                long sdkSize = RuntimeActions.MeasureSize(paths.WebView2Directory);
                long dataSize = RuntimeActions.MeasureSize(paths.DataDirectory);

                StringBuilder builder = new StringBuilder();
                builder.AppendLine("DSH Desktop 运行环境");
                builder.AppendLine("安装目录   " + paths.Root);
                builder.AppendLine("DSH_HOME   " + paths.ResolveHome(config));
                builder.AppendLine();
                builder.AppendLine("组件                 来源            版本 / 状态");
                builder.AppendLine("--------------------------------------------------------------------------");
                builder.AppendLine(Row("Node（内置）", bundledNode.Found ? "runtime\\node.exe" : "（已删除）", bundledNode.Found ? bundledNode.Label : "—"));
                builder.AppendLine(Row("Node（系统）", ShortPath(systemNode.Path), systemNode.Label));
                string npmCli = RuntimeProbe.FindNpmCli(paths.Root, bundledNode.Found ? paths.NodeExe : systemNode.Path);
                builder.AppendLine(Row("npm", string.IsNullOrEmpty(npmCli) ? "（未找到）" : ShortPath(npmCli), string.IsNullOrEmpty(npmCli) ? "更新功能不可用" : "可用于更新 DSH"));
                builder.AppendLine(Row("DSH 运行时", "runtime\\app", Updates.InstalledVersion(paths)));
                builder.AppendLine(Row("WebView2 运行时", webview.Found ? webview.Source : "（未检测到）", webview.Found ? webview.Version : "将回退到 Edge 独立窗口"));
                builder.AppendLine(Row("WebView2 SDK", "webview2\\", "随包分发（必需，仅 " + RuntimeActions.FormatSize(sdkSize) + "）"));
                builder.AppendLine();
                builder.AppendLine("体积");
                builder.AppendLine("--------------------------------------------------------------------------");
                builder.AppendLine("内置 Node          " + RuntimeActions.FormatSize(nodeSize));
                builder.AppendLine("DSH 运行时依赖     " + RuntimeActions.FormatSize(runtimeSize) + "（已精简）");
                builder.AppendLine("内置 npm           " + RuntimeActions.FormatSize(npmSize));
                builder.AppendLine("WebView2 SDK       " + RuntimeActions.FormatSize(sdkSize));
                long payload = nodeSize + runtimeSize + npmSize + sdkSize + RuntimeActions.MeasureSize(Path.Combine(paths.Root, "DSH Desktop.exe"));
                builder.AppendLine("可分发载荷合计     " + RuntimeActions.FormatSize(payload));
                builder.AppendLine("用户数据目录       " + RuntimeActions.FormatSize(dataSize) + "（会话与缓存，不随包分发）");
                builder.AppendLine();
                builder.AppendLine("建议");
                builder.AppendLine("--------------------------------------------------------------------------");
                if (!webview.Found)
                {
                    builder.AppendLine("· 未检测到 WebView2 运行时：窗口会回退为 Edge 独立窗口。安装 WebView2 Runtime 可恢复内嵌窗口。");
                }
                if (systemNode.Found && systemNode.Usable && bundledNode.Found)
                {
                    builder.AppendLine("· 系统 Node " + systemNode.Version + " 满足 DSH 要求 " + RuntimeProbe.EngineRangeText()
                        + "，可删除内置 Node 节省 " + RuntimeActions.FormatSize(nodeSize) + "。");
                }
                else if (systemNode.Found && !systemNode.Usable)
                {
                    builder.AppendLine("· 系统 Node " + systemNode.Version + " 低于 DSH 要求 " + RuntimeProbe.EngineRangeText()
                        + "，因此不允许删除内置 Node（删了会启动不了）。升级系统 Node 后再试。");
                }
                else if (!systemNode.Found && bundledNode.Found)
                {
                    builder.AppendLine("· 本机没有系统 Node：内置 Node 是唯一运行时，不能删除。");
                }
                else if (!bundledNode.Found && !systemNode.Usable)
                {
                    builder.AppendLine("· 内置 Node 已删除且系统 Node 不可用：请用下方按钮恢复，否则无法启动。");
                }
                builder.AppendLine("· 更新 DSH 需要 npm；内置 npm 已随包分发，删除内置 Node 后也能用系统 Node 运行它。");

                report.SetLines(builder.ToString());

                bool canRemove = bundledNode.Found && systemNode.Found && systemNode.Usable;
                removeButton.Enabled = canRemove;
                copyButton.Enabled = !bundledNode.Found && systemNode.Found;
                downloadButton.Enabled = !bundledNode.Found;
                if (announce)
                {
                    MessageBox.Show(this, "已重新检测本机运行环境。", "运行环境管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                log.Error("runtime refresh failed: " + ex);
                report.SetLines("检测失败：" + ex.Message);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private static string Row(string name, string source, string value)
        {
            return Pad(name, 20) + Pad(source, 16) + value;
        }

        private static string Pad(string text, int width)
        {
            string value = text == null ? "" : text;
            if (value.Length >= width)
            {
                return value.Substring(0, width - 1) + " ";
            }
            return value + new string(' ', width - value.Length);
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "（未找到）";
            }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0 && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            {
                return "%USERPROFILE%" + path.Substring(home.Length);
            }
            return path;
        }

        private void RemoveBundledNode()
        {
            if (!(systemNode.Found && systemNode.Usable))
            {
                MessageBox.Show(this, "系统 Node 不可用，不能删除内置 Node。", "运行环境管理",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            long size = RuntimeActions.MeasureSize(paths.NodeExe);
            string text = "将删除内置 Node（" + RuntimeActions.FormatSize(size) + "），启动器改用：\n"
                + systemNode.Path + "\n\n版本 " + systemNode.Version + "，满足 DSH 要求。\n\n"
                + "如需恢复：可用本窗口的“从系统 Node 复制”或“重新下载内置 Node”，也可以重新运行 build.ps1。\n\n继续吗？";
            if (MessageBox.Show(this, text, "删除内置 Node", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }
            RuntimeActionResult result = RuntimeActions.RemoveBundledNode(paths, log);
            if (result.Ok && config != null)
            {
                config.NodeMode = "system";
                config.NodePath = systemNode.Path;
                if (persistConfig != null)
                {
                    persistConfig();
                }
            }
            MessageBox.Show(this, result.Message, "运行环境管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Refresh(false);
            if (result.Ok && restartServer != null)
            {
                if (MessageBox.Show(this, "要现在重启服务以使用系统 Node 吗？", "运行环境管理",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    restartServer();
                    Close();
                }
            }
        }

        private void CopyFromSystem()
        {
            RuntimeActionResult result = RuntimeActions.RestoreBundledNodeFrom(systemNode.Path, paths, log);
            if (result.Ok && config != null)
            {
                config.NodeMode = "auto";
                config.NodePath = "";
                if (persistConfig != null)
                {
                    persistConfig();
                }
            }
            MessageBox.Show(this, result.Message, "运行环境管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Refresh(false);
        }

        private void DownloadNode()
        {
            downloadButton.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                RuntimeActionResult result = RuntimeActions.DownloadBundledNode(paths, log, delegate(string line)
                {
                    if (IsHandleCreated)
                    {
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            report.SetLines("正在恢复内置 Node\n\n" + line);
                        }));
                    }
                });
                MessageBox.Show(this, result.Message, "运行环境管理", MessageBoxButtons.OK,
                    result.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            finally
            {
                Cursor = Cursors.Default;
                Refresh(false);
            }
        }
    }

    /// <summary>
    /// Checks npm for a newer DeepSeek Harness and installs it into the bundled
    /// runtime on request, then restarts the server.
    /// </summary>
    public sealed class UpdateForm : Form
    {
        private readonly AppPaths paths;
        private readonly Logger log;
        private readonly DesktopConfig config;
        private readonly Action persistConfig;
        private readonly Action restartServer;

        private readonly ReportBox report = new ReportBox();
        private Button checkButton;
        private Button installButton;
        private UpdateInfo lastInfo;
        private bool busy;

        public UpdateForm(AppPaths paths, Logger log, DesktopConfig config, Action persistConfig, Action restartServer)
        {
            this.paths = paths;
            this.log = log;
            this.config = config;
            this.persistConfig = persistConfig;
            this.restartServer = restartServer;

            Text = "检查更新 — DSH Desktop";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(560, 400);
            Size = new Size(780, 520);
            BackColor = Color.FromArgb(22, 25, 32);
            ForeColor = Color.Gainsboro;
            ShowInTaskbar = false;

            ButtonBar bar = new ButtonBar();
            checkButton = bar.Add("检查更新", delegate { Check(false); });
            installButton = bar.Add("更新并重启", delegate { Install(); });
            installButton.Enabled = false;
            bar.Add("切换渠道", delegate { ToggleChannel(); });
            bar.Add("关闭", delegate { Close(); });

            Controls.Add(report);
            Controls.Add(bar);

            Load += delegate { Check(true); };
        }

        private void ToggleChannel()
        {
            config.Channel = config.Channel == "latest" ? "next" : "latest";
            if (persistConfig != null)
            {
                persistConfig();
            }
            Check(false);
        }

        /// <summary>Queries the registry on a worker thread and reports on the UI thread.</summary>
        public void Check(bool firstRun)
        {
            if (busy)
            {
                return;
            }
            busy = true;
            checkButton.Enabled = false;
            installButton.Enabled = false;
            report.SetLines("正在查询 npm registry …\n\n包：" + Updates.PackageName + "\n渠道：" + config.Channel);
            Task.Run(delegate
            {
                return Updates.Check(paths, config.Channel);
            }).ContinueWith(delegate(Task<UpdateInfo> task)
            {
                UpdateInfo info = task.Result;
                if (IsHandleCreated)
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        lastInfo = info;
                        Render(info, firstRun);
                        busy = false;
                        checkButton.Enabled = true;
                        installButton.Enabled = info.Ok && info.UpdateAvailable;
                    }));
                }
            });
        }

        private void Render(UpdateInfo info, bool firstRun)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("DSH 运行时版本");
            builder.AppendLine("--------------------------------------------------------------------------");
            builder.AppendLine("当前（随包分发）  " + (string.IsNullOrEmpty(info.CurrentVersion) ? "未知" : info.CurrentVersion));
            builder.AppendLine("npm next 标签      " + (string.IsNullOrEmpty(info.NextVersion) ? "—" : info.NextVersion));
            builder.AppendLine("npm latest 标签    " + (string.IsNullOrEmpty(info.LatestVersion) ? "—" : info.LatestVersion));
            builder.AppendLine("查询渠道           " + info.Channel + (info.Channel == "latest" ? "（仅正式版）" : "（含预发布）"));
            if (!string.IsNullOrEmpty(info.PublishedAt))
            {
                builder.AppendLine("目标版本发布时间   " + info.PublishedAt);
            }
            builder.AppendLine();
            builder.AppendLine("结论");
            builder.AppendLine("--------------------------------------------------------------------------");
            if (!info.Ok)
            {
                builder.AppendLine("检查失败：" + info.Error);
                builder.AppendLine();
                builder.AppendLine("常见原因：无网络、公司代理拦截 npm、或 registry 不可达。");
                builder.AppendLine("离线时仍可正常使用当前版本。");
            }
            else if (info.UpdateAvailable)
            {
                builder.AppendLine("有新版本可用：" + info.TargetVersion);
                builder.AppendLine();
                builder.AppendLine("“更新并重启”会执行：");
                builder.AppendLine("  node runtime\\npm\\bin\\npm-cli.js install " + Updates.PackageName + "@" + info.TargetVersion);
                builder.AppendLine("       --prefix runtime\\app --omit=dev --ignore-scripts");
                builder.AppendLine("随后自动精简依赖并重启服务。会话记录保留。");
                builder.AppendLine();
                builder.AppendLine("与官方 npx 的差别：npx 每次启动都拉取最新版本，版本会悄悄变化；");
                builder.AppendLine("DSH Desktop 默认固定版本，只有你确认后才更新。");
            }
            else
            {
                builder.AppendLine("已是最新：" + (string.IsNullOrEmpty(info.CurrentVersion) ? "—" : info.CurrentVersion));
                builder.AppendLine();
                builder.AppendLine("如需跟随官方滚动发布，可切换到 next/latest 渠道再检查。");
            }
            builder.AppendLine();
            builder.AppendLine("说明");
            builder.AppendLine("--------------------------------------------------------------------------");
            builder.AppendLine("· 更新只影响 runtime\\app 里的 DSH 依赖，不动你的插件、配置与会话。");
            builder.AppendLine("· 更新需要 npm；随包已带 npm（runtime\\npm），删除内置 Node 后会用系统 Node 运行它。");
            report.SetLines(builder.ToString());
            if (firstRun)
            {
                log.Info("update check: current=" + info.CurrentVersion + " target=" + info.TargetVersion
                    + " available=" + info.UpdateAvailable + (info.Ok ? "" : " error=" + info.Error));
            }
        }

        private void Install()
        {
            if (busy || lastInfo == null || !lastInfo.UpdateAvailable)
            {
                return;
            }
            string version = lastInfo.TargetVersion;
            if (MessageBox.Show(this, "将安装 " + Updates.PackageName + "@" + version + " 并重启服务，继续吗？",
                "更新 DSH", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            string nodeExe = paths.NodeExe;
            if (!File.Exists(nodeExe))
            {
                NodeProbe probe = RuntimeProbe.FindSystemNode();
                nodeExe = probe.Found ? probe.Path : "";
            }
            if (string.IsNullOrEmpty(nodeExe))
            {
                MessageBox.Show(this, "没有可用的 Node（内置已删除且系统不可用）。\n请先在“运行环境管理”里恢复内置 Node。",
                    "更新 DSH", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            busy = true;
            checkButton.Enabled = false;
            installButton.Enabled = false;
            report.SetLines("正在更新到 " + version + " …\n\n请勿关闭窗口。");
            string finalNode = nodeExe;
            Task.Run(delegate
            {
                return Updates.Install(paths, version, finalNode, log, delegate(string line)
                {
                    if (IsHandleCreated)
                    {
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            report.AppendText(line + Environment.NewLine);
                        }));
                    }
                });
            }).ContinueWith(delegate(Task<string> task)
            {
                string error = task.Result;
                if (IsHandleCreated)
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        busy = false;
                        checkButton.Enabled = true;
                        if (string.IsNullOrEmpty(error))
                        {
                            MessageBox.Show(this, "已更新到 " + version + "，现在重启服务。", "更新 DSH",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                            if (restartServer != null)
                            {
                                restartServer();
                            }
                            Close();
                        }
                        else
                        {
                            MessageBox.Show(this, "更新失败：\n" + error, "更新 DSH",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            Check(false);
                        }
                    }));
                }
            });
        }
    }
}
