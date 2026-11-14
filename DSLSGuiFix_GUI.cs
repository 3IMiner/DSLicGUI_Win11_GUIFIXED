// DSLS_GuiFix (GUI) —— 修复 DS License Server 管理界面在 Win10 1903+ / Win11 上启动即崩溃的问题
//
// 补丁内容（6 字节，长度/偏移/分支结构均不变）：
//   sun/awt/windows/WComponentPeer.addDropTarget()    B6 03 35 -> 09 00 00  (lconst_0; nop; nop)
//   sun/awt/windows/WComponentPeer.removeDropTarget() B6 03 3E -> 00 00 00  (nop; nop; nop)
//
// 定位方式：解析 class 常量池，按被调用方法名找 invoke 下标，任意 Java 8 的 rt.jar 通用。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Patcher
{
    public const string EntryName = "sun/awt/windows/WComponentPeer.class";

    public static string Patch(byte[] cls, out byte[] result, out int addCount, out int remCount)
    {
        result = cls;
        addCount = 0; remCount = 0;
        if (cls == null || cls.Length < 10) return "UNKNOWN";
        try
        {
            var cf = new CF(cls);
            cf.ParsePool();
            List<int> add, rem;
            cf.ScanCode(out add, out rem);
            addCount = add.Count;
            remCount = rem.Count;
            if (add.Count == 0 && rem.Count == 0) return "ALREADY";
            byte[] o = (byte[])cls.Clone();
            foreach (int s in add) { o[s] = 0x09; o[s + 1] = 0x00; o[s + 2] = 0x00; }
            foreach (int s in rem) { o[s] = 0x00; o[s + 1] = 0x00; o[s + 2] = 0x00; }
            result = o;
            return "PATCHED";
        }
        catch { return "UNKNOWN"; }
    }

    sealed class CF
    {
        readonly byte[] B;
        int P, cpCount;
        int[] tag, a1, a2, uOff, uLen;

        public CF(byte[] b) { B = b; }
        int U1() { return B[P++]; }
        int U2() { int v = (B[P] << 8) | B[P + 1]; P += 2; return v; }
        uint U4()
        {
            uint v = ((uint)B[P] << 24) | ((uint)B[P + 1] << 16) | ((uint)B[P + 2] << 8) | B[P + 3];
            P += 4; return v;
        }

        public void ParsePool()
        {
            P = 0;
            if (U4() != 0xCAFEBABE) throw new InvalidDataException("not a class");
            P += 4;
            cpCount = U2();
            tag = new int[cpCount]; a1 = new int[cpCount]; a2 = new int[cpCount];
            uOff = new int[cpCount]; uLen = new int[cpCount];
            for (int i = 1; i < cpCount; i++)
            {
                int t = U1(); tag[i] = t;
                switch (t)
                {
                    case 1: uLen[i] = U2(); uOff[i] = P; P += uLen[i]; break;
                    case 7: case 8: case 16: case 19: case 20: a1[i] = U2(); break;
                    case 15: U1(); a1[i] = U2(); break;
                    case 9: case 10: case 11: case 12: case 17: case 18: a1[i] = U2(); a2[i] = U2(); break;
                    case 3: case 4: P += 4; break;
                    case 5: case 6: P += 8; i++; break;
                    default: throw new InvalidDataException("bad tag " + t);
                }
            }
        }

        string Utf(int i)
        {
            if (i <= 0 || i >= cpCount || tag[i] != 1) return null;
            return Encoding.UTF8.GetString(B, uOff[i], uLen[i]);
        }

        string MethodRefName(int idx)
        {
            if (idx <= 0 || idx >= cpCount) return null;
            int t = tag[idx];
            if (t != 10 && t != 11) return null;
            int nat = a2[idx];
            if (nat <= 0 || nat >= cpCount || tag[nat] != 12) return null;
            return Utf(a1[nat]);
        }

        public void ScanCode(out List<int> add, out List<int> rem)
        {
            add = new List<int>(); rem = new List<int>();
            P += 6;
            int iface = U2(); P += 2 * iface;
            SkipMembers();
            int methods = U2();
            for (int m = 0; m < methods; m++)
            {
                P += 6;
                int attrs = U2();
                for (int a = 0; a < attrs; a++)
                {
                    int an = U2();
                    uint alen = U4();
                    int aend = P + (int)alen;
                    if (Utf(an) == "Code")
                    {
                        P += 4;
                        uint clen = U4();
                        int cs = P;
                        for (int i = 0; i + 2 < (int)clen; i++)
                        {
                            byte op = B[cs + i];
                            if (op != 0xB6 && op != 0xB7 && op != 0xB8) continue;
                            int refIdx = (B[cs + i + 1] << 8) | B[cs + i + 2];
                            string nm = MethodRefName(refIdx);
                            if (nm == "addNativeDropTarget") add.Add(cs + i);
                            else if (nm == "removeNativeDropTarget") rem.Add(cs + i);
                        }
                    }
                    P = aend;
                }
            }
        }

        void SkipMembers()
        {
            int n = U2();
            for (int i = 0; i < n; i++)
            {
                P += 6;
                int ac = U2();
                for (int k = 0; k < ac; k++) { P += 2; uint len = U4(); P += (int)len; }
            }
        }
    }
}

