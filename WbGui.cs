// gen181 WindBot 壳 —— GUI 版
// 字段默认值：回环 127.0.0.1:7911（ai_duel --server 默认端口）/密码 s/名字 gen181。
// 卡组与模型下拉框自动扫描运行目录（*.ydk 递归一层 Decks/；*.onnx 本目录）。
// 帧协议与网络线协议逐字节相同，TCP <-> 决策核 stdin/stdout 纯字节转发。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

class WbForm : Form
{
    TextBox txtHost, txtPort, txtPass, txtName;
    ComboBox cmbDeck, cmbModel;
    Button btnGo;
    TextBox txtLog;
    Process core;
    TcpClient tcp;
    volatile bool running;

    const string CdbUrl = "https://cdn02.moecube.com:444/ygopro-database/zh-CN/cards.cdb";

    void DownloadCdb()
    {
        var t = new Thread(() =>
        {
            try
            {
                Log("下载卡库 cards.cdb（约 8MB，来自 moecube 官方 CDN）...");
                System.Net.ServicePointManager.SecurityProtocol =
                    (System.Net.SecurityProtocolType)3072; // TLS 1.2
                var tmp = Path.GetTempFileName();
                new System.Net.WebClient().DownloadFile(CdbUrl, tmp);
                var fi = new FileInfo(tmp);
                if (fi.Length < 1000000) { Log("下载异常（" + fi.Length + " 字节），已丢弃"); return; }
                if (File.Exists("cards.cdb")) File.Delete("cards.cdb");
                File.Move(tmp, "cards.cdb");
                Log("卡库就绪：" + fi.Length + " 字节");
            }
            catch (Exception ex) { Log("卡库下载失败: " + ex.Message + "（可重开程序重试）"); }
        });
        t.IsBackground = true; t.Start();
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new WbForm());
    }

    void ScanFiles(string dir, string pattern, ComboBox cmb, bool recursive)
    {
        try
        {
            var files = Directory.GetFiles(dir, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            foreach (var f in files.OrderBy(f => f))
                cmb.Items.Add(Path.GetFileName(Path.GetDirectoryName(f)) == "Decks" ? "Decks\\" + Path.GetFileName(f) : Path.GetFileName(f));
        }
        catch { }
    }

    void FindAndSelect(ComboBox cmb, params string[] prefers)
    {
        foreach (var p in prefers)
            for (int i = 0; i < cmb.Items.Count; i++)
                if (((string)cmb.Items[i]).IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                { cmb.SelectedIndex = i; return; }
        if (cmb.Items.Count > 0) cmb.SelectedIndex = 0;
    }

    WbForm()
    {
        Text = "gen181 WindBot";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(430, 470);

        int y = 12;
        Controls.Add(new Label { Text = "服务器 IP", Location = new Point(12, y + 3), AutoSize = true });
        txtHost = new TextBox { Location = new Point(120, y), Width = 130, Text = "127.0.0.1" };
        Controls.Add(txtHost);
        Controls.Add(new Label { Text = "端口", Location = new Point(258, y + 3), AutoSize = true });
        txtPort = new TextBox { Location = new Point(295, y), Width = 60, Text = "7911" };
        Controls.Add(txtPort);
        y += 32;
        Controls.Add(new Label { Text = "房间密码", Location = new Point(12, y + 3), AutoSize = true });
        txtPass = new TextBox { Location = new Point(120, y), Width = 130, Text = "s" };
        Controls.Add(txtPass);
        y += 32;
        Controls.Add(new Label { Text = "名字", Location = new Point(12, y + 3), AutoSize = true });
        txtName = new TextBox { Location = new Point(120, y), Width = 130, Text = "gen181" };
        Controls.Add(txtName);
        y += 32;
        Controls.Add(new Label { Text = "卡组", Location = new Point(12, y + 3), AutoSize = true });
        cmbDeck = new ComboBox { Location = new Point(120, y), Width = 235, DropDownStyle = ComboBoxStyle.DropDownList };
        ScanFiles(".", "*.ydk", cmbDeck, false);
        ScanFiles(Path.Combine(".", "Decks"), "*.ydk", cmbDeck, false);
        FindAndSelect(cmbDeck, "ChaosRitual");
        Controls.Add(cmbDeck);
        y += 34;
        Controls.Add(new Label { Text = "模型", Location = new Point(12, y + 3), AutoSize = true });
        cmbModel = new ComboBox { Location = new Point(120, y), Width = 235, DropDownStyle = ComboBoxStyle.DropDownList };
        ScanFiles(".", "model*.onnx", cmbModel, false);
        FindAndSelect(cmbModel, "model.onnx");
        Controls.Add(cmbModel);
        y += 40;
        btnGo = new Button { Text = "连接并开始", Location = new Point(120, y), Width = 120 };
        var btnCdb = new Button { Text = "更新卡库", Location = new Point(250, y), Width = 105 };
        btnCdb.Click += (s, e) => DownloadCdb();
        Controls.Add(btnCdb);
        btnGo.Click += BtnGo_Click;
        Controls.Add(btnGo);
        y += 40;
        txtLog = new TextBox
        {
            Location = new Point(12, y),
            Size = new Size(406, ClientSize.Height - y - 10),
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 8.5f), WordWrap = false,
        };
        Controls.Add(txtLog);

        if (!File.Exists("cards.cdb")) { Log("未找到 cards.cdb"); DownloadCdb(); }
        FormClosed += (s, e) => Stop();
        Log("就绪。默认连本机 ai_duel --server（127.0.0.1:7911）。");
    }
    void Add(Control c) { Controls.Add(c); }

    void Log(string s)
    {
        if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { Log(s); }); return; }
        txtLog.AppendText(s + "\r\n");
    }

    void Stop()
    {
        running = false;
        try { if (tcp != null) tcp.Close(); } catch { }
        try { if (core != null && !core.HasExited) core.Kill(); } catch { }
        btnGo.Text = "连接并开始";
        btnGo.Enabled = true;
    }

    void BtnGo_Click(object sender, EventArgs e)
    {
        if (running) { Stop(); Log("已停止。"); return; }

        string deck = cmbDeck.SelectedItem as string ?? "AI_ChaosRitual.ydk";
        string model = cmbModel.SelectedItem as string ?? "model.onnx";
        string host = txtHost.Text.Trim(), pass = txtPass.Text.Trim(), name = txtName.Text.Trim();
        int port;
        if (!int.TryParse(txtPort.Text.Trim(), out port)) { Log("端口不合法"); return; }

        var psi = new ProcessStartInfo("ai_duel.exe")
        {
            Arguments = "--client stdio " + port + " \"" + pass + "\" \"" + deck + "\"" +
                        " --client-bc \"" + model + "\" --client-name \"" + name + "\" --client-games 1",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.EnvironmentVariables["YGO_CLIENT_EXTADDR"] = "1";
        psi.EnvironmentVariables["PYTHONUTF8"] = "1";
        try { core = Process.Start(psi); }
        catch (Exception ex) { Log("决策核启动失败: " + ex.Message); return; }
        core.ErrorDataReceived += (s, e2) => { if (e2.Data != null) Log("[核] " + e2.Data); };
        core.EnableRaisingEvents = true;
        core.Exited += (s, e2) => Log("决策核退出 code=" + core.ExitCode);
        core.BeginErrorReadLine();

        tcp = new TcpClient();
        try { tcp.Connect(host, port); }
        catch (Exception ex) { Log("连接失败: " + ex.Message + "（本机没开服务器就先跑 ai_duel --server）"); Stop(); return; }
        tcp.NoDelay = true;
        Log("已连接 " + host + ":" + port + "，卡组=" + deck + " 模型=" + model);
        running = true;
        btnGo.Text = "停止";
        var net = tcp.GetStream();

        var up = new Thread(() =>
        {
            var buf = new byte[65536];
            try
            {
                int n;
                while (running && (n = net.Read(buf, 0, buf.Length)) > 0)
                { var bs = core.StandardInput.BaseStream; bs.Write(buf, 0, n); bs.Flush(); }
            }
            catch { }
            try { core.StandardInput.Close(); } catch { }
            Log("连接关闭。");
        });
        up.IsBackground = true; up.Start();

        var down = new Thread(() =>
        {
            var bs = core.StandardOutput.BaseStream;
            var buf = new byte[65536];
            try
            {
                int n;
                while (running && (n = bs.Read(buf, 0, buf.Length)) > 0)
                    net.Write(buf, 0, n);
            }
            catch { }
            try { tcp.Close(); } catch { }
        });
        down.IsBackground = true; down.Start();
    }
}
