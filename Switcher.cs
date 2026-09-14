// Steam账户快速切换 V3.1 (白色主题版)
// 通过读写 Steam 的 loginusers.vdf 实现账户切换，支持离线模式启动。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SteamSwitcher
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs e)
            {
                try { File.WriteAllText(Path.Combine(Application.StartupPath, "error.log"), e.Exception.ToString()); } catch { }
                MessageBox.Show(e.Exception.Message, "错误");
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"), e.ExceptionObject.ToString()); } catch { }
            };
            Application.Run(new MainForm());
        }
    }

    // ---------------- VDF 解析 ----------------
    class Vdf
    {
        public class Node
        {
            public List<KeyValuePair<string, object>> Entries = new List<KeyValuePair<string, object>>();

            public string Get(string key)
            {
                foreach (var kv in Entries)
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                        return kv.Value as string;
                return null;
            }
            public void Set(string key, string val)
            {
                for (int i = 0; i < Entries.Count; i++)
                    if (string.Equals(Entries[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    { Entries[i] = new KeyValuePair<string, object>(key, val); return; }
                Entries.Add(new KeyValuePair<string, object>(key, val));
            }
            public Node Child(string key)
            {
                foreach (var kv in Entries)
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                        return kv.Value as Node;
                return null;
            }
        }

        private static string s;
        private static int pos;

        public static Node Parse(string text)
        {
            s = text; pos = 0;
            return ParseNode();
        }

        private static void SkipWs()
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        private static string NextToken()
        {
            SkipWs();
            if (pos >= s.Length) return null;
            char c = s[pos];
            if (c == '{') { pos++; return "{"; }
            if (c == '}') { pos++; return "}"; }
            if (c == '"')
            {
                pos++;
                var sb = new StringBuilder();
                while (pos < s.Length && s[pos] != '"')
                {
                    if (s[pos] == '\\' && pos + 1 < s.Length)
                    {
                        pos++;
                        char e = s[pos];
                        if (e == 'n') sb.Append('\n');
                        else if (e == 't') sb.Append('\t');
                        else if (e == 'r') sb.Append('\r');
                        else sb.Append(e);
                    }
                    else sb.Append(s[pos]);
                    pos++;
                }
                if (pos < s.Length) pos++; // closing quote
                return sb.ToString();
            }
            int start = pos;
            while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] != '{' && s[pos] != '}') pos++;
            return s.Substring(start, pos - start);
        }

        private static Node ParseNode()
        {
            var node = new Node();
            while (true)
            {
                string key = NextToken();
                if (key == null || key == "}") break;
                string val = NextToken();
                if (val == null) break;
                if (val == "{")
                    node.Entries.Add(new KeyValuePair<string, object>(key, ParseNode()));
                else
                    node.Entries.Add(new KeyValuePair<string, object>(key, val));
            }
            return node;
        }

        private static string Escape(string t)
        {
            return t.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r");
        }

        private static void SerNode(Node n, int depth, StringBuilder sb)
        {
            string indent = new string('\t', depth);
            foreach (var kv in n.Entries)
            {
                sb.Append(indent).Append('"').Append(Escape(kv.Key)).Append('"');
                if (kv.Value is Node)
                {
                    sb.Append("\n").Append(indent).Append("{\n");
                    SerNode((Node)kv.Value, depth + 1, sb);
                    sb.Append(indent).Append("}\n");
                }
                else
                {
                    sb.Append("\t\t\"").Append(Escape((string)kv.Value)).Append("\"\n");
                }
            }
        }

        public static string Serialize(Node root)
        {
            var sb = new StringBuilder();
            SerNode(root, 0, sb);
            return sb.ToString();
        }
    }

    // ---------------- 账户模型 ----------------
    class SteamAccount
    {
        public string SteamId;
        public string AccountName;
        public string PersonaName;
        public long Timestamp;
        public bool MostRecent;
        public bool WantsOffline;
        public Vdf.Node Node;
        public Image Avatar;
    }

    // ---------------- 主窗口 ----------------
    public class MainForm : Form
    {
        private string steamPath;
        private string steamExe;
        private List<SteamAccount> accounts = new List<SteamAccount>();
        private SteamAccount selected;
        private CheckBox chkOffline;
        private Button btnLaunch;
        private Button btnOptions;
        private bool silentOn;
        private bool noBrowserOn;
        private bool rememberOn = true;
        private bool exitOnLaunch;
        private bool keepOfflineOn;
        private bool offlineDefaultApplied;
        private int launchState; // 0=普通 1=已发送启动指令 2=再次发送确认
        private bool launchArmed;
        private DropdownForm dropdown;
        private string cacheDir;
        private bool cardHover;

        private Font fName = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
        private Font fSub = new Font("Microsoft YaHei UI", 9.5f);
        private Font fDate = new Font("Microsoft YaHei UI", 8.5f);
        private Font fHint = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);

        private static readonly Color ColCardBg = Color.FromArgb(247, 248, 250);
        private static readonly Color ColCardBorder = Color.FromArgb(224, 227, 233);
        private static readonly Color ColText = Color.FromArgb(30, 34, 40);
        private static readonly Color ColSub = Color.FromArgb(128, 134, 142);
        private static readonly Color ColDate = Color.FromArgb(156, 162, 170);
        private static readonly Color ColAccent = Color.FromArgb(35, 112, 228);

        public Rectangle CardRect = new Rectangle(16, 16, 468, 132);

        public MainForm()
        {
            // 缓存统一保存在 C 盘用户目录下的独立文件夹（必须在读取配置前初始化）
            cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SteamSwitcherCache");
            try { Directory.CreateDirectory(cacheDir); } catch { }

            Text = "Steam账户切换器 V1.1";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            HelpButton = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(500, 252);
            BackColor = Color.White;
            DoubleBuffered = true;
            ShowInTaskbar = false;

            this.HelpButtonClicked += delegate
            {
                MessageBox.Show(this,
                    "1. 点击账户卡片可展开账户列表，点击选择要登录的账户。\n" +
                    "2. 鼠标悬停到列表中的账户上，点右侧出现的 × 可删除该账户的本机登录记录。\n" +
                    "3. 勾选“离线”将以离线模式启动 Steam（无网络时使用）。\n" +
                    "4. 点击“选项”可在弹出窗口中设置启动参数：静默启动、禁用内置浏览器。\n" +
                    "5. 点击“启动 Steam”完成切换并启动。\n\n" +
                    "注意：切换/删除账户需要先退出正在运行的 Steam，程序会自动提示。\n" +
                    "新增账户：请在 Steam 登录界面登录新账户并勾选“记住密码”，\n之后它会自动出现在列表中。",
                    "帮助", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            chkOffline = new CheckBox();
            chkOffline.Text = "离线";
            chkOffline.AutoSize = true;
            chkOffline.Font = fSub;
            chkOffline.ForeColor = ColText;
            chkOffline.BackColor = ColCardBg;
            chkOffline.Location = new Point(CardRect.Right - 78, CardRect.Bottom - 36);
            chkOffline.CheckedChanged += delegate
            {
                if (selected != null)
                {
                    selected.WantsOffline = chkOffline.Checked;
                    WriteOfflineFlag(selected);
                }
            };
            Controls.Add(chkOffline);

            btnLaunch = new Button();
            btnLaunch.Text = "启动 Steam";
            btnLaunch.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            btnLaunch.Size = new Size(CardRect.Width - 78, 66);
            btnLaunch.Location = new Point(16, CardRect.Bottom + 18);
            btnLaunch.FlatStyle = FlatStyle.Flat;
            btnLaunch.FlatAppearance.BorderColor = Color.FromArgb(216, 220, 226);
            btnLaunch.FlatAppearance.BorderSize = 1;
            btnLaunch.BackColor = Color.White;
            btnLaunch.ForeColor = ColText;
            btnLaunch.TextImageRelation = TextImageRelation.Overlay;
            btnLaunch.ImageAlign = ContentAlignment.MiddleLeft;
            btnLaunch.Padding = new Padding(14, 0, 0, 0);
            btnLaunch.TextAlign = ContentAlignment.MiddleCenter;
            btnLaunch.Cursor = Cursors.Hand;
            btnLaunch.MouseEnter += delegate
            {
                if (launchState == 1 && launchArmed)
                {
                    launchState = 2;
                    ApplyLaunchButtonVisual();
                }
            };
            btnLaunch.MouseLeave += delegate
            {
                if (launchState == 1) launchArmed = true;
                if (launchState == 2) { launchState = 1; ApplyLaunchButtonVisual(); }
            };
            btnLaunch.Click += delegate { LaunchSteam(); };
            Controls.Add(btnLaunch);
            btnLaunch.BringToFront();

            // 启动参数选项按钮
            btnOptions = new Button();
            btnOptions.Text = "选项";
            btnOptions.Font = new Font("Microsoft YaHei UI", 10f);
            btnOptions.Size = new Size(70, 66);
            btnOptions.Location = new Point(btnLaunch.Right + 8, btnLaunch.Top);
            btnOptions.FlatStyle = FlatStyle.Flat;
            btnOptions.FlatAppearance.BorderColor = Color.FromArgb(216, 220, 226);
            btnOptions.FlatAppearance.BorderSize = 1;
            btnOptions.BackColor = Color.White;
            btnOptions.ForeColor = ColText;
            btnOptions.Cursor = Cursors.Hand;
            btnOptions.MouseEnter += delegate { btnOptions.BackColor = Color.FromArgb(243, 246, 250); };
            btnOptions.MouseLeave += delegate { btnOptions.BackColor = Color.White; };
            btnOptions.Click += delegate { ShowOptions(); };
            Controls.Add(btnOptions);
            btnOptions.BringToFront();
            LoadLaunchSettings();
            ApplyLaunchButtonVisual();

            Load += delegate
            {
                InitSteam();
                SetFormIcon();
                LoadAccounts();
                SelectInitial();
                LoadAvatarsAsync();
                InitLaunchIcon();
            };
            this.Activated += delegate { if (accounts.Count > 0) ReloadAccounts(); };
        }

        // 窗口图标优先用程序内嵌图标，失败则回退 Steam 图标
        private void SetFormIcon()
        {
            try
            {
                using (Icon ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                    this.Icon = new Icon(ic, ic.Size);
                return;
            }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(steamExe) && File.Exists(steamExe))
                {
                    using (Icon ic = Icon.ExtractAssociatedIcon(steamExe))
                        this.Icon = new Icon(ic, ic.Size);
                }
            }
            catch { }
        }

        // ---------- 初始化 ----------
        private void InitSteam()
        {
            steamPath = null; steamExe = null;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (k != null)
                    {
                        object p = k.GetValue("SteamPath");
                        object e = k.GetValue("SteamExe");
                        if (p != null) steamPath = p.ToString().Replace('/', '\\');
                        if (e != null) steamExe = e.ToString().Replace('/', '\\');
                    }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(steamPath))
            {
                string guess = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
                if (File.Exists(Path.Combine(guess, "steam.exe"))) { steamPath = guess; steamExe = Path.Combine(guess, "steam.exe"); }
            }
            if (string.IsNullOrEmpty(steamExe) && !string.IsNullOrEmpty(steamPath))
                steamExe = Path.Combine(steamPath, "steam.exe");
        }

        private string LoginUsersPath()
        {
            if (string.IsNullOrEmpty(steamPath)) return null;
            return Path.Combine(steamPath, "config", "loginusers.vdf");
        }

        private Encoding DetectEncoding(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(true);
            try { new UTF8Encoding(false, true).GetString(bytes); return new UTF8Encoding(false); }
            catch { return Encoding.Default; }
        }

        private void LoadAccounts()
        {
            lock (accounts)
            {
            accounts.Clear();
            string path = LoginUsersPath();
            if (path == null || !File.Exists(path))
            {
                MessageBox.Show(this, "未找到 Steam (loginusers.vdf)。\n请确认已安装 Steam。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Encoding enc = DetectEncoding(path);
            string text = File.ReadAllText(path, enc);
            Vdf.Node root = Vdf.Parse(text);
            Vdf.Node users = root.Child("users");
            if (users == null) return;
            foreach (var kv in users.Entries)
            {
                Vdf.Node u = kv.Value as Vdf.Node;
                if (u == null) continue;
                var a = new SteamAccount();
                a.SteamId = kv.Key;
                a.AccountName = u.Get("AccountName");
                a.PersonaName = u.Get("PersonaName");
                if (string.IsNullOrEmpty(a.PersonaName)) a.PersonaName = a.AccountName;
                long.TryParse(u.Get("Timestamp"), out a.Timestamp);
                a.MostRecent = u.Get("MostRecent") == "1";
                a.WantsOffline = u.Get("WantsOfflineMode") == "1";
                a.Node = u;
                accounts.Add(a);
            }
            // 按最近登录排序
            accounts.Sort(delegate (SteamAccount x, SteamAccount y) { return y.Timestamp.CompareTo(x.Timestamp); });
            }
        }

        private void ReloadAccounts()
        {
            SteamAccount keep = selected;
            LoadAccounts();
            selected = null;
            if (keep != null)
                foreach (var a in accounts) if (a.SteamId == keep.SteamId) { selected = a; break; }
            if (selected == null) SelectInitial();
            Invalidate();
            LoadAvatarsAsync();
        }

        private void SelectInitial()
        {
            selected = null;
            // 读取上次选择的账户
            string cfg = Path.Combine(cacheDir, "last.cfg");
            string lastId = null;
            try { if (File.Exists(cfg)) lastId = File.ReadAllText(cfg).Trim(); } catch { }
            foreach (var a in accounts)
            {
                if (lastId != null && a.SteamId == lastId) { selected = a; break; }
            }
            if (selected == null)
                foreach (var a in accounts) if (a.MostRecent) { selected = a; break; }
            if (selected == null && accounts.Count > 0) selected = accounts[0];
            if (selected != null && !offlineDefaultApplied)
            {
                // 首次打开时按“保持以离线模式启动”决定离线默认值，之后不再覆盖
                chkOffline.Checked = keepOfflineOn;
                offlineDefaultApplied = true;
            }
            Invalidate();
        }

        private void SaveSelection()
        {
            if (selected == null) return;
            try { File.WriteAllText(Path.Combine(cacheDir, "last.cfg"), selected.SteamId); } catch { }
        }

        private void WriteOfflineFlag(SteamAccount acc)
        {
            string path = LoginUsersPath();
            if (path == null || !File.Exists(path)) return;
            try
            {
                Encoding enc = DetectEncoding(path);
                string text = File.ReadAllText(path, enc);
                Vdf.Node root = Vdf.Parse(text);
                Vdf.Node users = root.Child("users");
                if (users == null) return;
                Vdf.Node target = users.Child(acc.SteamId);
                if (target == null) return;
                target.Set("WantsOfflineMode", acc.WantsOffline ? "1" : "0");
                target.Set("SkipOfflineModeWarning", acc.WantsOffline ? "1" : "0");
                File.WriteAllText(path, Vdf.Serialize(root), enc);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "写入离线标志失败: " + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ApplyMostRecent(SteamAccount acc)
        {
            string path = LoginUsersPath();
            if (path == null || !File.Exists(path)) return;
            foreach (var a in accounts)
            {
                if (a.Node == null) continue;
                a.Node.Set("MostRecent", a == acc ? "1" : "0");
            }
            acc.Node.Set("WantsOfflineMode", acc.WantsOffline ? "1" : "0");
            acc.Node.Set("SkipOfflineModeWarning", acc.WantsOffline ? "1" : "0");
            // 写入时需从根节点序列化
            Encoding enc = DetectEncoding(path);
            string text = File.ReadAllText(path, enc);
            Vdf.Node root = Vdf.Parse(text);
            Vdf.Node users = root.Child("users");
            if (users != null)
            {
                foreach (var a in accounts)
                {
                    if (a.Node == null) continue;
                    Vdf.Node target = users.Child(a.SteamId);
                    if (target == null) continue;
                    // 新版 Steam 使用 AutoLogin，旧版使用 MostRecent，两者都写入保证兼容
                    target.Set("MostRecent", a == acc ? "1" : "0");
                    target.Set("AutoLogin", a == acc ? "1" : "0");
                    if (a == acc)
                    {
                        target.Set("WantsOfflineMode", acc.WantsOffline ? "1" : "0");
                        target.Set("SkipOfflineModeWarning", acc.WantsOffline ? "1" : "0");
                    }
                }
                File.WriteAllText(path, Vdf.Serialize(root), enc);
            }
        }

        // ---------- 启动 ----------
        private void LoadLaunchSettings()
        {
            silentOn = false;
            noBrowserOn = false;
            exitOnLaunch = false;
            keepOfflineOn = false;
            try
            {
                string p = Path.Combine(cacheDir, "launch.cfg");
                if (!File.Exists(p)) return; // 无记录时默认记住选项
                string[] lines = File.ReadAllLines(p);
                for (int i = 0; i < lines.Length; i++)
                {
                    string l = lines[i].Trim();
                    if (l == "remember") rememberOn = true;
                    else if (l == "noremember") rememberOn = false;
                    else if (l == "-silent") silentOn = true;
                    else if (l == "-no-browser") noBrowserOn = true;
                    else if (l == "-exit-on-launch") exitOnLaunch = true;
                    else if (l == "-keep-offline") keepOfflineOn = true;
                }
                if (!rememberOn) { silentOn = false; noBrowserOn = false; exitOnLaunch = false; keepOfflineOn = false; }
            }
            catch { }
        }

        private void SaveLaunchSettings()
        {
            try
            {
                var sb = new StringBuilder();
                if (rememberOn)
                {
                    sb.AppendLine("remember");
                    if (silentOn) sb.AppendLine("-silent");
                    if (noBrowserOn) sb.AppendLine("-no-browser");
                    if (exitOnLaunch) sb.AppendLine("-exit-on-launch");
                    if (keepOfflineOn) sb.AppendLine("-keep-offline");
                }
                else
                {
                    sb.AppendLine("noremember");
                }
                File.WriteAllText(Path.Combine(cacheDir, "launch.cfg"), sb.ToString());
            }
            catch { }
        }

        // 弹出式启动参数设置窗口
        private void ShowOptions()
        {
            using (OptionsForm f = new OptionsForm())
            {
                f.ChkSilent.Checked = silentOn;
                f.ChkNoBrowser.Checked = noBrowserOn;
                f.ChkKeepOffline.Checked = keepOfflineOn;
                f.ChkRemember.Checked = rememberOn;
                f.ChkExitOnLaunch.Checked = exitOnLaunch;
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    silentOn = f.ChkSilent.Checked;
                    noBrowserOn = f.ChkNoBrowser.Checked;
                    keepOfflineOn = f.ChkKeepOffline.Checked;
                    rememberOn = f.ChkRemember.Checked;
                    exitOnLaunch = f.ChkExitOnLaunch.Checked;
                    SaveLaunchSettings();
                }
            }
        }

        // 启动按钮三态：普通 / 已发送(绿) / 再次确认(红)
        private void ApplyLaunchButtonVisual()
        {
            switch (launchState)
            {
                case 1:
                    btnLaunch.Text = "已发送启动指令";
                    btnLaunch.ForeColor = Color.FromArgb(24, 138, 64);
                    btnLaunch.BackColor = Color.FromArgb(232, 247, 236);
                    btnLaunch.FlatAppearance.BorderColor = Color.FromArgb(178, 220, 190);
                    break;
                case 2:
                    btnLaunch.Text = "再次发送启动命令？";
                    btnLaunch.ForeColor = Color.FromArgb(204, 51, 51);
                    btnLaunch.BackColor = Color.FromArgb(253, 234, 234);
                    btnLaunch.FlatAppearance.BorderColor = Color.FromArgb(235, 180, 180);
                    break;
                default:
                    btnLaunch.Text = "启动 Steam";
                    btnLaunch.ForeColor = ColText;
                    btnLaunch.BackColor = Color.White;
                    btnLaunch.FlatAppearance.BorderColor = Color.FromArgb(216, 220, 226);
                    break;
            }
        }

        // 新版 Steam 启动时按注册表 AutoLoginUser 决定自动登录的账户，必须一并写入
        private void ApplyAutoLoginRegistry(SteamAccount acc)
        {
            if (acc == null || string.IsNullOrEmpty(acc.AccountName)) return;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true))
                {
                    if (k != null) k.SetValue("AutoLoginUser", acc.AccountName);
                }
            }
            catch { }
        }

        private bool IsSteamRunning()
        {
            return Process.GetProcessesByName("steam").Length > 0;
        }

        private void KillSteam()
        {
            // 优雅关闭：Steam 官方支持的 -shutdown 参数（包括托盘在内的完整退出）
            try
            {
                if (!string.IsNullOrEmpty(steamExe) && File.Exists(steamExe))
                    Process.Start(steamExe, "-shutdown");
            }
            catch { }
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(400);
                if (!IsSteamRunning()) { Thread.Sleep(500); return; }
            }
            // 超时后强制结束残留进程
            foreach (Process p in Process.GetProcessesByName("steam"))
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
            }
            Thread.Sleep(800);
        }

        private void LaunchSteam()
        {
            if (selected == null) return;
            if (string.IsNullOrEmpty(steamExe) || !File.Exists(steamExe))
            {
                MessageBox.Show(this, "未找到 steam.exe，无法启动。", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (IsSteamRunning())
            {
                var r = MessageBox.Show(this,
                    "检测到 Steam 正在运行。\n切换账户需要退出 Steam，是否结束 Steam 进程？",
                    "切换账户", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
                KillSteam();
            }
            try
            {
                ApplyMostRecent(selected);
                ApplyAutoLoginRegistry(selected);
                SaveSelection();
                string args = "";
                if (silentOn) args += "-silent ";
                if (noBrowserOn) args += "-no-browser ";
                if (args.Length > 0)
                    Process.Start(steamExe, args.Trim());
                else
                    Process.Start(steamExe);
                launchState = 1;
                launchArmed = false;
                ApplyLaunchButtonVisual();
                // 启动 Steam 后自动退出：后台轮询检测到 steam 进程即关闭本程序
                if (exitOnLaunch)
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            for (int i = 0; i < 120; i++)
                            {
                                Thread.Sleep(500);
                                try
                                {
                                    if (Process.GetProcessesByName("steam").Length > 0)
                                    {
                                        BeginInvoke((Action)delegate { try { Close(); } catch { } });
                                        return;
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "启动失败: " + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void NewAccount()
        {
            MessageBox.Show(this,
                "添加新账户的方法：\n\n" +
                "1. 在 Steam 中选择“更改账户”退出当前登录。\n" +
                "2. 登录新账户时勾选“记住密码”。\n" +
                "3. 之后该账户会自动出现在本工具的账户列表中。",
                "新建账户", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!string.IsNullOrEmpty(steamExe) && File.Exists(steamExe))
                Process.Start(steamExe);
        }

        // ---------- 删除本地账户 ----------
        private void DeleteAccount(SteamAccount a)
        {
            if (a == null) return;
            if (IsSteamRunning())
            {
                var r0 = MessageBox.Show(this,
                    "Steam 正在运行，退出时可能会把删除的账户重新写回。\n建议先退出 Steam 再删除。仍要继续吗？",
                    "删除账户", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r0 != DialogResult.Yes) return;
            }
            var r = MessageBox.Show(this,
                "确定要从本机删除账户 “" + (a.AccountName ?? a.SteamId) + "” 的登录记录吗？\n\n" +
                "仅删除本机的登录记录，不会注销或删除 Steam 账户本身。\n下次在 Steam 登录该账户需重新输入密码。",
                "删除账户", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            string path = LoginUsersPath();
            if (path != null && File.Exists(path))
            {
                try
                {
                    Encoding enc = DetectEncoding(path);
                    string text = File.ReadAllText(path, enc);
                    Vdf.Node root = Vdf.Parse(text);
                    Vdf.Node users = root.Child("users");
                    if (users != null)
                    {
                        users.Entries.RemoveAll(delegate (KeyValuePair<string, object> kv)
                        {
                            return string.Equals(kv.Key, a.SteamId, StringComparison.OrdinalIgnoreCase);
                        });
                        File.WriteAllText(path, Vdf.Serialize(root), enc);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "删除失败: " + ex.Message, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            // 清理本工具的缓存
            try
            {
                string img = Path.Combine(cacheDir, a.SteamId + ".img");
                if (File.Exists(img)) File.Delete(img);
                string last = Path.Combine(cacheDir, "last.cfg");
                if (File.Exists(last) && File.ReadAllText(last).Trim() == a.SteamId) File.Delete(last);
            }
            catch { }

            if (selected == a) selected = null;
            LoadAccounts();
            if (selected == null) SelectInitial();
            Invalidate();
        }

        // ---------- 头像 ----------
        private void LoadAvatarsAsync()
        {
            if (accounts.Count == 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    SteamAccount[] snapshot;
                    lock (accounts) { snapshot = accounts.ToArray(); }
                    foreach (SteamAccount a in snapshot)
                    {
                        Image img = GetAvatar(a);
                        try
                        {
                            BeginInvoke((Action)delegate
                            {
                                if (a.Avatar != null) { a.Avatar.Dispose(); a.Avatar = null; }
                                a.Avatar = img;
                                Invalidate();
                                if (dropdown != null && dropdown.Visible) dropdown.Invalidate();
                            });
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(Path.Combine(cacheDir ?? "", "error_avatar.log"), ex.ToString()); } catch { }
                }
            });
        }

        private Image SafeLoadImage(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read))
                using (var img = Image.FromStream(fs))
                    return new Bitmap(img);
            }
            catch { return null; }
        }

        private Image GetAvatar(SteamAccount a)
        {
            if (a == null) return null;
            // 1. Steam 本地头像缓存 (config\avatarcache\<SteamID>.png)
            Image local = FindSteamCacheAvatar(a.SteamId);
            if (local != null)
            {
                SaveAvatarCopy(local, a.SteamId);
                return local;
            }
            // 2. 本工具的持久缓存（Steam 清缓存后仍可显示）
            string cache = Path.Combine(cacheDir, a.SteamId + ".img");
            if (File.Exists(cache))
            {
                Image im = SafeLoadImage(cache);
                if (im != null) return im;
            }
            // 3. 网络兜底（steamcommunity 被墙时自动失败，不影响本地显示）
            string url = FetchProfileAvatarUrl(a.SteamId);
            if (url != null)
            {
                try
                {
                    byte[] data = DownloadData(url, 6000);
                    File.WriteAllBytes(cache, data);
                    using (var ms = new MemoryStream(data))
                    using (var img = Image.FromStream(ms))
                        return new Bitmap(img);
                }
                catch { }
            }
            return null;
        }

        // 自动识别 Steam 本地头像缓存目录
        private Image FindSteamCacheAvatar(string steamId)
        {
            if (string.IsNullOrEmpty(steamPath) || string.IsNullOrEmpty(steamId)) return null;
            string ac = Path.Combine(steamPath, "config", "avatarcache");
            try
            {
                if (!Directory.Exists(ac)) return null;
                foreach (string f in Directory.GetFiles(ac))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (string.Equals(name, steamId, StringComparison.OrdinalIgnoreCase))
                    {
                        Image im = SafeLoadImage(f);
                        if (im != null) return im;
                    }
                }
            }
            catch { }
            return null;
        }

        // 把 Steam 缓存的头像复制到本工具目录，留作离线备份
        private void SaveAvatarCopy(Image img, string steamId)
        {
            try
            {
                string p = Path.Combine(cacheDir, steamId + ".img");
                if (!File.Exists(p))
                    img.Save(p, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch { }
        }

        private string FetchProfileAvatarUrl(string steamId)
        {
            try
            {
                string xml = Encoding.UTF8.GetString(
                    DownloadData("https://steamcommunity.com/profiles/" + steamId + "/?xml=1", 6000));
                var m = Regex.Match(xml, @"<avatarFull>\s*(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?\s*</avatarFull>",
                    RegexOptions.Singleline);
                if (m.Success && m.Groups[1].Value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return m.Groups[1].Value.Trim();
            }
            catch { }
            return null;
        }

        private byte[] DownloadData(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "Mozilla/5.0 SteamAccountSwitcher";
            using (var resp = req.GetResponse())
            using (var ms = new MemoryStream())
            {
                resp.GetResponseStream().CopyTo(ms);
                return ms.ToArray();
            }
        }

        private void InitLaunchIcon()
        {
            try
            {
                if (!string.IsNullOrEmpty(steamExe) && File.Exists(steamExe))
                {
                    Icon ic = Icon.ExtractAssociatedIcon(steamExe);
                    if (ic != null) btnLaunch.Image = ic.ToBitmap();
                }
            }
            catch { }
            if (btnLaunch.Image == null)
                btnLaunch.Image = DrawSteamFallback(32);
        }

        private Bitmap DrawSteamFallback(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            using (GraphicsPath p = Round(new Rectangle(0, 0, size - 1, size - 1), size / 2))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(35, 112, 228)))
                    g.FillPath(b, p);
                using (Pen w = new Pen(Color.White, size / 8f))
                {
                    g.DrawEllipse(w, size / 4, size / 4, size / 2, size / 2);
                }
                using (SolidBrush b2 = new SolidBrush(Color.White))
                    g.FillEllipse(b2, size / 2 - size / 10, size / 2 - size / 10, size / 5, size / 5);
            }
            return bmp;
        }

        // ---------- 绘制 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // 卡片
            Color bg = cardHover ? Color.FromArgb(242, 245, 249) : ColCardBg;
            using (GraphicsPath p = Round(CardRect, 8))
            {
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, p);
                using (Pen pen = new Pen(ColCardBorder)) g.DrawPath(pen, p);
            }

            if (selected == null)
            {
                TextRenderer.DrawText(g, "未找到 Steam 账户", fHint,
                    new Rectangle(CardRect.X + 20, CardRect.Y + 50, 300, 32), ColSub,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
            else
            {
                // 头像
                Rectangle avRect = new Rectangle(CardRect.X + 12, CardRect.Y + 12, 76, 76);
                DrawAvatar(g, selected, avRect);

                int tx = CardRect.X + 104;
                TextRenderer.DrawText(g, selected.AccountName ?? "", fName,
                    new Point(tx, CardRect.Y + 18), ColText);
                TextRenderer.DrawText(g, selected.PersonaName ?? "", fSub,
                    new Point(tx, CardRect.Y + 50), ColSub);
                TextRenderer.DrawText(g, FormatDate(selected.Timestamp), fDate,
                    new Point(tx, CardRect.Y + 76), ColDate);
            }

            // 下拉箭头
            int ax = CardRect.Right - 32, ay = CardRect.Y + 44;
            using (SolidBrush b = new SolidBrush(ColDate))
            {
                PointF[] tri = new PointF[] {
                    new PointF(ax - 6, ay), new PointF(ax + 6, ay), new PointF(ax, ay + 7) };
                g.FillPolygon(b, tri);
            }
        }

        private string FormatDate(long ts)
        {
            if (ts <= 0) return "";
            try
            {
                DateTime d = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts).ToLocalTime();
                return d.ToString("yyyy年M月d日");
            }
            catch { return ""; }
        }

        private void DrawAvatar(Graphics g, SteamAccount a, Rectangle r)
        {
            if (a.Avatar != null)
            {
                using (GraphicsPath p = Round(r, 6))
                {
                    g.SetClip(p);
                    g.DrawImage(a.Avatar, r);
                    g.ResetClip();
                    using (Pen pen = new Pen(Color.FromArgb(225, 228, 233)))
                        g.DrawPath(pen, p);
                }
            }
            else
            {
                using (GraphicsPath p = Round(r, 6))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 64, 70)))
                {
                    g.FillPath(b, p);
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    using (Font f = new Font("Segoe UI", 22f, FontStyle.Bold))
                    using (SolidBrush wb = new SolidBrush(Color.White))
                        g.DrawString("?", f, wb, new RectangleF(r.X, r.Y - 2, r.Width, r.Height), sf);
                }
            }
        }

        private static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------- 交互 ----------
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = CardRect.Contains(e.Location);
            if (over != cardHover)
            {
                cardHover = over;
                Cursor = over ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (CardRect.Contains(e.Location)) ToggleDropdown();
        }

        private void ToggleDropdown()
        {
            if (dropdown != null && dropdown.Visible) { dropdown.Close(); return; }
            if (accounts.Count == 0) return;
            dropdown = new DropdownForm();
            dropdown.Items = accounts;
            dropdown.SelectedId = selected != null ? selected.SteamId : null;
            dropdown.OnPick = delegate (SteamAccount a)
            {
                selected = a;
                chkOffline.Checked = a.WantsOffline;
                SaveSelection();
                Invalidate();
            };
            dropdown.OnDelete = DeleteAccount;
            dropdown.OnNew = NewAccount;
            Point cardScreen = PointToScreen(new Point(CardRect.X, CardRect.Bottom + 2));
            dropdown.ShowBelow(cardScreen, CardRect.Width, this);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (dropdown != null) dropdown.Close();
        }
    }

    // ---------------- 启动参数设置窗口 ----------------
    class OptionsForm : Form
    {
        public readonly CheckBox ChkSilent = new CheckBox();
        public readonly CheckBox ChkNoBrowser = new CheckBox();
        public readonly CheckBox ChkKeepOffline = new CheckBox();
        public readonly CheckBox ChkRemember = new CheckBox();
        public readonly CheckBox ChkExitOnLaunch = new CheckBox();

        public OptionsForm()
        {
            Text = "启动参数";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(400, 270);
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10f);

            Label tip = new Label();
            tip.Text = "勾选的参数将在点击“启动 Steam”时生效：";
            tip.Location = new Point(20, 16);
            tip.AutoSize = true;
            tip.ForeColor = Color.FromArgb(128, 134, 142);
            Controls.Add(tip);

            ChkSilent.Text = "静默启动 (-silent)，启动后缩到系统托盘";
            ChkSilent.Location = new Point(22, 50);
            ChkSilent.AutoSize = true;
            ChkSilent.Cursor = Cursors.Hand;
            Controls.Add(ChkSilent);

            ChkNoBrowser.Text = "禁用内置浏览器 (-no-browser)";
            ChkNoBrowser.Location = new Point(22, 84);
            ChkNoBrowser.AutoSize = true;
            ChkNoBrowser.Cursor = Cursors.Hand;
            Controls.Add(ChkNoBrowser);

            ChkKeepOffline.Text = "保持以离线模式启动（配合“记住”生效）";
            ChkKeepOffline.Location = new Point(22, 118);
            ChkKeepOffline.AutoSize = true;
            ChkKeepOffline.Cursor = Cursors.Hand;
            Controls.Add(ChkKeepOffline);

            ChkRemember.Text = "记住选择的选项（下次启动仍生效）";
            ChkRemember.Location = new Point(22, 152);
            ChkRemember.AutoSize = true;
            ChkRemember.Cursor = Cursors.Hand;
            Controls.Add(ChkRemember);

            ChkExitOnLaunch.Text = "启动 Steam 后自动退出本程序";
            ChkExitOnLaunch.Location = new Point(22, 186);
            ChkExitOnLaunch.AutoSize = true;
            ChkExitOnLaunch.Cursor = Cursors.Hand;
            Controls.Add(ChkExitOnLaunch);

            Button ok = new Button();
            ok.Text = "确定";
            ok.Size = new Size(92, 34);
            ok.Location = new Point(ClientSize.Width - 204, ClientSize.Height - 50);
            ok.FlatStyle = FlatStyle.Flat;
            ok.BackColor = Color.FromArgb(35, 112, 228);
            ok.ForeColor = Color.White;
            ok.FlatAppearance.BorderSize = 0;
            ok.Cursor = Cursors.Hand;
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Size = new Size(92, 34);
            cancel.Location = new Point(ClientSize.Width - 102, ClientSize.Height - 50);
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.FlatAppearance.BorderColor = Color.FromArgb(216, 220, 226);
            cancel.ForeColor = ColText;
            cancel.BackColor = Color.White;
            cancel.Cursor = Cursors.Hand;
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private static readonly Color ColText = Color.FromArgb(30, 34, 40);
    }

    // ---------------- 下拉账户列表 ----------------
    class DropdownForm : Form
    {
        public List<SteamAccount> Items;
        public string SelectedId;
        public Action<SteamAccount> OnPick;
        public Action<SteamAccount> OnDelete;
        public Action OnNew;

        private int hover = -1;
        private bool hoverDelete;
        private int scroll = 0;
        private const int ItemH = 104;
        private const int AvSize = 64;
        private const int DelSize = 30;

        private Font fName = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
        private Font fSub = new Font("Microsoft YaHei UI", 9f);
        private Font fDate = new Font("Microsoft YaHei UI", 8.5f);
        private Font fNew = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
        private Font fPlus = new Font("Segoe UI", 26f, FontStyle.Bold);

        private static readonly Color ColSelBg = Color.FromArgb(232, 242, 255);
        private static readonly Color ColHover = Color.FromArgb(244, 247, 251);
        private static readonly Color ColAccent = Color.FromArgb(35, 112, 228);
        private static readonly Color ColText = Color.FromArgb(30, 34, 40);
        private static readonly Color ColSub = Color.FromArgb(128, 134, 142);
        private static readonly Color ColDate = Color.FromArgb(156, 162, 170);
        private static readonly Color ColLine = Color.FromArgb(238, 240, 244);

        public DropdownForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.White;
            DoubleBuffered = true;
        }

        public void ShowBelow(Point topLeft, int width, Form owner)
        {
            int total = (Items.Count + 1) * ItemH;
            Rectangle wa = Screen.FromPoint(topLeft).WorkingArea;
            int maxH = wa.Bottom - topLeft.Y - 4;
            if (maxH < ItemH * 2) maxH = ItemH * 2; // 允许向上覆盖
            int h = Math.Min(total, Math.Max(maxH, ItemH * 2));
            Bounds = new Rectangle(topLeft.X, topLeft.Y, width, h);
            Show(owner);
            Activate();
        }

        private int TotalHeight { get { return (Items.Count + 1) * ItemH; } }
        private int VisibleHeight { get { return ClientSize.Height; } }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll -= e.Delta / 2;
            if (scroll < 0) scroll = 0;
            if (scroll + VisibleHeight > TotalHeight) scroll = Math.Max(0, TotalHeight - VisibleHeight);
            Invalidate();
        }

        private int IndexAt(Point p)
        {
            int i = (p.Y + scroll) / ItemH;
            if (p.X < 0 || p.X > ClientSize.Width) return -1;
            if (i < 0 || i >= Items.Count + 1) return -1;
            if (p.Y + scroll >= TotalHeight) return -1;
            return i;
        }

        // 第 i 行（账户行）右侧删除按钮的命中区域
        private Rectangle DeleteRect(int i)
        {
            return new Rectangle(ClientSize.Width - 52, i * ItemH - scroll + (ItemH - DelSize) / 2, DelSize, DelSize);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Location);
            bool overDel = i >= 0 && i < Items.Count && DeleteRect(i).Contains(e.Location);
            if (i != hover || overDel != hoverDelete)
            {
                hover = i;
                hoverDelete = overDel;
                Cursor = overDel ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            hoverDelete = false;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i = IndexAt(e.Location);
            if (i < 0) return;
            if (i < Items.Count)
            {
                if (hoverDelete && DeleteRect(i).Contains(e.Location))
                {
                    SteamAccount a = Items[i];
                    Close();
                    if (OnDelete != null) OnDelete(a);
                    return;
                }
                if (OnPick != null) OnPick(Items[i]);
                Close();
            }
            else
            {
                if (OnNew != null) OnNew();
                Close();
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (Pen border = new Pen(Color.FromArgb(208, 211, 218)))
                g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            int count = Items.Count + 1;
            for (int i = 0; i < count; i++)
            {
                int y = i * ItemH - scroll;
                if (y + ItemH < 0 || y > ClientSize.Height) continue;
                var clip = new Rectangle(0, Math.Max(0, y), ClientSize.Width,
                    Math.Min(ItemH, ClientSize.Height - Math.Max(0, y)));
                g.SetClip(clip);

                bool isSelected = i < Items.Count && Items[i].SteamId == SelectedId;
                if (isSelected)
                {
                    using (SolidBrush b = new SolidBrush(ColSelBg))
                        g.FillRectangle(b, 0, y, ClientSize.Width, ItemH);
                    using (var dashed = new Pen(ColAccent, 2))
                    {
                        dashed.DashStyle = DashStyle.Dash;
                        g.DrawRectangle(dashed, 3, y + 3, ClientSize.Width - 7, ItemH - 7);
                    }
                }
                else if (i == hover)
                {
                    using (SolidBrush b = new SolidBrush(ColHover))
                        g.FillRectangle(b, 0, y, ClientSize.Width, ItemH);
                }

                if (i < Items.Count)
                {
                    SteamAccount a = Items[i];
                    var avRect = new Rectangle(18, y + (ItemH - AvSize) / 2, AvSize, AvSize);
                    DrawAvatar(g, a, avRect);

                    int tx = avRect.Right + 16;
                    TextRenderer.DrawText(g, a.AccountName ?? "", fName,
                        new Point(tx, y + 20), ColText);
                    TextRenderer.DrawText(g, a.PersonaName ?? "", fSub,
                        new Point(tx, y + 48), ColSub);
                    string date = FormatDate(a.Timestamp);
                    if (date.Length > 0)
                        TextRenderer.DrawText(g, date, fDate, new Point(tx, y + 72), ColDate);

                    // 悬停时在右侧显示删除按钮 ×
                    if (i == hover)
                    {
                        Rectangle dr = DeleteRect(i);
                        Color delBg = hoverDelete ? Color.FromArgb(254, 235, 235) : Color.FromArgb(238, 240, 244);
                        Color delFg = hoverDelete ? Color.FromArgb(214, 69, 65) : Color.FromArgb(120, 126, 134);
                        using (GraphicsPath dp = Round(dr, DelSize / 2))
                        using (SolidBrush db = new SolidBrush(delBg))
                            g.FillPath(db, dp);
                        int m = DelSize / 2 - 7;
                        using (Pen xp = new Pen(delFg, 2f))
                        {
                            xp.StartCap = LineCap.Round;
                            xp.EndCap = LineCap.Round;
                            g.DrawLine(xp, dr.X + m, dr.Y + m, dr.Right - m, dr.Bottom - m);
                            g.DrawLine(xp, dr.Right - m, dr.Y + m, dr.X + m, dr.Bottom - m);
                        }
                    }
                }
                else
                {
                    // 新建账户
                    var sq = new Rectangle(18, y + (ItemH - AvSize) / 2, AvSize, AvSize);
                    using (GraphicsPath p = Round(sq, 6))
                    using (SolidBrush b = new SolidBrush(ColAccent))
                        g.FillPath(b, p);
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    using (SolidBrush wb = new SolidBrush(Color.White))
                        g.DrawString("+", fPlus, wb, new RectangleF(sq.X, sq.Y - 3, sq.Width, sq.Height), sf);
                    TextRenderer.DrawText(g, "新建账户", fNew,
                        new Point(sq.Right + 16, y + ItemH / 2 - 12), ColAccent);
                }

                if (i < count - 1 && i + 1 != count - 1)
                {
                    using (Pen pen = new Pen(ColLine))
                        g.DrawLine(pen, 12, y + ItemH, ClientSize.Width - 12, y + ItemH);
                }
                g.ResetClip();
            }
        }

        private string FormatDate(long ts)
        {
            if (ts <= 0) return "";
            try
            {
                DateTime d = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts).ToLocalTime();
                return d.ToString("yyyy年M月d日");
            }
            catch { return ""; }
        }

        private void DrawAvatar(Graphics g, SteamAccount a, Rectangle r)
        {
            if (a.Avatar != null)
            {
                using (GraphicsPath p = Round(r, 6))
                {
                    g.SetClip(p);
                    g.DrawImage(a.Avatar, r);
                    g.ResetClip();
                    using (Pen pen = new Pen(Color.FromArgb(225, 228, 233)))
                        g.DrawPath(pen, p);
                }
            }
            else
            {
                using (GraphicsPath p = Round(r, 6))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 64, 70)))
                {
                    g.FillPath(b, p);
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    using (Font f = new Font("Segoe UI", 20f, FontStyle.Bold))
                    using (SolidBrush wb = new SolidBrush(Color.White))
                        g.DrawString("?", f, wb, new RectangleF(r.X, r.Y - 2, r.Width, r.Height), sf);
                }
            }
        }

        private static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Vdf.Node 的根访问辅助
    static class VdfExt
    {
        // 保留：写入离线标志时从任意节点向上序列化并不必要，
        // 统一在调用处重新解析根节点处理。
    }
}