static class JarIO
{
    public static byte[] ReadEntry(string jar, string name)
    {
        using (var fs = new FileStream(jar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            ZipArchiveEntry e = za.GetEntry(name);
            if (e == null) return null;
            using (Stream s = e.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }

    public static void WriteEntry(string jar, string name, byte[] data)
    {
        FileAttributes attr = File.GetAttributes(jar);
        if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(jar, attr & ~FileAttributes.ReadOnly);
        using (var fs = new FileStream(jar, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Update))
        {
            ZipArchiveEntry e = za.GetEntry(name);
            if (e != null) e.Delete();
            ZipArchiveEntry ne = za.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream s = ne.Open()) s.Write(data, 0, data.Length);
        }
    }
}

static class Detector
{
    public static List<string> FindRoots(string installRoot)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Action<string> add = p =>
        {
            if (string.IsNullOrEmpty(p)) return;
            p = p.TrimEnd('\\', '/');
            if (seen.Contains(p)) return;
            if (File.Exists(Path.Combine(p, @"intel_a\code\bin\jre\lib\rt.jar")))
            {
                seen.Add(p);
                roots.Add(p);
            }
        };

        if (!string.IsNullOrEmpty(installRoot))
        {
            string n = Normalize(installRoot);
            add(n != null ? n : installRoot);
            return roots;   // 指定了目录就只处理它
        }
        add(RootFromExePath(ServiceImagePath()));
        foreach (string p in ProcessPaths("DSLicSrv")) add(RootFromExePath(p));
        foreach (string p in UninstallPaths()) add(p);

        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            string dr = d.RootDirectory.FullName;
            add(Path.Combine(dr, @"Program Files (x86)\Dassault Systemes\DS License Server"));
            add(Path.Combine(dr, @"Program Files\Dassault Systemes\DS License Server"));
            add(Path.Combine(dr, @"Dassault Systemes\DS License Server"));
            try
            {
                foreach (string dir in Directory.GetDirectories(dr))
                {
                    string n = Path.GetFileName(dir).ToLowerInvariant();
                    if (n.StartsWith("program files") || n.StartsWith("programms"))
                        add(Path.Combine(dir, @"Dassault Systemes\DS License Server"));
                }
            }
            catch { }
        }

        string self = Path.GetDirectoryName(typeof(Detector).Assembly.Location);
        for (int depth = 0; depth < 4 && !string.IsNullOrEmpty(self); depth++)
        {
            add(self);
            add(Path.Combine(self, @"DS License Server"));
            try
            {
                foreach (string dir in Directory.GetDirectories(self))
                    add(Path.Combine(dir, @"intel_a"));
            }
            catch { }
            self = Path.GetDirectoryName(self);
        }

        return roots;
    }

    /// <summary>允许用户选到 bin / jre / lib / rt.jar 任意层级，向上回溯找出安装根目录。</summary>
    public static string Normalize(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            string cur = Path.GetFullPath(path.Trim().Trim('"'));
            if (File.Exists(cur)) cur = Path.GetDirectoryName(cur);
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(cur); i++)
            {
                if (File.Exists(Path.Combine(cur, @"intel_a\code\bin\jre\lib\rt.jar"))) return cur;
                cur = Path.GetDirectoryName(cur);
            }
        }
        catch { }
        return null;
    }

    static string RootFromExePath(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        exePath = exePath.Trim('"', ' ');
        int i = exePath.ToLowerInvariant().IndexOf(@"\intel_a\code\bin");
        if (i > 0) return exePath.Substring(0, i);
        return null;
    }

    static string ServiceImagePath()
    {
        foreach (string key in new[] { @"SYSTEM\CurrentControlSet\Services\DSLicSrv", @"SYSTEM\CurrentControlSet\Services\DSLS" })
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(key))
                {
                    if (k == null) continue;
                    string v = k.GetValue("ImagePath") as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch { }
        }
        return null;
    }

    static List<string> ProcessPaths(string name)
    {
        var list = new List<string>();
        try
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try { list.Add(p.MainModule.FileName); } catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return list;
    }

    static List<string> UninstallPaths()
    {
        var list = new List<string>();
        foreach (string b in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(b))
                {
                    if (k == null) continue;
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        using (RegistryKey sk = k.OpenSubKey(sub))
                        {
                            if (sk == null) continue;
                            string name = (sk.GetValue("DisplayName") as string) ?? "";
                            if (name.IndexOf("License Server", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            string loc = (sk.GetValue("InstallLocation") as string) ?? "";
                            if (loc.Length > 0) list.Add(loc);
                            string un = (sk.GetValue("UninstallString") as string) ?? "";
                            if (un.Length > 0)
                            {
                                un = un.Trim('"');
                                int sp = un.ToLowerInvariant().IndexOf(@"\intel_a\");
                                if (sp > 0) list.Add(un.Substring(0, sp));
                            }
                        }
                    }
                }
            }
            catch { }
        }
        return list;
    }
}

static class Sys
{
    public static bool IsElevated()
    {
        try
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static bool RelaunchElevated(string args)
    {
        try
        {
            var psi = new ProcessStartInfo();
            psi.FileName = typeof(Sys).Assembly.Location;
            psi.Arguments = args;
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            Process p = Process.Start(psi);
            return p != null;
        }
        catch { return false; }
    }

    public static int StopServer(string root)
    {
        int n = 0;

        // 与安装脚本一致：服务名是 "DS license Server"
        foreach (string name in new[] { "DS license Server", "DSLicSrv", "DSLS" })
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Running ||
                        sc.Status == ServiceControllerStatus.StartPending ||
                        sc.Status == ServiceControllerStatus.Paused)
                    {
                        sc.Stop();
                        try { sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); } catch { }
                        n++;
                    }
                }
            }
            catch { }
        }

        // 没有服务（便携部署）时再按进程收尾，且只动目标目录下的进程
        foreach (Process p in Process.GetProcessesByName("DSLicSrv"))
        {
            try
            {
                string path = null;
                try { path = p.MainModule.FileName; } catch { }
                bool ours = path == null || path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
                if (ours) { p.Kill(); p.WaitForExit(5000); n++; }
            }
            catch { }
            finally { p.Dispose(); }
        }
        if (n > 0) Thread.Sleep(1500);
        return n;
    }

    public static bool IsServiceInstalled()
    {
        foreach (string name in new[] { "DS license Server", "DSLicSrv", "DSLS" })
        {
            try { using (var sc = new ServiceController(name)) { var s = sc.Status; return true; } }
            catch { }
        }
        return false;
    }
    public static bool StartServer(string root)
    {
        // 有服务就用服务（与 Install_DS_AutoElevate.bat 的部署方式一致）
        foreach (string name in new[] { "DS license Server", "DSLicSrv", "DSLS" })
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Running) return true;
                    if (sc.Status == ServiceControllerStatus.Stopped || sc.Status == ServiceControllerStatus.Paused)
                    {
                        sc.Start();
                        try { sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(45)); } catch { }
                        return true;
                    }
                }
            }
            catch { }
        }

        // 便携部署：直接从 bin 启动，工作目录必须是 bin（否则找不到 RebuildLS.dll）
        string bin = Path.Combine(root, @"intel_a\code\bin");
        string exe = Path.Combine(bin, "DSLicSrv.exe");
        if (!File.Exists(exe)) return false;
        try
        {
            var psi = new ProcessStartInfo(exe, "/test -startServer");
            psi.WorkingDirectory = bin;
            psi.UseShellExecute = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process p = Process.Start(psi);
            if (p != null) { Thread.Sleep(6000); p.Dispose(); }
            return true;
        }
        catch { return false; }
    }
    public static bool LaunchAdminUI(string root)
    {
        string bin = Path.Combine(root, @"intel_a\code\bin");
        string exe = Path.Combine(bin, "DSLicSrv.exe");
        if (!File.Exists(exe)) return false;
        try
        {
            var psi = new ProcessStartInfo(exe, "/test -adminUI");
            psi.WorkingDirectory = bin;
            psi.UseShellExecute = false;
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }
}

