// DSH Desktop - the window shell around a bundled DeepSeek Harness runtime.
//
// Boot sequence: load config and plugins, spawn `node .../@deepseek-ai/dsh/lib/bin.js web`,
// wait for the tokenized URL on stdout, then host that URL in WebView2 with the
// plugins' window, brand, and injection settings applied.
//
// Compiled with the in-box C# 5 compiler; see DesktopSupport.cs for the syntax
// ceiling this file obeys.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshDesktop
{
    internal static class Program
    {
        private const string Version = "1.1.1";
        private const string MutexName = "Global\\DshDesktop.SingleInstance.6F0B6E2A";

        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew = false;
            Mutex mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("DSH Desktop 已经在运行。\n请在任务栏中切换到已打开的窗口。", "DSH Desktop",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppPaths paths = AppPaths.Resolve();
            Logger log = new Logger(paths.LogsDirectory);
            log.Info("=== DSH Desktop " + Version + " starting, root=" + paths.Root);
            log.Info("command line: " + string.Join(" ", args));

            // The WebView2 SDK ships in its own folder; if the flattened copies
            // beside the executable are missing, resolve them from there instead
            // of failing the whole window.
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs resolveArgs)
            {
                try
                {
                    string simpleName = new System.Reflection.AssemblyName(resolveArgs.Name).Name;
                    if (simpleName == null || simpleName.IndexOf("Microsoft.Web.WebView2", StringComparison.Ordinal) != 0)
                    {
                        return null;
                    }
                    string candidate = Path.Combine(paths.WebView2Directory, simpleName + ".dll");
                    if (File.Exists(candidate))
                    {
                        log.Warn("resolving " + simpleName + " from " + candidate);
                        return System.Reflection.Assembly.LoadFrom(candidate);
                    }
                }
                catch (Exception ex)
                {
                    log.Error("assembly resolve failed for " + resolveArgs.Name + ": " + ex.Message);
                }
                return null;
            };

            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                log.Error("unhandled UI exception: " + e.Exception);
                MessageBox.Show("发生未处理的错误：\n" + e.Exception.Message + "\n\n详情见日志：" + log.CurrentFile,
                    "DSH Desktop", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                log.Error("unhandled domain exception: " + e.ExceptionObject);
            };

            try
            {
                Application.Run(new MainForm(paths, log, Version, args));
            }
            finally
            {
                log.Info("=== DSH Desktop exited");
                GC.KeepAlive(mutex);
            }
        }
    }

    /// <summary>The launcher window: start the server, host it, and keep both alive together.</summary>
    public sealed class MainForm : Form
    {
        private readonly AppPaths paths;
        private readonly Logger log;
        private readonly string version;
        private readonly string[] commandLine;

        private DesktopConfig config;
        private List<LoadedPlugin> plugins;
        private DshServer server;
        private WebView2 webView;
        private Panel content;
        private Label splash;
        private MenuStrip menu;
        private StatusStrip status;
        private ToolStripStatusLabel statusText;
        private ToolStripStatusLabel statusUrl;
        private string accessUrl;
        private bool fullScreen;
        private FormWindowState boundsState = FormWindowState.Normal;
        private Rectangle boundsRect;
        private Process fallbackBrowser;

        public MainForm(AppPaths paths, Logger log, string version, string[] commandLine)
        {
            this.paths = paths;
            this.log = log;
            this.version = version;
            this.commandLine = commandLine;

            LoadConfiguration();
            ApplyWindowSettings();

            Text = EffectiveTitle();
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(config.MinWidth, config.MinHeight);
            KeyPreview = true;
            BackColor = Color.FromArgb(18, 20, 26);
            ForeColor = Color.Gainsboro;

            // The page host is positioned explicitly in LayoutContent: WinForms docking
            // order between a fill control and the menu/status strips depends on
            // z-order subtleties, and getting it wrong paints the strips over the page.
            content = new Panel();
            content.BackColor = Color.FromArgb(18, 20, 26);
            Controls.Add(content);

            BuildMenu();
            BuildStatusBar();
            LayoutContent();
            BuildSplash();
            ApplyChrome();

            Load += OnFormLoad;
            FormClosing += OnFormClosing;
            KeyDown += OnKeyDown;
            Resize += OnResize;
            Move += OnMove;
        }

        private string EffectiveTitle()
        {
            string title = config.Title;
            foreach (LoadedPlugin plugin in plugins)
            {
                if (!string.IsNullOrEmpty(plugin.Manifest.Title))
                {
                    title = plugin.Manifest.Title;
                }
            }
            return title;
        }

        private void LoadConfiguration()
        {
            string path = Path.Combine(paths.Root, "config.json");
            try
            {
                if (File.Exists(path))
                {
                    DesktopConfig loaded = Json.Read<DesktopConfig>(path);
                    if (loaded != null)
                    {
                        config = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warn("config.json could not be parsed (" + ex.Message + "), using built-in defaults");
            }
            if (config == null)
            {
                config = new DesktopConfig();
            }
            NormalizeConfig();

            if (config.PluginsDisabled)
            {
                plugins = new List<LoadedPlugin>();
                log.Info("plugins disabled by config.json");
            }
            else
            {
                plugins = PluginLoader.Load(paths.PluginsDirectory, log);
            }
            foreach (LoadedPlugin plugin in plugins)
            {
                ApplyPluginToConfig(plugin);
            }
        }

        private void NormalizeConfig()
        {
            if (config.TrustedHosts == null) config.TrustedHosts = new string[0];
            if (config.ExtraArgs == null) config.ExtraArgs = new string[0];
            if (config.Env == null) config.Env = new Dictionary<string, string>();
            if (config.Host == null) config.Host = "127.0.0.1";
            if (config.Title == null) config.Title = "DSH Desktop";
            if (config.Width <= 0) config.Width = 1280;
            if (config.Height <= 0) config.Height = 840;
            if (config.MinWidth <= 0) config.MinWidth = 420;
            if (config.MinHeight <= 0) config.MinHeight = 320;
            if (config.StartupTimeoutSeconds <= 0) config.StartupTimeoutSeconds = 240;
            if (config.ZoomFactor <= 0.1) config.ZoomFactor = 1.0;
            if (string.IsNullOrEmpty(config.NodeMode)) config.NodeMode = "auto";
            if (config.NodePath == null) config.NodePath = "";
            if (string.IsNullOrEmpty(config.Channel)) config.Channel = "next";
            // The window chrome is optional: a product that wants the page to own the
            // whole window sets both keys to false, and Alt+M switches them at runtime.
            if (!config.ShowMenuBar.HasValue) config.ShowMenuBar = true;
            if (!config.ShowStatusBar.HasValue) config.ShowStatusBar = true;
        }

        /// <summary>Applies the configured window chrome, keeping the page layout in charge.</summary>
        private void ApplyChrome()
        {
            menu.Visible = config.ShowMenuBar.HasValue ? config.ShowMenuBar.Value : true;
            status.Visible = config.ShowStatusBar.HasValue ? config.ShowStatusBar.Value : true;
            LayoutContent();
        }

        /// <summary>
        /// Gives the page host exactly the client area minus the visible chrome, so
        /// the menu and status bars never cover part of the DSH interface.
        /// </summary>
        private void LayoutContent()
        {
            if (content == null)
            {
                return;
            }
            int top = menu != null && menu.Visible ? menu.Height : 0;
            int bottom = status != null && status.Visible ? status.Height : 0;
            int height = ClientSize.Height - top - bottom;
            if (height < 0)
            {
                height = 0;
            }
            content.Bounds = new Rectangle(0, top, ClientSize.Width, height);
        }

        /// <summary>Shows or hides the menu and status bars so the page can use the whole window.</summary>
        private void ToggleChrome()
        {
            bool show = !(menu.Visible && status.Visible);
            config.ShowMenuBar = show;
            config.ShowStatusBar = show;
            menu.Visible = show;
            status.Visible = show;
            LayoutContent();
            SaveConfig();
            statusText.Text = show ? "已显示窗口工具栏（Alt+M 隐藏）" : "已隐藏窗口工具栏（Alt+M 显示）";
            log.Info("chrome toggled: " + (show ? "visible" : "hidden"));
        }

        /// <summary>Writes config.json back, used after the runtime panel changes the layout.</summary>
        public void SaveConfig()
        {
            try
            {
                Json.Write(Path.Combine(paths.Root, "config.json"), config);
                log.Info("config.json saved");
            }
            catch (Exception ex)
            {
                log.Error("failed to save config.json: " + ex.Message);
            }
        }

        /// <summary>Plugins override config.json so a plugin ships its own defaults.</summary>
        private void ApplyPluginToConfig(LoadedPlugin plugin)
        {
            PluginManifest manifest = plugin.Manifest;
            if (manifest.Width > 0) config.Width = manifest.Width;
            if (manifest.Height > 0) config.Height = manifest.Height;
            if (manifest.MinWidth > 0) config.MinWidth = manifest.MinWidth;
            if (manifest.MinHeight > 0) config.MinHeight = manifest.MinHeight;
            if (manifest.ZoomFactor > 0.1) config.ZoomFactor = manifest.ZoomFactor;
            if (manifest.Env != null)
            {
                foreach (KeyValuePair<string, string> pair in manifest.Env)
                {
                    config.Env[pair.Key] = pair.Value;
                }
            }
            if (manifest.TrustedHosts != null)
            {
                List<string> merged = new List<string>(config.TrustedHosts);
                merged.AddRange(manifest.TrustedHosts);
                config.TrustedHosts = merged.ToArray();
            }
            if (manifest.Args != null)
            {
                List<string> merged = new List<string>(config.ExtraArgs);
                merged.AddRange(manifest.Args);
                config.ExtraArgs = merged.ToArray();
            }
        }

        private void ApplyWindowSettings()
        {
            Rectangle bounds = Screen.PrimaryScreen.WorkingArea;
            int width = config.Width;
            int height = config.Height;
            if (width > bounds.Width) width = bounds.Width;
            if (height > bounds.Height) height = bounds.Height;
            Size = new Size(width, height);

            WindowState state = LoadWindowState();
            bool restored = false;
            if (state != null && state.Width > 200 && state.Height > 200)
            {
                Rectangle candidate = new Rectangle(state.X, state.Y, state.Width, state.Height);
                Rectangle visible = Screen.GetWorkingArea(candidate);
                if (visible.IntersectsWith(candidate))
                {
                    StartPosition = FormStartPosition.Manual;
                    boundsRect = candidate;
                    Location = candidate.Location;
                    Size = candidate.Size;
                    boundsState = state.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
                    restored = true;
                }
            }
            if (!restored && config.MaximizeOnSmallScreen && (bounds.Width <= 1440 || bounds.Height <= 900))
            {
                boundsState = FormWindowState.Maximized;
            }
        }

        private WindowState LoadWindowState()
        {
            string path = Path.Combine(paths.DataDirectory, "window.json");
            try
            {
                if (File.Exists(path))
                {
                    return Json.Read<WindowState>(path);
                }
            }
            catch (Exception ex)
            {
                log.Warn("window.json could not be parsed (" + ex.Message + ")");
            }
            return null;
        }

        private void SaveWindowState()
        {
            try
            {
                WindowState state = new WindowState();
                Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                state.X = bounds.X;
                state.Y = bounds.Y;
                state.Width = bounds.Width;
                state.Height = bounds.Height;
                state.Maximized = WindowState == FormWindowState.Maximized;
                Directory.CreateDirectory(paths.DataDirectory);
                Json.Write(Path.Combine(paths.DataDirectory, "window.json"), state);
            }
            catch (Exception ex)
            {
                log.Warn("window.json could not be written (" + ex.Message + ")");
            }
        }

        private void BuildMenu()
        {
            menu = new MenuStrip();
            menu.BackColor = Color.FromArgb(28, 32, 40);
            menu.ForeColor = Color.Gainsboro;

            ToolStripMenuItem file = new ToolStripMenuItem("文件(&F)");
            file.DropDownItems.Add(MenuItem("重新加载页面\tF5", delegate { Reload(); }));
            file.DropDownItems.Add(MenuItem("重启 DSH 服务", delegate { RestartServer(); }));
            file.DropDownItems.Add(MenuItem("选择工作区…", delegate { ChooseWorkspace(); }));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(MenuItem("运行环境管理…(&R)", delegate { ShowRuntimeManager(); }));
            file.DropDownItems.Add(MenuItem("打开数据目录", delegate { OpenPath(paths.DataDirectory); }));
            file.DropDownItems.Add(MenuItem("打开配置目录", delegate { OpenPath(paths.Root); }));
            file.DropDownItems.Add(MenuItem("打开日志文件", delegate { OpenPath(log.CurrentFile); }));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(MenuItem("退出", delegate { Close(); }));

            ToolStripMenuItem view = new ToolStripMenuItem("视图(&V)");
            view.DropDownItems.Add(MenuItem("放大\tCtrl+Plus", delegate { Zoom(1.1); }));
            view.DropDownItems.Add(MenuItem("缩小\tCtrl+Minus", delegate { Zoom(1 / 1.1); }));
            view.DropDownItems.Add(MenuItem("重置缩放\tCtrl+0", delegate { SetZoom(config.ZoomFactor); }));
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownItems.Add(MenuItem("全屏\tF11", delegate { ToggleFullScreen(); }));
            view.DropDownItems.Add(MenuItem("开发者工具\tF12", delegate { OpenDevTools(); }));
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownItems.Add(MenuItem("复制访问地址", delegate { CopyUrl(); }));
            view.DropDownItems.Add(MenuItem("在系统浏览器中打开", delegate { OpenInBrowser(); }));

            ToolStripMenuItem pluginMenu = new ToolStripMenuItem("插件(&P)");
            pluginMenu.DropDownItems.Add(MenuItem("已加载插件…", delegate { ShowPlugins(); }));
            pluginMenu.DropDownItems.Add(MenuItem("打开插件目录", delegate { OpenPath(paths.PluginsDirectory); }));
            pluginMenu.DropDownItems.Add(MenuItem("重新扫描插件（需重启）", delegate { RestartServer(); }));

            ToolStripMenuItem help = new ToolStripMenuItem("帮助(&H)");
            help.DropDownItems.Add(MenuItem("检查更新…(&U)", delegate { ShowUpdateDialog(); }));
            help.DropDownItems.Add(MenuItem("诊断信息…", delegate { ShowDiagnostics(); }));
            help.DropDownItems.Add(MenuItem("关于 DSH Desktop", delegate { ShowAbout(); }));

            menu.Items.Add(file);
            menu.Items.Add(view);
            menu.Items.Add(pluginMenu);
            menu.Items.Add(help);
            MainMenuStrip = menu;
            Controls.Add(menu);
        }

        private static ToolStripMenuItem MenuItem(string text, EventHandler handler)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += handler;
            return item;
        }

        private void BuildStatusBar()
        {
            status = new StatusStrip();
            status.BackColor = Color.FromArgb(24, 27, 34);
            statusText = new ToolStripStatusLabel("正在启动 DSH…");
            statusText.ForeColor = Color.Gainsboro;
            statusUrl = new ToolStripStatusLabel("");
            statusUrl.ForeColor = Color.FromArgb(150, 156, 170);
            statusUrl.Spring = true;
            statusUrl.TextAlign = ContentAlignment.MiddleRight;
            status.Items.Add(statusText);
            status.Items.Add(statusUrl);
            Controls.Add(status);
        }

        private void BuildSplash()
        {
            splash = new Label();
            splash.Dock = DockStyle.Fill;
            splash.TextAlign = ContentAlignment.MiddleCenter;
            splash.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular);
            splash.ForeColor = Color.Gainsboro;
            splash.Text = "正在启动 DeepSeek Harness…" + Environment.NewLine + Environment.NewLine
                + "首次启动会在 data\\dsh-home 建立运行环境，请稍候。";
            content.Controls.Add(splash);
            splash.BringToFront();
        }

        private async void OnFormLoad(object sender, EventArgs e)
        {
            // Control heights are only real once the handle exists, so the page host
            // is measured again here rather than trusting the constructor's layout.
            LayoutContent();
            WindowState = boundsState;
            LayoutContent();
            string panel = ParseStartupPanel(commandLine);
            if (panel.Length > 0)
            {
                startupPanel = panel;
                // A desktop shortcut can open a panel directly; the delay lets the
                // window paint before the modal dialog covers it. The field keeps the
                // timer alive: a WinForms timer that nothing references never ticks.
                panelTimer = new System.Windows.Forms.Timer();
                panelTimer.Interval = 1200;
                panelTimer.Tick += delegate
                {
                    panelTimer.Stop();
                    panelTimer.Dispose();
                    panelTimer = null;
                    OpenStartupPanel();
                };
                panelTimer.Start();
            }
            await StartAndAttach();
        }

        /// <summary>Reads <c>--open &lt;panel&gt;</c> from the command line.</summary>
        private static string ParseStartupPanel(string[] args)
        {
            for (int i = 0; i < args.Length; i += 1)
            {
                if (string.Equals(args[i], "--open", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1].Trim().ToLowerInvariant();
                }
            }
            return "";
        }

        private void OpenStartupPanel()
        {
            log.Info("opening startup panel: " + startupPanel);
            try
            {
                if (startupPanel == "runtime") { ShowRuntimeManager(); return; }
                if (startupPanel == "update") { ShowUpdateDialog(); return; }
                if (startupPanel == "plugins") { ShowPlugins(); return; }
                if (startupPanel == "diagnostics") { ShowDiagnostics(); return; }
                log.Warn("unknown --open panel: " + startupPanel);
            }
            catch (Exception ex)
            {
                log.Error("startup panel failed: " + ex);
            }
        }

        /// <summary>Starts the server, then hosts the serving URL in WebView2.</summary>
        private async Task StartAndAttach()
        {
            try
            {
                // WebView2 initialization takes seconds on a cold profile, so it runs
                // alongside the server boot instead of after it.
                statusText.Text = "正在启动 WebView2 与 DSH 服务…";
                Task<bool> prepareWebView = PrepareWebView();

                bool started = await Task.Run(delegate { return StartServer(); });
                if (!started && config.Port != 0)
                {
                    // The bundled webserver has no auto-increment: a busy port is a
                    // hard failure, so fall back to an OS-assigned port once.
                    log.Warn("start on port " + config.Port + " failed, retrying on an OS-assigned port");
                    if (server != null)
                    {
                        server.Dispose();
                        server = null;
                    }
                    portOverride = 0;
                    started = await Task.Run(delegate { return StartServer(); });
                }
                if (!started)
                {
                    ShowStartupFailure();
                    return;
                }
                accessUrl = server.Url;
                statusText.Text = "DSH 服务已就绪，正在加载界面…";
                statusUrl.Text = accessUrl;
                log.Info("serving url: " + accessUrl);

                bool prepared;
                try
                {
                    prepared = await prepareWebView;
                }
                catch (Exception ex)
                {
                    log.Error("WebView2 preparation failed: " + ex);
                    prepared = false;
                }
                if (shuttingDown)
                {
                    return;
                }
                if (!prepared)
                {
                    HostInFallbackBrowser();
                    return;
                }
                ShowWebViewAndNavigate();
                CheckUpdatesInBackground();
            }
            catch (Exception ex)
            {
                log.Error("startup failed: " + ex);
                ShowStartupFailure();
            }
        }

        private bool StartServer()
        {
            string nodeExe = ResolveNodeExe();
            if (string.IsNullOrEmpty(nodeExe))
            {
                statusText.Text = "找不到可用的 Node 运行时（文件 → 运行环境管理）";
                return false;
            }
            if (!File.Exists(paths.DshEntry))
            {
                log.Error("bundled dsh entry missing: " + paths.DshEntry);
                return false;
            }

            string host = config.Host;
            int port = portOverride >= 0 ? portOverride : config.Port;
            // This DSH release rejects wildcard binds on purpose: serving the agent
            // runtime to the network exposes remote code execution. Fall back to
            // loopback and tell the operator how to reach other devices safely.
            if (string.Equals(host, "0.0.0.0", StringComparison.Ordinal)
                || string.Equals(host, "::", StringComparison.Ordinal)
                || string.Equals(host, "*", StringComparison.Ordinal))
            {
                log.Warn("refusing to bind " + host
                    + ": this DSH release rejects wildcard binds because they expose remote code execution; using 127.0.0.1");
                statusText.Text = "已改用 127.0.0.1 绑定（DSH 出于安全拒绝通配绑定）";
                host = "127.0.0.1";
            }
            else if (config.Lan)
            {
                log.Info("lan mode: binding " + host
                    + " with the configured trustedHosts; keep the bind on loopback and reach it through a tunnel or a reverse proxy that presents a trusted authority");
            }

            List<string> arguments = new List<string>();
            // `dsh` takes launcher-level overlays as program options and the app's
            // own flags after them: `dsh --profile web --patch <file> --no-open ...`.
            arguments.Add("--profile");
            arguments.Add("web");

            List<string> trusted = new List<string>();
            trusted.AddRange(config.TrustedHosts);
            if (config.Lan && port != 0)
            {
                // A tunnel or proxy presents this authority while the bind stays on
                // the configured host, so the port in the authority must match.
                trusted.Add("localhost:" + port.ToString(CultureInfo.InvariantCulture));
                trusted.Add("127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            }
            List<string> patchFiles = new List<string>();
            foreach (LoadedPlugin plugin in plugins)
            {
                if (!string.IsNullOrEmpty(plugin.Manifest.DshLinkModules))
                {
                    string linkRoot = PluginLoader.ResolveInside(plugin, plugin.Manifest.DshLinkModules);
                    if (linkRoot != null)
                    {
                        string runtimeModules = Path.Combine(paths.Root,
                            Path.Combine("runtime", Path.Combine("app", "node_modules")));
                        PluginLoader.EnsureModuleLink(Path.Combine(linkRoot, "node_modules"), runtimeModules, log);
                    }
                }
                if (plugin.Manifest.DshPatch == null)
                {
                    continue;
                }
                foreach (string relative in plugin.Manifest.DshPatch)
                {
                    string resolved = PluginLoader.ResolveInside(plugin, relative);
                    if (resolved == null || !File.Exists(resolved))
                    {
                        log.Warn("plugin " + plugin.Manifest.Name + ": patch file not found: " + relative);
                        continue;
                    }
                    patchFiles.Add(resolved);
                }
            }
            foreach (string patch in patchFiles)
            {
                arguments.Add("--patch");
                arguments.Add(patch);
            }

            // The booted profile's own flags follow the launcher options.
            arguments.Add("--no-open");
            arguments.Add("--host");
            arguments.Add(host);
            arguments.Add("--port");
            arguments.Add(port.ToString(CultureInfo.InvariantCulture));
            foreach (string entry in trusted)
            {
                if (!string.IsNullOrEmpty(entry))
                {
                    arguments.Add("--trusted-host");
                    arguments.Add(entry);
                }
            }
            foreach (string extra in config.ExtraArgs)
            {
                if (!string.IsNullOrEmpty(extra))
                {
                    arguments.Add(extra);
                }
            }

            string home = paths.ResolveHome(config);
            string workspace = paths.ResolveWorkspace(config);
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(paths.CacheDirectory);

            Dictionary<string, string> environment = new Dictionary<string, string>();
            environment["DSH_HOME"] = home;
            environment["DSH_DESKTOP"] = "1";
            environment["DSH_DESKTOP_VERSION"] = version;
            environment["DSH_DESKTOP_ROOT"] = paths.Root;
            environment["DSH_DESKTOP_PLUGINS"] = paths.PluginsDirectory;
            environment["DSH_DESKTOP_CONFIG"] = Path.Combine(paths.Root, "config.json");
            environment["DSH_DESKTOP_PATCHES"] = string.Join(";", patchFiles.ToArray());
            environment["NODE_COMPILE_CACHE"] = paths.CacheDirectory;
            environment["npm_config_cache"] = paths.CacheDirectory;
            foreach (KeyValuePair<string, string> pair in config.Env)
            {
                environment[pair.Key] = pair.Value;
            }

            server = new DshServer(log);
            bool ok = server.Start(nodeExe, paths.DshEntry, workspace, arguments.ToArray(), environment,
                config.StartupTimeoutSeconds);
            log.Info("dsh home: " + home + ", workspace: " + workspace);
            return ok;
        }

        /// <summary>
        /// Chooses the Node executable: an explicit path, the bundled copy, or a
        /// system installation, in that order, each one checked against the DSH
        /// engine range before it is used.
        /// </summary>
        /// <returns>The executable to run, or an empty string when none is usable.</returns>
        private string ResolveNodeExe()
        {
            if (!string.IsNullOrEmpty(config.NodePath) && File.Exists(config.NodePath))
            {
                log.Info("node: config.json nodePath = " + config.NodePath);
                return config.NodePath;
            }
            bool bundledExists = File.Exists(paths.NodeExe);
            if (bundledExists && !string.Equals(config.NodeMode, "system", StringComparison.OrdinalIgnoreCase))
            {
                NodeProbe bundled = RuntimeProbe.ProbeNode(paths.NodeExe);
                if (bundled.Usable)
                {
                    log.Info("node: bundled " + bundled.Version + " (" + paths.NodeExe + ")");
                    return paths.NodeExe;
                }
                log.Warn("bundled node unusable: " + bundled.Reason);
            }
            NodeProbe system = RuntimeProbe.FindSystemNode();
            if (system.Found && system.Usable)
            {
                log.Info("node: system " + system.Version + " (" + system.Path + ")");
                return system.Path;
            }
            if (bundledExists)
            {
                log.Warn("using the bundled node although it reports " + system.Label);
                return paths.NodeExe;
            }
            if (system.Found)
            {
                log.Error("system node " + system.Version + " does not satisfy " + RuntimeProbe.EngineRangeText());
            }
            else
            {
                log.Error("no Node runtime: the bundled copy is missing and no system Node was found. "
                    + "Use File -> Runtime manager to download it again.");
            }
            return "";
        }

        /// <summary>Opens the runtime panel, which can drop or restore the bundled Node.</summary>
        private void ShowRuntimeManager()
        {
            log.Info("runtime panel: creating form");
            using (RuntimeForm form = new RuntimeForm(paths, log, config, SaveConfig, RestartServer))
            {
                log.Info("runtime panel: showing dialog");
                DialogResult result = form.ShowDialog(this);
                log.Info("runtime panel: closed with " + result);
            }
        }

        /// <summary>Opens the update dialog; installs nothing until the user confirms.</summary>
        private void ShowUpdateDialog()
        {
            using (UpdateForm form = new UpdateForm(paths, log, config, SaveConfig, RestartServer))
            {
                form.ShowDialog(this);
            }
        }

        /// <summary>
        /// Asks npm for a newer DSH after the window is up. Never installs unless
        /// config.json sets autoUpdate, because a silent runtime swap would change
        /// behaviour under the user.
        /// </summary>
        private void CheckUpdatesInBackground()
        {
            bool enabled = !config.CheckUpdatesOnStartup.HasValue || config.CheckUpdatesOnStartup.Value;
            if (!enabled || updateCheckStarted)
            {
                return;
            }
            updateCheckStarted = true;
            Task.Run(delegate { return Updates.Check(paths, config.Channel); }).ContinueWith(delegate(Task<UpdateInfo> task)
            {
                UpdateInfo info = task.Result;
                if (shuttingDown || !IsHandleCreated)
                {
                    return;
                }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (!info.Ok)
                        {
                            log.Info("startup update check failed: " + info.Error);
                            return;
                        }
                        if (!info.UpdateAvailable)
                        {
                            log.Info("dsh runtime is current (" + info.CurrentVersion + ")");
                            return;
                        }
                        log.Info("update available: " + info.CurrentVersion + " -> " + info.TargetVersion);
                        bool auto = config.AutoUpdate.HasValue && config.AutoUpdate.Value;
                        if (!auto)
                        {
                            statusText.Text = "有新版本 " + info.TargetVersion + "（帮助 → 检查更新）";
                            return;
                        }
                        statusText.Text = "自动更新到 " + info.TargetVersion + " …";
                        string nodeExe = ResolveNodeExe();
                        if (string.IsNullOrEmpty(nodeExe))
                        {
                            statusText.Text = "自动更新失败：没有可用的 Node";
                            return;
                        }
                        string version = info.TargetVersion;
                        Task.Run(delegate
                        {
                            return Updates.Install(paths, version, nodeExe, log, null);
                        }).ContinueWith(delegate(Task<string> installTask)
                        {
                            string error = installTask.Result;
                            if (shuttingDown || !IsHandleCreated)
                            {
                                return;
                            }
                            BeginInvoke(new MethodInvoker(delegate
                            {
                                if (string.IsNullOrEmpty(error))
                                {
                                    statusText.Text = "已更新到 " + version + "，正在重启服务";
                                    RestartServer();
                                }
                                else
                                {
                                    statusText.Text = "自动更新失败（帮助 → 检查更新）";
                                    log.Error("auto update failed: " + error);
                                }
                            }));
                        });
                    }));
                }
                catch (InvalidOperationException)
                {
                }
            });
        }

        /// <summary>Creates the WebView2 environment and injects plugin content, without navigating.</summary>
        private async Task<bool> PrepareWebView()
        {
            // WebView2 refuses to initialize from an MTA thread with
            // RPC_E_CHANGED_MODE; this window always runs on the STA UI thread.
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                log.Error("WebView2 requires an STA thread; apartment is " + Thread.CurrentThread.GetApartmentState());
                return false;
            }

            webView = new WebView2();
            webView.Dock = DockStyle.Fill;
            webView.DefaultBackgroundColor = Color.FromArgb(18, 20, 26);
            // The control must be parented before EnsureCoreWebView2Async, otherwise
            // it has no window handle to host the browser and initialization times out.
            // The splash is in front until the swap below.
            content.Controls.Add(webView);
            webView.SendToBack();

            string userDataFolder = Path.Combine(paths.DataDirectory, "webview2");
            Directory.CreateDirectory(userDataFolder);

            CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions(
                "--disable-features=msWebOOUI,msPdfOOUI --autoplay-policy=no-user-gesture-required");
            CoreWebView2Environment environment;
            try
            {
                environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                log.Info("webview2 runtime: " + environment.BrowserVersionString);
                await webView.EnsureCoreWebView2Async(environment);
            }
            catch (Exception ex)
            {
                log.Warn("WebView2 unavailable (" + ex.GetType().Name + ": " + ex.Message + ")");
                try
                {
                    webView.Dispose();
                }
                catch (InvalidOperationException)
                {
                }
                webView = null;
                // A shutdown that interrupts initialization is not a missing runtime:
                // never hand the user a browser window on the way out.
                return shuttingDown;
            }
            CoreWebView2 core = webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = config.DevTools;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;

            core.NewWindowRequested += OnNewWindowRequested;
            core.ProcessFailed += OnProcessFailed;
            // The page owns keyboard focus, so shortcuts are collected in an injected
            // listener and posted back here.
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;
            core.DownloadStarting += OnDownloadStarting;

            await ApplyInjections(core);
            return true;
        }

        /// <summary>
        /// Swaps the splash for the prepared WebView2 and starts loading the
        /// serving URL. Runs on the UI thread once the server is ready.
        /// </summary>
        private void ShowWebViewAndNavigate()
        {
            if (shuttingDown || webView == null || webView.CoreWebView2 == null)
            {
                return;
            }
            if (splash != null)
            {
                content.Controls.Remove(splash);
                splash.Dispose();
                splash = null;
            }
            webView.Visible = true;
            webView.BringToFront();
            menu.BringToFront();
            status.BringToFront();
            LayoutContent();
            webView.BringToFront();
            webView.CoreWebView2.Navigate(accessUrl);
        }

        private async Task ApplyInjections(CoreWebView2 core)
        {
            string bootstrap = "window.__DSH_DESKTOP__ = "
                + "{ version: " + JsonString(version)
                + ", root: " + JsonString(paths.Root)
                + ", plugins: " + PluginIdArray()
                + ", lan: " + (config.Lan ? "true" : "false")
                + ", host: " + JsonString(config.Host)
                + ", platform: " + JsonString("windows") + " };";
            await core.AddScriptToExecuteOnDocumentCreatedAsync(bootstrap);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ShortcutListener());

            foreach (LoadedPlugin plugin in plugins)
            {
                foreach (string relative in plugin.Manifest.InjectCss)
                {
                    string file = PluginLoader.ResolveInside(plugin, relative);
                    if (file == null || !File.Exists(file))
                    {
                        log.Warn("plugin " + plugin.Manifest.Name + ": css not found: " + relative);
                        continue;
                    }
                    string css = File.ReadAllText(file, Encoding.UTF8);
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(CssInjector(css));
                    log.Info("plugin " + plugin.Manifest.Name + ": injected css " + relative);
                }
                foreach (string relative in plugin.Manifest.InjectJs)
                {
                    string file = PluginLoader.ResolveInside(plugin, relative);
                    if (file == null || !File.Exists(file))
                    {
                        log.Warn("plugin " + plugin.Manifest.Name + ": script not found: " + relative);
                        continue;
                    }
                    string script = File.ReadAllText(file, Encoding.UTF8);
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
                    log.Info("plugin " + plugin.Manifest.Name + ": injected js " + relative);
                }
                if (!string.IsNullOrEmpty(plugin.Manifest.UserAgentSuffix))
                {
                    core.Settings.UserAgent = core.Settings.UserAgent + " " + plugin.Manifest.UserAgentSuffix;
                }
            }
        }

        /// <summary>
        /// Page-side listener for the window shortcuts. The WebView2 surface holds
        /// keyboard focus, so a listener here is the only place the launcher can see
        /// Alt combinations while a page is loaded; it forwards them over host messaging.
        /// </summary>
        private static string ShortcutListener()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("(function(){if(window.__DSH_DESKTOP_KEYS__){return;}window.__DSH_DESKTOP_KEYS__=true;");
            builder.Append("var send=function(action){try{window.chrome.webview.postMessage('dshDesktop:'+action);}catch(e){}};");
            builder.Append("document.addEventListener('keydown',function(event){");
            builder.Append("var key=event.key;var action=null;");
            builder.Append("if(event.altKey&&!event.ctrlKey&&!event.metaKey){var alt={r:'runtime',u:'update',p:'plugins',d:'diagnostics',l:'copy-url',m:'chrome'};action=alt[(key||'').toLowerCase()];}");
            builder.Append("else if(event.ctrlKey&&!event.altKey){if(key==='='||key==='+')action='zoom-in';else if(key==='-')action='zoom-out';else if(key==='0')action='zoom-reset';}");
            builder.Append("else if(key==='F5')action='reload';else if(key==='F10')action='chrome';else if(key==='F11')action='fullscreen';else if(key==='F12')action='devtools';");
            builder.Append("if(action){event.preventDefault();event.stopPropagation();send(action);}},true);})();");
            return builder.ToString();
        }

        private static string CssInjector(string css)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("(function(){var s=document.createElement('style');s.setAttribute('data-dsh-desktop','1');");
            builder.Append("s.textContent=").Append(JsonString(css)).Append(";");
            builder.Append("var add=function(){(document.head||document.documentElement).appendChild(s);};");
            builder.Append("if(document.head){add();}else{document.addEventListener('DOMContentLoaded',add);}})();");
            return builder.ToString();
        }

        private string PluginIdArray()
        {
            List<string> ids = new List<string>();
            foreach (LoadedPlugin plugin in plugins)
            {
                ids.Add(JsonString(plugin.Manifest.Name));
            }
            return "[" + string.Join(",", ids.ToArray()) + "]";
        }

        /// <summary>Minimal JSON string encoder for injected values.</summary>
        private static string JsonString(string value)
        {
            if (value == null)
            {
                return "null";
            }
            StringBuilder builder = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }
            return builder.Append("\"").ToString();
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                log.Warn("navigation failed: " + e.WebErrorStatus);
                statusText.Text = "页面加载失败：" + e.WebErrorStatus;
                return;
            }
            statusText.Text = "DSH 已就绪";
            SetZoom(currentZoom <= 0.1 ? config.ZoomFactor : currentZoom);
            Text = EffectiveTitle();
            // Geometry marker: the page must own everything the chrome does not.
            log.Info("layout: client=" + ClientSize.Width + "x" + ClientSize.Height
                + " webview=" + webView.Width + "x" + webView.Height
                + " at " + webView.Left + "," + webView.Top
                + " host=" + content.Width + "x" + content.Height + " at " + content.Left + "," + content.Top
                + " menu=" + (menu.Visible ? menu.Height : 0)
                + " status=" + (status.Visible ? status.Height : 0));
            // Readiness marker for the end-to-end test: the window is showing the app.
            log.Info("webview2 ready: " + accessUrl);
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (config.OpenExternalLinks)
            {
                OpenExternal(e.Uri);
            }
        }

        private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            log.Error("webview2 process failed: " + e.ProcessFailedKind);
            statusText.Text = "渲染进程异常：" + e.ProcessFailedKind;
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                MessageBox.Show("浏览器渲染进程已退出，DSH Desktop 将关闭。", "DSH Desktop",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Close();
            }
        }

        private void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            log.Info("download started: " + e.DownloadOperation.Uri);
        }

        private double currentZoom;
        private int portOverride = -1;
        private volatile bool shuttingDown;
        private bool updateCheckStarted;
        private string startupPanel = "";
        private System.Windows.Forms.Timer panelTimer;

        private void SetZoom(double factor)
        {
            if (webView == null || webView.CoreWebView2 == null)
            {
                return;
            }
            if (factor < 0.25) factor = 0.25;
            if (factor > 5.0) factor = 5.0;
            webView.ZoomFactor = factor;
            currentZoom = factor;
            statusText.Text = "缩放 " + Math.Round(factor * 100).ToString(CultureInfo.InvariantCulture) + "%";
        }

        private void Zoom(double multiplier)
        {
            SetZoom((currentZoom <= 0.1 ? config.ZoomFactor : currentZoom) * multiplier);
        }

        private void Reload()
        {
            if (webView != null && webView.CoreWebView2 != null)
            {
                webView.CoreWebView2.Reload();
            }
        }

        private void OpenDevTools()
        {
            if (webView != null && webView.CoreWebView2 != null && config.DevTools)
            {
                webView.CoreWebView2.OpenDevToolsWindow();
            }
        }

        private void ToggleFullScreen()
        {
            fullScreen = !fullScreen;
            if (fullScreen)
            {
                menu.Visible = false;
                status.Visible = false;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                menu.Visible = true;
                status.Visible = true;
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = FormWindowState.Normal;
            }
        }

        private void CopyUrl()
        {
            if (!string.IsNullOrEmpty(accessUrl))
            {
                try
                {
                    Clipboard.SetText(accessUrl);
                    statusText.Text = "已复制访问地址";
                }
                catch (ExternalException)
                {
                    statusText.Text = "复制失败，请查看日志";
                }
            }
        }

        private void OpenInBrowser()
        {
            if (!string.IsNullOrEmpty(accessUrl))
            {
                OpenExternal(accessUrl);
            }
        }

        private void OpenExternal(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                log.Warn("failed to open " + url + ": " + ex.Message);
            }
        }

        /// <summary>Restarts the bundled server; plugins and config are re-read.</summary>
        private void RestartServer()
        {
            DialogResult answer = MessageBox.Show("重启 DSH 服务会中断当前会话的实时连接（会话记录保留）。是否继续？",
                "DSH Desktop", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK)
            {
                return;
            }
            log.Info("restart requested");
            if (server != null)
            {
                server.Stop();
                server.Dispose();
                server = null;
            }
            if (webView != null)
            {
                webView.Dispose();
                webView = null;
            }
            LoadConfiguration();
            Text = EffectiveTitle();
            MinimumSize = new Size(config.MinWidth, config.MinHeight);
            BuildSplash();
            Task attach = StartAndAttach();
        }

        private void ChooseWorkspace()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择 DSH 的默认工作区（Agent 可读写的项目目录）";
                dialog.ShowNewFolderButton = true;
                if (Directory.Exists(paths.ResolveWorkspace(config)))
                {
                    dialog.SelectedPath = paths.ResolveWorkspace(config);
                }
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                string path = Path.Combine(paths.Root, "config.json");
                try
                {
                    config.Workspace = dialog.SelectedPath;
                    Json.Write(path, config);
                    log.Info("workspace changed to " + dialog.SelectedPath);
                    MessageBox.Show("工作区已保存到 config.json，重启服务后生效。", "DSH Desktop",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("无法写入 config.json：" + ex.Message, "DSH Desktop",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ShowPlugins()
        {
            StringBuilder builder = new StringBuilder();
            if (plugins.Count == 0)
            {
                builder.Append("没有启用任何启动器插件。").Append(Environment.NewLine).Append(Environment.NewLine);
                builder.Append("把插件目录放进：").Append(paths.PluginsDirectory);
            }
            else
            {
                foreach (LoadedPlugin plugin in plugins)
                {
                    builder.Append("• ").Append(plugin.Manifest.Name);
                    if (!string.IsNullOrEmpty(plugin.Manifest.Description))
                    {
                        builder.Append(" — ").Append(plugin.Manifest.Description);
                    }
                    builder.Append(Environment.NewLine);
                    builder.Append("    目录: ").Append(plugin.Directory).Append(Environment.NewLine);
                }
            }
            MessageBox.Show(builder.ToString(), "已加载插件", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowDiagnostics()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("版本: ").Append(version).Append(Environment.NewLine);
            builder.Append("安装目录: ").Append(paths.Root).Append(Environment.NewLine);
            builder.Append("DSH_HOME: ").Append(paths.ResolveHome(config)).Append(Environment.NewLine);
            builder.Append("工作区: ").Append(paths.ResolveWorkspace(config)).Append(Environment.NewLine);
            builder.Append("访问地址: ").Append(accessUrl == null ? "(未启动)" : accessUrl).Append(Environment.NewLine);
            builder.Append("插件数: ").Append(plugins.Count.ToString(CultureInfo.InvariantCulture)).Append(Environment.NewLine);
            builder.Append("日志: ").Append(log.CurrentFile).Append(Environment.NewLine);
            builder.Append("WebView2: ").Append(
                webView != null && webView.CoreWebView2 != null ? "已加载" : "不可用或未初始化").Append(Environment.NewLine);
            builder.Append(Environment.NewLine).Append("--- 服务端输出尾部 ---").Append(Environment.NewLine);
            builder.Append(server == null ? "(无)" : server.OutputTail);
            MessageBox.Show(builder.ToString(), "诊断信息", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowAbout()
        {
            MessageBox.Show("DSH Desktop " + version + Environment.NewLine + Environment.NewLine
                + "把 DeepSeek Harness 打包成双击即用的桌面应用：" + Environment.NewLine
                + "内置 Node 运行时与 DSH 插件框架，独立窗口承载 Web UI，" + Environment.NewLine
                + "支持通过 plugins 目录改造窗口、注入界面与挂载 DSH 插件。" + Environment.NewLine + Environment.NewLine
                + "DSH 主页: https://github.com/deepseek-ai/deepseek-harness",
                "关于 DSH Desktop", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OpenPath(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                    return;
                }
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                log.Warn("failed to open " + path + ": " + ex.Message);
            }
        }

        private void ShowStartupFailure()
        {
            if (splash != null)
            {
                string tail = server == null ? "" : server.OutputTail;
                if (tail.Length > 1200)
                {
                    tail = tail.Substring(tail.Length - 1200);
                }
                splash.Text = "DSH 启动失败。" + Environment.NewLine + Environment.NewLine
                    + "日志：" + log.CurrentFile + Environment.NewLine + Environment.NewLine + tail;
                splash.ForeColor = Color.FromArgb(255, 170, 170);
                content.Controls.Add(splash);
                splash.BringToFront();
            }
            statusText.Text = "启动失败";
        }

        /// <summary>
        /// Runs the Web UI in Edge application mode when the WebView2 runtime is
        /// missing, keeping the bundled server alive for as long as that window.
        /// </summary>
        private void HostInFallbackBrowser()
        {
            string edge = FindEdge();
            if (edge == null)
            {
                log.Warn("no WebView2 runtime and no Edge; opening the default browser");
                OpenExternal(accessUrl);
                MessageBox.Show("未能加载 WebView2 运行时，已在系统默认浏览器中打开 DSH。" + Environment.NewLine
                    + "关闭本对话框将停止 DSH 服务。", "DSH Desktop",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string profile = Path.Combine(paths.DataDirectory, "edge-profile");
            Directory.CreateDirectory(profile);
            ProcessStartInfo info = new ProcessStartInfo(edge);
            info.Arguments = "--app=" + DshServer.Quote(accessUrl) + " --user-data-dir=" + DshServer.Quote(profile)
                + " --no-first-run --no-default-browser-check --window-size="
                + config.Width.ToString(CultureInfo.InvariantCulture) + ","
                + config.Height.ToString(CultureInfo.InvariantCulture);
            info.UseShellExecute = false;
            log.Info("fallback: " + edge + " " + info.Arguments);
            fallbackBrowser = Process.Start(info);
            if (fallbackBrowser != null)
            {
                fallbackBrowser.EnableRaisingEvents = true;
                fallbackBrowser.Exited += delegate { BeginInvoke(new MethodInvoker(Close)); };
            }
            statusText.Text = "已在 Edge 独立窗口打开（未检测到 WebView2 运行时）";
            if (splash != null)
            {
                splash.Text = "未检测到 WebView2 运行时，已改用 Edge 独立窗口打开 DSH。"
                    + Environment.NewLine + Environment.NewLine + accessUrl
                    + Environment.NewLine + Environment.NewLine + "关闭本窗口将停止 DSH 服务。";
                splash.BringToFront();
            }
        }

        private static string FindEdge()
        {
            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Path.Combine("Microsoft", Path.Combine("Edge", Path.Combine("Application", "msedge.exe")))),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Path.Combine("Microsoft", Path.Combine("Edge", Path.Combine("Application", "msedge.exe"))))
            };
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }

        /// <summary>
        /// Handles the window shortcuts wherever the key event arrives from: the form
        /// itself when it has focus, or the WebView2 control when the page does.
        /// </summary>
        /// <param name="key">The pressed key code.</param>
        /// <param name="control">Whether Control was held.</param>
        /// <param name="alt">Whether Alt was held.</param>
        /// <returns>True when the key was consumed.</returns>
        private bool HandleShortcut(Keys key, bool control, bool alt)
        {
            if (alt)
            {
                if (key == Keys.R) { ShowRuntimeManager(); return true; }
                if (key == Keys.U) { ShowUpdateDialog(); return true; }
                if (key == Keys.P) { ShowPlugins(); return true; }
                if (key == Keys.D) { ShowDiagnostics(); return true; }
                if (key == Keys.L) { CopyUrl(); return true; }
                if (key == Keys.M) { ToggleChrome(); return true; }
                return false;
            }
            if (control)
            {
                if (key == Keys.Oemplus || key == Keys.Add) { Zoom(1.1); return true; }
                if (key == Keys.OemMinus || key == Keys.Subtract) { Zoom(1 / 1.1); return true; }
                if (key == Keys.D0) { SetZoom(config.ZoomFactor); return true; }
                return false;
            }
            if (key == Keys.F5) { Reload(); return true; }
            if (key == Keys.F11) { ToggleFullScreen(); return true; }
            if (key == Keys.F12) { OpenDevTools(); return true; }
            if (key == Keys.F10) { ToggleChrome(); return true; }
            if (key == Keys.Escape && fullScreen) { ToggleFullScreen(); return true; }
            return false;
        }

        /// <summary>Keys reported by the injected page listener, which sees them first.</summary>
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message;
            try
            {
                message = e.TryGetWebMessageAsString();
            }
            catch (Exception ex)
            {
                log.Warn("ignoring non-text web message: " + ex.Message);
                return;
            }
            if (string.IsNullOrEmpty(message) || message.IndexOf("dshDesktop", StringComparison.Ordinal) < 0)
            {
                return;
            }
            log.Info("shortcut from page: " + message);
            if (message.IndexOf("runtime", StringComparison.Ordinal) >= 0) { ShowRuntimeManager(); return; }
            if (message.IndexOf("update", StringComparison.Ordinal) >= 0) { ShowUpdateDialog(); return; }
            if (message.IndexOf("plugins", StringComparison.Ordinal) >= 0) { ShowPlugins(); return; }
            if (message.IndexOf("diagnostics", StringComparison.Ordinal) >= 0) { ShowDiagnostics(); return; }
            if (message.IndexOf("copy-url", StringComparison.Ordinal) >= 0) { CopyUrl(); return; }
            if (message.IndexOf("zoom-in", StringComparison.Ordinal) >= 0) { Zoom(1.1); return; }
            if (message.IndexOf("zoom-out", StringComparison.Ordinal) >= 0) { Zoom(1 / 1.1); return; }
            if (message.IndexOf("zoom-reset", StringComparison.Ordinal) >= 0) { SetZoom(config.ZoomFactor); return; }
            if (message.IndexOf("reload", StringComparison.Ordinal) >= 0) { Reload(); return; }
            if (message.IndexOf("chrome", StringComparison.Ordinal) >= 0) { ToggleChrome(); return; }
            if (message.IndexOf("fullscreen", StringComparison.Ordinal) >= 0) { ToggleFullScreen(); return; }
            if (message.IndexOf("devtools", StringComparison.Ordinal) >= 0) { OpenDevTools(); return; }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (HandleShortcut(e.KeyCode, e.Control, e.Alt))
            {
                e.Handled = true;
            }
        }

        private void OnResize(object sender, EventArgs e)
        {
            LayoutContent();
            if (WindowState != FormWindowState.Minimized)
            {
                bool compact = Width < 720;
                status.Visible = !fullScreen && !compact && (config.ShowStatusBar.HasValue ? config.ShowStatusBar.Value : true);
                LayoutContent();
            }
        }

        private void OnMove(object sender, EventArgs e)
        {
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            shuttingDown = true;
            SaveWindowState();
            if (server != null)
            {
                server.Stop();
                server.Dispose();
                server = null;
            }
            if (fallbackBrowser != null)
            {
                try
                {
                    if (!fallbackBrowser.HasExited)
                    {
                        fallbackBrowser.Kill();
                    }
                }
                catch (InvalidOperationException)
                {
                }
                fallbackBrowser = null;
            }
            if (webView != null)
            {
                webView.Dispose();
                webView = null;
            }
            log.Info("window closed");
        }
    }
}