sealed class Entry
{
    public string Root;
    public string Jar;
    public string Status;
    public int Kind;          // 1=需要修复 2=已修复 3=不适用/无法识别 0=错误
    public bool NeedsFix;
    public byte[] Patched;
    public int Add, Rem;
}

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0xF4, 0xF6, 0xF9);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(0xE3, 0xE7, 0xEE);
    public static readonly Color Text = Color.FromArgb(0x1B, 0x1F, 0x27);
    public static readonly Color Muted = Color.FromArgb(0x6B, 0x72, 0x80);
    public static readonly Color Accent = Color.FromArgb(0x25, 0x63, 0xEB);
    public static readonly Color AccentDark = Color.FromArgb(0x1D, 0x4E, 0xD8);
    public static readonly Color AccentSoft = Color.FromArgb(0xE8, 0xF0, 0xFE);
    public static readonly Color Danger = Color.FromArgb(0xB4, 0x23, 0x18);
    public static readonly Color DangerSoft = Color.FromArgb(0xFD, 0xEC, 0xEC);
    public static readonly Color Ok = Color.FromArgb(0x15, 0x80, 0x3D);
    public static readonly Color OkSoft = Color.FromArgb(0xE9, 0xF7, 0xEF);
    public static readonly Color Warn = Color.FromArgb(0xB4, 0x53, 0x09);
    public static readonly Color WarnSoft = Color.FromArgb(0xFE, 0xF3, 0xE2);

    public static Font Ui(float size) { return new Font("Microsoft YaHei UI", size, FontStyle.Regular); }
    public static Font UiBold(float size) { return new Font("Microsoft YaHei UI", size, FontStyle.Bold); }
    public static Font Mono(float size) { return new Font("Consolas", size, FontStyle.Regular); }

    public static System.Drawing.Drawing2D.GraphicsPath Round(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

sealed class FlatButton : Control
{
    public bool Primary;
    bool hover, press;

    public FlatButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Font = Theme.Ui(9.5F);
        Cursor = Cursors.Hand;
        TabStop = true;
        Height = 34;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; press = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { press = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { press = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        Color fill, fore, border;
        if (Primary)
        {
            fill = !Enabled ? Color.FromArgb(0xC7, 0xD6, 0xF5) : (press ? Theme.AccentDark : (hover ? Theme.AccentDark : Theme.Accent));
            fore = Color.White;
            border = Color.Empty;
        }
        else
        {
            fill = !Enabled ? Color.FromArgb(0xF7, 0xF8, 0xFA)
                  : (press ? Color.FromArgb(0xE9, 0xEF, 0xFB) : (hover ? Color.FromArgb(0xF2, 0xF6, 0xFD) : Theme.Card));
            fore = Enabled ? Theme.Text : Color.FromArgb(0xBC, 0xC3, 0xCE);
            border = Theme.Border;
        }

        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Round(r, 7))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (border != Color.Empty) using (var p = new Pen(border)) g.DrawPath(p, path);
            if (Focused && Enabled)
                using (var p = new Pen(Primary ? Color.FromArgb(0x93, 0xB4, 0xF8) : Color.FromArgb(0xC7, 0xD6, 0xF5), 2))
                    g.DrawPath(p, path);
        }

        TextRenderer.DrawText(g, Text, Font, r, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

sealed class CardPanel : Panel
{
    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Round(r, 10))
        {
            using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
            using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
        }
    }
}

sealed class FlatList : ListView
{
    public FlatList()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        DoubleBuffered = true;
    }
}

sealed class MainForm : Form
{
    readonly string installRootArg;
    readonly FlatList lv = new FlatList();
    readonly RichTextBox log = new RichTextBox();
    readonly FlatButton btnScan = new FlatButton();
    readonly FlatButton btnAdd = new FlatButton();
    readonly FlatButton btnFix = new FlatButton();
    readonly FlatButton btnRestore = new FlatButton();
    readonly FlatButton btnOpenUi = new FlatButton();
    readonly CheckBox chkRestart = new CheckBox();
    readonly Label lblCount = new Label();
    readonly Label lblSvc = new Label();
    readonly Label lblEmpty = new Label();
    readonly Panel pnlNote = new Panel();
    readonly List<Entry> entries = new List<Entry>();

    public MainForm(string installRoot)
    {
        installRootArg = installRoot;
        Text = "DS License Server 管理界面修复";
        ClientSize = new Size(920, 680);
        MinimumSize = new Size(840, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        Font = Theme.Ui(9F);
        Icon = MakeIcon();
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;

        // ---------- 页头 ----------
        var header = new Panel();
        header.Dock = DockStyle.Top;
        header.Height = 92;
        header.Paint += (s, e) =>
        {
            var g = e.Graphics;
            using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(1, header.Width), header.Height),
                Color.FromArgb(0x1E, 0x3A, 0x8A), Color.FromArgb(0x2E, 0x6B, 0xE6), 0F))
                g.FillRectangle(b, header.ClientRectangle);
            using (var b = new SolidBrush(Color.FromArgb(0x30, 0, 0, 0)))
                g.FillRectangle(b, new Rectangle(0, header.Height - 3, header.Width, 3));
        };

        var lblTitle = new Label();
        lblTitle.Text = "DS License Server 管理界面修复";
        lblTitle.Font = Theme.UiBold(15F);
        lblTitle.ForeColor = Color.White;
        lblTitle.BackColor = Color.Transparent;
        lblTitle.AutoSize = false;
        lblTitle.SetBounds(22, 16, 560, 30);

        var lblSub = new Label();
        lblSub.Text = "修补安装目录自带 JRE 的 rt.jar，让 DSLicSrv.exe /test -adminUI 在新系统上不再崩溃";
        lblSub.Font = Theme.Ui(9F);
        lblSub.ForeColor = Color.FromArgb(0xDC, 0xE7, 0xFB);
        lblSub.BackColor = Color.Transparent;
        lblSub.AutoSize = false;
        lblSub.SetBounds(24, 48, 700, 20);

        var lblMeta = new Label();
        lblMeta.Text = "补丁：WComponentPeer 的 OLE 拖放注册调用 · 6 字节 · 不改 class 结构 · 自动备份 rt.jar.orig";
        lblMeta.Font = Theme.Ui(8.5F);
        lblMeta.ForeColor = Color.FromArgb(0xB8, 0xCB, 0xF2);
        lblMeta.BackColor = Color.Transparent;
        lblMeta.AutoSize = false;
        lblMeta.SetBounds(24, 68, 760, 18);

        header.Controls.Add(lblTitle);
        header.Controls.Add(lblSub);
        header.Controls.Add(lblMeta);

        // ---------- 非管理员提示条 ----------
        pnlNote.Dock = DockStyle.Top;
        pnlNote.Height = 32;
        pnlNote.Paint += (s, e) =>
        {
            using (var b = new SolidBrush(Theme.WarnSoft)) e.Graphics.FillRectangle(b, pnlNote.ClientRectangle);
            using (var p = new Pen(Color.FromArgb(0xF3, 0xD5, 0xAA))) e.Graphics.DrawLine(p, 0, pnlNote.Height - 1, pnlNote.Width, pnlNote.Height - 1);
        };
        var lblNote = new Label();
        lblNote.Text = "非管理员运行：安装目录在 Program Files 下时，点「开始修复」会请求以管理员身份重启。";
        lblNote.ForeColor = Theme.Warn;
        lblNote.BackColor = Color.Transparent;
        lblNote.Dock = DockStyle.Fill;
        lblNote.TextAlign = ContentAlignment.MiddleLeft;
        lblNote.Padding = new Padding(22, 0, 0, 0);
        pnlNote.Controls.Add(lblNote);

        // ---------- 主体 ----------
        var body = new Panel();
        body.Dock = DockStyle.Fill;
        body.BackColor = Theme.Bg;
        body.Padding = new Padding(18, 14, 18, 12);

        var cardList = new CardPanel();
        cardList.Dock = DockStyle.Fill;
        cardList.Padding = new Padding(1, 1, 1, 1);

        var listHead = new Panel();
        listHead.Dock = DockStyle.Top;
        listHead.Height = 46;
        listHead.BackColor = Theme.Card;

        var lblListTitle = new Label();
        lblListTitle.Text = "检测到的安装";
        lblListTitle.Font = Theme.UiBold(10F);
        lblListTitle.ForeColor = Theme.Text;
        lblListTitle.AutoSize = true;
        lblListTitle.Location = new Point(16, 16);
        listHead.Controls.Add(lblListTitle);

        var lblListHint = new Label();
        lblListHint.Text = "双击打开目录 · 可直接拖入安装目录";
        lblListHint.Font = Theme.Ui(8.5F);
        lblListHint.ForeColor = Theme.Muted;
        lblListHint.AutoSize = true;
        lblListHint.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        listHead.Controls.Add(lblListHint);
        listHead.Resize += (s, e) =>
        {
            lblListHint.Left = listHead.ClientSize.Width - lblListHint.Width - 16;
            lblListHint.Top = 19;
        };

        lv.View = View.Details;
        lv.FullRowSelect = true;
        lv.GridLines = false;
        lv.HideSelection = false;
        lv.BorderStyle = BorderStyle.None;
        lv.BackColor = Theme.Card;
        lv.Dock = DockStyle.Fill;
        lv.OwnerDraw = true;
        lv.Font = Theme.Ui(9F);
        lv.Columns.Add("路径", 620);
        lv.Columns.Add("状态", 220);
        var il = new ImageList();
        il.ImageSize = new Size(1, 26);
        lv.SmallImageList = il;
        lv.DrawColumnHeader += LvDrawHeader;
        lv.DrawItem += (s, e) => { e.DrawDefault = false; };
        lv.DrawSubItem += LvDrawSubItem;
        lv.SelectedIndexChanged += (s, e) => UpdateButtons();
        lv.DoubleClick += (s, e) => OpenFolder();
        lv.Resize += (s, e) =>
        {
            if (lv.Columns.Count == 2)
            {
                int w = lv.ClientSize.Width;
                lv.Columns[0].Width = Math.Max(300, w - 240);
                lv.Columns[1].Width = 240;
            }
        };

        var listHost = new Panel();
        listHost.Dock = DockStyle.Fill;
        listHost.Padding = new Padding(10, 0, 10, 8);
        listHost.BackColor = Theme.Card;
        listHost.Controls.Add(lv);

        lblEmpty.Text = "没有找到安装目录。\r\n点「手动指定路径…」选择，或把 DS License Server 文件夹直接拖进来。";
        lblEmpty.Dock = DockStyle.Fill;
        lblEmpty.TextAlign = ContentAlignment.MiddleCenter;
        lblEmpty.ForeColor = Theme.Muted;
        lblEmpty.Font = Theme.Ui(9.5F);
        lblEmpty.BackColor = Theme.Card;
        lblEmpty.Visible = false;
        listHost.Controls.Add(lblEmpty);
        lblEmpty.BringToFront();

        cardList.Controls.Add(listHost);
        cardList.Controls.Add(listHead);

        // ---------- 按钮行 ----------
        var btnRow = new Panel();
        btnRow.Dock = DockStyle.Top;
        btnRow.Height = 60;
        btnRow.BackColor = Theme.Bg;

        btnScan.Text = "重新扫描";
        btnScan.SetBounds(0, 14, 100, 34);
        btnScan.Click += (s, e) => Scan();

        btnAdd.Text = "手动指定路径…";
        btnAdd.SetBounds(108, 14, 132, 34);
        btnAdd.Click += (s, e) => BrowseRoot();

        btnFix.Text = "开始修复";
        btnFix.Primary = true;
        btnFix.SetBounds(248, 14, 112, 34);
        btnFix.Click += (s, e) => DoFix();

        btnRestore.Text = "恢复原版";
        btnRestore.SetBounds(368, 14, 100, 34);
        btnRestore.Click += (s, e) => DoRestore();

        btnOpenUi.Text = "打开管理界面";
        btnOpenUi.SetBounds(476, 14, 132, 34);
        btnOpenUi.Click += (s, e) => DoOpenUi();

        chkRestart.Text = "修复后重启许可证服务";
        chkRestart.Font = Theme.Ui(9F);
        chkRestart.ForeColor = Theme.Muted;
        chkRestart.Checked = true;
        chkRestart.AutoSize = true;
        chkRestart.Location = new Point(492, 23);
        chkRestart.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnRow.Resize += (s, e) =>
        {
            chkRestart.Left = btnRow.ClientSize.Width - chkRestart.Width - 2;
        };

        btnRow.Controls.Add(btnScan);
        btnRow.Controls.Add(btnAdd);
        btnRow.Controls.Add(btnFix);
        btnRow.Controls.Add(btnRestore);
        btnRow.Controls.Add(btnOpenUi);
        btnRow.Controls.Add(chkRestart);

        // ---------- 日志卡片 ----------
        var cardLog = new CardPanel();
        cardLog.Dock = DockStyle.Bottom;
        cardLog.Height = 196;
        cardLog.Padding = new Padding(1, 1, 1, 1);

        var lblLogTitle = new Label();
        lblLogTitle.Text = "日志";
        lblLogTitle.Font = Theme.UiBold(10F);
        lblLogTitle.ForeColor = Theme.Text;
        lblLogTitle.Dock = DockStyle.Top;
        lblLogTitle.Height = 34;
        lblLogTitle.TextAlign = ContentAlignment.MiddleLeft;
        lblLogTitle.Padding = new Padding(16, 0, 0, 0);

        log.Dock = DockStyle.Fill;
        log.BorderStyle = BorderStyle.None;
        log.ReadOnly = true;
        log.BackColor = Theme.Card;
        log.ForeColor = Theme.Text;
        log.Font = Theme.Mono(9.5F);
        log.WordWrap = true;
        log.ScrollBars = RichTextBoxScrollBars.Vertical;
        log.DetectUrls = false;

        var logHost = new Panel();
        logHost.Dock = DockStyle.Fill;
        logHost.Padding = new Padding(12, 0, 12, 10);
        logHost.BackColor = Theme.Card;
        logHost.Controls.Add(log);

        cardLog.Controls.Add(logHost);
        cardLog.Controls.Add(lblLogTitle);

        var spacer = new Panel();
        spacer.Dock = DockStyle.Bottom;
        spacer.Height = 12;
        spacer.BackColor = Theme.Bg;

        body.Controls.Add(cardList);
        body.Controls.Add(cardLog);
        body.Controls.Add(spacer);
        body.Controls.Add(btnRow);

        // ---------- 状态栏 ----------
        var status = new Panel();
        status.Dock = DockStyle.Bottom;
        status.Height = 34;
        status.Paint += (s, e) =>
        {
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, 0, status.Width, 0);
        };

        lblCount.Text = "共 0 个安装";
        lblCount.ForeColor = Theme.Muted;
        lblCount.AutoSize = true;
        lblCount.Location = new Point(20, 9);

        lblSvc.Text = "许可证服务：检测中…";
        lblSvc.ForeColor = Theme.Muted;
        lblSvc.AutoSize = true;
        lblSvc.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        status.Resize += (s, e) => { lblSvc.Left = status.ClientSize.Width - lblSvc.Width - 20; };

        status.Controls.Add(lblCount);
        status.Controls.Add(lblSvc);

        Controls.Add(body);
        Controls.Add(status);
        Controls.Add(pnlNote);
        Controls.Add(header);

        AllowDrop = true;
        DragEnter += (s, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (s, e) =>
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return;
            foreach (string f in files) AddManualRoot(f, true);
        };

        Load += (s, e) =>
        {
            pnlNote.Visible = !Sys.IsElevated();
            Scan();
            UpdateButtons();
        };
    }

    static Icon MakeIcon()
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = Theme.Round(new Rectangle(0, 0, 31, 31), 7))
                using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, 32, 32), Color.FromArgb(0x2E, 0x6B, 0xE6), Color.FromArgb(0x1E, 0x3A, 0x8A), 55F))
                    g.FillPath(b, path);
                using (var p = new Pen(Color.White, 3F))
                {
                    p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    p.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    g.DrawLines(p, new[] { new PointF(9F, 16.5F), new PointF(14F, 21.5F), new PointF(23F, 10.5F) });
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    void OpenFolder()
    {
        Entry e = Selected();
        if (e == null) return;
        try { System.Diagnostics.Process.Start("explorer.exe", "\"" + e.Root + "\""); }
        catch (Exception ex) { Log("打开目录失败：" + ex.Message); }
    }

    // ------------------------------------------------------------------ 列表绘制
    void LvDrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, e.Bounds);
        using (var p = new Pen(Theme.Border)) g.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        TextRenderer.DrawText(g, e.Header.Text, Theme.UiBold(8.5F),
            new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height),
            Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    void LvDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
    {
        var g = e.Graphics;
        var en = e.Item.Tag as Entry;
        bool sel = e.Item.Selected;
        Color bg = sel ? Theme.AccentSoft : (e.ItemIndex % 2 == 1 ? Color.FromArgb(0xFA, 0xFB, 0xFD) : Theme.Card);
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);
        if (sel)
        {
            using (var b = new SolidBrush(Theme.Accent))
                g.FillRectangle(b, new Rectangle(e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height));
        }

        if (e.ColumnIndex == 0)
        {
            TextRenderer.DrawText(g, e.SubItem.Text, Theme.Ui(9F),
                new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 20, e.Bounds.Height),
                Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);
            return;
        }

        // 状态胶囊
        int kind = en == null ? 3 : en.Kind;
        Color fg, soft;
        switch (kind)
        {
            case 1: fg = Theme.Danger; soft = Theme.DangerSoft; break;
            case 2: fg = Theme.Ok; soft = Theme.OkSoft; break;
            case 3: fg = Theme.Warn; soft = Theme.WarnSoft; break;
            default: fg = Theme.Muted; soft = Color.FromArgb(0xF1, 0xF3, 0xF6); break;
        }
        string text = e.SubItem.Text;
        Size tsz = TextRenderer.MeasureText(g, text, Theme.Ui(8.5F), new Size(10000, 20), TextFormatFlags.SingleLine);
        int w = Math.Min(tsz.Width + 20, e.Bounds.Width - 18);
        int h = 22;
        var pill = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + (e.Bounds.Height - h) / 2, w, h);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Theme.Round(pill, 11))
        using (var b = new SolidBrush(soft)) g.FillPath(b, path);
        using (var b = new SolidBrush(fg))
            g.FillEllipse(b, pill.X + 7, pill.Y + h / 2 - 3, 6, 6);
        TextRenderer.DrawText(g, text, Theme.Ui(8.5F),
            new Rectangle(pill.X + 16, pill.Y, pill.Width - 20, pill.Height), fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    // ------------------------------------------------------------------ 日志
    void Append(string s, Color c)
    {
        log.SelectionStart = log.TextLength;
        log.SelectionLength = 0;
        log.SelectionColor = c;
        log.AppendText(s + "\r\n");
    }

    void Log(string s)
    {
        Color c = Theme.Text;
        if (s.IndexOf("失败") >= 0) c = Theme.Danger;
        else if (s.IndexOf("OK：") >= 0 || s.IndexOf("已恢复") >= 0 || s.IndexOf("已启动") >= 0 || s.IndexOf("已重启") >= 0 || s.IndexOf("已打补丁") >= 0) c = Theme.Ok;
        else if (s.StartsWith("  ")) c = Theme.Muted;
        Append("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s, c);
        log.ScrollToCaret();
    }

    // ------------------------------------------------------------------ 扫描 / 修复
    void Scan()
    {
        lv.Items.Clear();
        entries.Clear();
        btnFix.Enabled = false;
        btnRestore.Enabled = false;
        if (log.TextLength > 0) Append("", Theme.Text);

        List<string> roots = Detector.FindRoots(installRootArg);
        if (roots.Count == 0)
        {
            Log("没找到 DS License Server 安装目录（需要 intel_a\\code\\bin\\jre\\lib\\rt.jar）。");
            Log("请确认已安装 DS License Server；装到非标准位置时可点「手动指定路径…」或用 -InstallRoot \"路径\" 指定。");
            UpdateEmpty();
            UpdateStatus();
            UpdateButtons();
            return;
        }

        foreach (string root in roots)
        {
            string nr = NormalizeRoot(root);
            Entry en = Evaluate(nr != null ? nr : root);
            var it = new ListViewItem(en.Root);
            it.SubItems.Add(en.Status);
            it.Tag = en;
            lv.Items.Add(it);
            entries.Add(en);
        }

        Log(string.Format("扫描完成，找到 {0} 个安装。", roots.Count));
        foreach (Entry e in entries) Log("  " + e.Root + "  ->  " + e.Status);
        if (lv.Items.Count > 0) lv.Items[0].Selected = true;
        UpdateEmpty();
        UpdateStatus();
        UpdateButtons();
    }

    Entry Evaluate(string root)
    {
        string jar = Path.Combine(root, @"intel_a\code\bin\jre\lib\rt.jar");
        var en = new Entry { Root = root, Jar = jar };
        try
        {
            byte[] cls = JarIO.ReadEntry(jar, Patcher.EntryName);
            if (cls == null)
            {
                en.Kind = 3;
                en.Status = "不适用（非 Java 8 rt.jar）";
            }
            else
            {
                int a, r; byte[] p;
                string st = Patcher.Patch(cls, out p, out a, out r);
                if (st == "PATCHED")
                {
                    en.NeedsFix = true; en.Patched = p; en.Add = a; en.Rem = r; en.Kind = 1;
                    en.Status = string.Format("需要修复 · {0} 处调用", a + r);
                }
                else if (st == "ALREADY") { en.Kind = 2; en.Status = "已修复"; }
                else { en.Kind = 0; en.Status = "无法识别"; }
            }
        }
        catch (Exception ex)
        {
            en.Kind = 0;
            en.Status = "读取失败：" + ex.Message;
        }
        return en;
    }

    /// <summary>允许用户选到 bin / jre / lib / rt.jar 任意层级，向上回溯找出安装根目录。</summary>
    static string NormalizeRoot(string path)
    {
        return Detector.Normalize(path);
    }

    void BrowseRoot()
    {
        using (var dlg = new FolderBrowserDialog())
        {
            dlg.Description = "选择 DS License Server 安装根目录（其下应有 intel_a\\code\\bin\\jre\\lib\\rt.jar）";
            dlg.ShowNewFolderButton = false;
            Entry cur = Selected();
            if (cur != null) dlg.SelectedPath = cur.Root;
            else
            {
                string guess = @"C:\Program Files (x86)\Dassault Systemes\DS License Server";
                if (Directory.Exists(guess)) dlg.SelectedPath = guess;
            }
            if (dlg.ShowDialog(this) == DialogResult.OK) AddManualRoot(dlg.SelectedPath, false);
        }
    }

    void AddManualRoot(string path, bool fromDrop)
    {
        string root = NormalizeRoot(path);
        if (root == null)
        {
            MessageBox.Show(this,
                "这个位置里没有 intel_a\\code\\bin\\jre\\lib\\rt.jar，看起来不是 DS License Server 的安装根目录。\r\n\r\n" +
                "请选择包含 intel_a 的那一层，通常形如：\r\nC:\\Program Files (x86)\\Dassault Systemes\\DS License Server",
                "路径无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Log("手动指定失败：不是 DS License Server 安装目录 -> " + path);
            return;
        }

        foreach (Entry e in entries)
        {
            if (string.Equals(e.Root, root, StringComparison.OrdinalIgnoreCase))
            {
                SelectRoot(root);
                Log("该目录已在列表中：" + root);
                return;
            }
        }

        Entry en = Evaluate(root);
        var it = new ListViewItem(en.Root);
        it.SubItems.Add(en.Status);
        it.Tag = en;
        lv.Items.Add(it);
        entries.Add(en);
        it.Selected = true;
        it.EnsureVisible();
        Log((fromDrop ? "已从拖入的路径添加：" : "已手动指定：") + root + "  ->  " + en.Status);
        UpdateEmpty();
        UpdateStatus();
        UpdateButtons();
    }

    void SelectRoot(string root)
    {
        foreach (ListViewItem it in lv.Items)
        {
            var e = it.Tag as Entry;
            if (e != null && string.Equals(e.Root, root, StringComparison.OrdinalIgnoreCase))
            {
                it.Selected = true;
                it.Focused = true;
                it.EnsureVisible();
                return;
            }
        }
    }

    void UpdateEmpty()
    {
        lblEmpty.Visible = lv.Items.Count == 0;
    }

    Entry Selected()
    {
        if (lv.SelectedItems.Count == 0) return null;
        return lv.SelectedItems[0].Tag as Entry;
    }

    void UpdateButtons()
    {
        Entry e = Selected();
        btnFix.Enabled = e != null && e.NeedsFix;
        btnRestore.Enabled = e != null && e.Kind == 2;
    }

    void UpdateStatus()
    {
        int need = 0, done = 0;
        foreach (Entry e in entries) { if (e.NeedsFix) need++; else if (e.Kind == 2) done++; }
        lblCount.Text = string.Format("共 {0} 个安装 · 需要修复 {1} · 已修复 {2}", entries.Count, need, done);

        string s; Color c;
        try
        {
            using (var sc = new ServiceController("DS license Server"))
            {
                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Running) { s = "许可证服务：运行中"; c = Theme.Ok; }
                else if (sc.Status == ServiceControllerStatus.Stopped) { s = "许可证服务：已停止"; c = Theme.Danger; }
                else { s = "许可证服务：" + sc.Status; c = Theme.Warn; }
            }
        }
        catch { s = "许可证服务：未安装"; c = Theme.Muted; }
        lblSvc.Text = s;
        lblSvc.ForeColor = c;
        lblSvc.Left = Math.Max(0, lblSvc.Parent.ClientSize.Width - lblSvc.Width - 20);
    }

    void DoFix()
    {
        List<Entry> todo = entries.FindAll(x => x.NeedsFix);
        if (todo.Count == 0) return;

        bool permissionDenied = false;
        foreach (Entry e in todo)
        {
            Log("修复中：" + e.Root);
            try
            {
                Sys.StopServer(e.Root);
                string bak = Path.Combine(Path.GetDirectoryName(e.Jar), "rt.jar.orig");
                if (!File.Exists(bak))
                {
                    File.Copy(e.Jar, bak, false);
                    Log("  已备份原版 -> rt.jar.orig");
                }
                else Log("  已存在备份，保留不动：rt.jar.orig");

                JarIO.WriteEntry(e.Jar, Patcher.EntryName, e.Patched);

                byte[] chk = JarIO.ReadEntry(e.Jar, Patcher.EntryName);
                int a2, r2; byte[] p2;
                if (Patcher.Patch(chk, out p2, out a2, out r2) != "ALREADY")
                    throw new InvalidOperationException("写回后仍能解析到可补丁的调用，补丁未生效");

                Log(string.Format("  OK：已打补丁 {0} 处（add={1}, remove={2}），rt.jar = {3:N0} 字节，校验通过。",
                    e.Add + e.Rem, e.Add, e.Rem, new FileInfo(e.Jar).Length));
                e.NeedsFix = false;
                e.Kind = 2;
                e.Status = "已修复";
            }
            catch (Exception ex)
            {
                Log("  失败：" + ex.Message);
                if (ex is UnauthorizedAccessException) permissionDenied = true;
                else Log("  提示：请先关闭管理界面、停止许可证服务；文件被占用时可结束 DSLicSrv 进程后重试。");
            }
        }

        if (chkRestart.Checked)
        {
            foreach (Entry e in todo)
            {
                if (e.Kind == 2)
                {
                    bool ok = Sys.StartServer(e.Root);
                    Log(ok ? "  已启动许可证服务：" + e.Root : "  未能自动启动服务，请手动执行 DSLicSrv.exe /test -startServer");
                }
            }
        }

        RefreshList();

        if (permissionDenied)
        {
            DialogResult r = MessageBox.Show(this,
                "没有权限修改安装目录里的文件。\r\n是否以管理员身份重新启动本程序继续？",
                "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes)
            {
                string args = string.IsNullOrEmpty(installRootArg) ? "" : "-InstallRoot \"" + installRootArg + "\"";
                if (Sys.RelaunchElevated(args)) { Close(); return; }
                MessageBox.Show(this, "提权未成功（可能取消了 UAC）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    void DoRestore()
    {
        Entry e = Selected();
        if (e == null) return;
        string bak = Path.Combine(Path.GetDirectoryName(e.Jar), "rt.jar.orig");
        if (!File.Exists(bak))
        {
            MessageBox.Show(this, "没有找到备份文件 rt.jar.orig，无法恢复。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this, "确定把 rt.jar 恢复成原版吗？\r\n" + e.Root,
            "恢复原版", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        try
        {
            Sys.StopServer(e.Root);
            File.Copy(bak, e.Jar, true);
            Log("已恢复原版：" + e.Root);
        }
        catch (Exception ex)
        {
            Log("恢复失败：" + ex.Message);
        }
        RefreshList();
    }

    void DoOpenUi()
    {
        Entry e = Selected();
        if (e == null) return;
        if (Sys.LaunchAdminUI(e.Root)) Log("已启动管理界面（DSLicSrv.exe /test -adminUI）。若弹「连接失败」，请先启动许可证服务。");
        else Log("启动管理界面失败，请检查：" + e.Root);
    }

    void RefreshList()
    {
        lv.Items.Clear();
        foreach (Entry en in entries)
        {
            var it = new ListViewItem(en.Root);
            it.SubItems.Add(en.Status);
            it.Tag = en;
            lv.Items.Add(it);
        }
        if (lv.Items.Count > 0) lv.Items[0].Selected = true;
        UpdateEmpty();
        UpdateStatus();
        UpdateButtons();
    }
}
static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static int Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }   // 125%/150% 缩放下保持文字清晰
        string installRoot = null;
        bool silent = false, noElevate = false, restart = false, dryRun = false, restore = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            if ((a == "-installroot" || a == "/installroot" || a == "-path") && i + 1 < args.Length) installRoot = args[++i];
            else if (a == "-silent") silent = true;
            else if (a == "-noelevate") noElevate = true;
            else if (a == "-restart") restart = true;
            else if (a == "-restore") restore = true;
            else if (a == "-dryrun") dryRun = true;
        }

        if (silent) return Headless(installRoot, noElevate, restart, dryRun, restore);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(installRoot));
        return 0;
    }

    // 无界面批量模式：DSLS_GuiFix.exe -Silent [-InstallRoot path] [-Restart]
    static int Headless(string installRoot, bool noElevate, bool restart, bool dryRun, bool restore)
    {
        string logPath = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location), "DSLS_GuiFix.log");
        var sb = new StringBuilder();
        Action<string> w = s => { sb.AppendLine(s); };

        w("=== DS License Server 管理界面修复（静默模式）===");
        if (!noElevate && !Sys.IsElevated())
        {
            if (Sys.RelaunchElevated(string.Join(" ", argsOf(installRoot, restart, restore))))
            {
                return 0;
            }
        }

        List<string> roots = Detector.FindRoots(installRoot);
        int patched = 0, failed = 0;
        if (roots.Count == 0)
        {
            w("没找到安装目录。");
            try { File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8); } catch { }
            return 2;
        }

        foreach (string root in roots)
        {
            string jar = Path.Combine(root, @"intel_a\code\bin\jre\lib\rt.jar");
            byte[] cls;
            try { cls = JarIO.ReadEntry(jar, Patcher.EntryName); }
            catch (Exception ex) { w("读取失败 " + root + " : " + ex.Message); continue; }
            if (cls == null) { w("跳过（非 Java 8 rt.jar）：" + root); continue; }

            if (restore)
            {
                string bak0 = Path.Combine(Path.GetDirectoryName(jar), "rt.jar.orig");
                if (!File.Exists(bak0)) { w("没有备份，无法恢复：" + root); failed++; continue; }
                if (dryRun) { w("可恢复原版：" + root); continue; }
                try
                {
                    Sys.StopServer(root);
                    File.Copy(bak0, jar, true);
                    w("已恢复原版：" + root + "  (rt.jar = " + new FileInfo(jar).Length.ToString("N0") + " 字节)");
                    patched++;
                    if (restart) { Sys.StartServer(root); w("  已重启服务"); }
                }
                catch (Exception ex) { w("恢复失败：" + root + " : " + ex.Message); failed++; }
                continue;
            }

            if (restore)
            {
                string bak0 = Path.Combine(Path.GetDirectoryName(jar), "rt.jar.orig");
                if (!File.Exists(bak0)) { w("没有备份，无法恢复：" + root); failed++; continue; }
                if (dryRun) { w("可恢复原版：" + root); continue; }
                try
                {
                    Sys.StopServer(root);
                    File.Copy(bak0, jar, true);
                    w("已恢复原版：" + root + "  (rt.jar = " + (new FileInfo(jar).Length).ToString("N0") + " 字节)");
                    patched++;
                    if (restart) { Sys.StartServer(root); w("  已重启服务"); }
                }
                catch (Exception ex) { w("恢复失败：" + root + " : " + ex.Message); failed++; }
                continue;
            }
            int a, r; byte[] p;
            string st = Patcher.Patch(cls, out p, out a, out r);
            if (st != "PATCHED")
            {
                w("已是补丁状态：" + root);
                if (restart && !dryRun)
                {
                    Sys.StopServer(root);
                    w(Sys.StartServer(root) ? "  已重启服务" : "  重启服务失败，请手动执行 DSLicSrv.exe /test -startServer");
                }
                continue;
            }
            if (dryRun) { w("需要修复：" + root + " (add=" + a + ", remove=" + r + ")"); continue; }

            try
            {
                Sys.StopServer(root);
                string bak = Path.Combine(Path.GetDirectoryName(jar), "rt.jar.orig");
                if (!File.Exists(bak)) File.Copy(jar, bak, false);
                JarIO.WriteEntry(jar, Patcher.EntryName, p);
                byte[] chk = JarIO.ReadEntry(jar, Patcher.EntryName);
                int a2, r2; byte[] p2;
                if (Patcher.Patch(chk, out p2, out a2, out r2) != "ALREADY") throw new InvalidOperationException("校验失败");
                w("OK：" + root + "  (add=" + a + ", remove=" + r + ")");
                patched++;
                if (restart) { Sys.StartServer(root); w("  已重启服务"); }
            }
            catch (Exception ex)
            {
                w("失败：" + root + " : " + ex.Message);
                failed++;
            }
        }

        w("完成：修复 " + patched + " 个，失败 " + failed + " 个。");
        try { File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8); } catch { }
        return failed > 0 ? 4 : 0;
    }

    static List<string> argsOf(string installRoot, bool restart, bool restore=false)
    {
        var l = new List<string> { "-Silent", "-NoElevate" };
        if (!string.IsNullOrEmpty(installRoot)) { l.Add("-InstallRoot"); l.Add("\"" + installRoot + "\""); }
        if (restore) l.Add("-Restore");   // 路径含空格，提权重启必须带引号
        if (restart) l.Add("-Restart");
        return l;
    }
}
