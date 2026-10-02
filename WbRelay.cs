// WindBot 形态壳：gen181 决策核的纯转发容器。
// CLI 与 WindBot 同风格（-h/-p/-n/-d/-w），真实套接字在此，CTOS/STOC 帧
// 与网络线协议逐字节相同，直接在 TCP <-> 决策核 stdin/stdout 间泵送。
// 决策核 = ai_duel.exe --client stdio ... --client-bc model.onnx
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

class WbRelay
{
    static int Main(string[] args)
    {
        string host = "127.0.0.1", name = "gen181", deck = "AI_ChaosRitual.ydk";
        string core = "ai_duel.exe", model = "model.onnx", pass = "s";
        int port = 888;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLower();
            if (a == "-h" && i + 1 < args.Length) host = args[++i];
            else if (a == "-p" && i + 1 < args.Length) port = int.Parse(args[++i]);
            else if (a == "-n" && i + 1 < args.Length) name = args[++i];
            else if (a == "-d" && i + 1 < args.Length) deck = args[++i];
            else if (a == "-w" && i + 1 < args.Length) pass = args[++i];
            else if (a == "-m" && i + 1 < args.Length) model = args[++i];
            else if (a == "--core" && i + 1 < args.Length) core = args[++i];
            else { Console.Error.WriteLine("用法: WbRelay -h host -p port -w 密码 -n 名字 -d 卡组 [-m model.onnx] [--core ai_duel.exe]"); return 2; }
        }

        var psi = new ProcessStartInfo(core)
        {
            Arguments = "--client stdio " + port + " \"" + pass + "\" \"" + deck + "\"" +
                        " --client-bc \"" + model + "\" --client-name \"" + name + "\" --client-games 1",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.StandardOutputEncoding = System.Text.Encoding.GetEncoding(1252); // 原始字节直通，不做解码解释
        psi.EnvironmentVariables["YGO_CLIENT_EXTADDR"] = "1"; // 自建 ai_server 握手要求首包 EXTERNAL_ADDRESS
        if (Environment.GetEnvironmentVariable("WB_TRACE") != null)
            psi.EnvironmentVariables["YGO_CLIENT_RAWDUMP"] = "rawdump.bin"; // STOC 原始帧调试（当前目录）

        Process coreProc = Process.Start(psi);
        coreProc.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine("[core] " + e.Data); };
        coreProc.BeginErrorReadLine();

        TcpClient tcp = new TcpClient();
        tcp.Connect(host, port);
        tcp.NoDelay = true;
        Console.Error.WriteLine("[wb] 已连接 " + host + ":" + port);
        var net = tcp.GetStream();

        // TCP -> 核 stdin（STOC）
        var up = new Thread(() =>
        {
            var buf = new byte[65536];
            try
            {
                int n;
                while ((n = net.Read(buf, 0, buf.Length)) > 0)
                {
                    var bs = coreProc.StandardInput.BaseStream;
                    bs.Write(buf, 0, n);
                    bs.Flush();
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("[wb] 上行流结束: " + ex.Message); }
            try { coreProc.StandardInput.Close(); } catch { }
        });
        up.IsBackground = true;
        up.Start();

        // 核 stdout -> TCP（CTOS）
        var down = new Thread(() =>
        {
            var bs = coreProc.StandardOutput.BaseStream;
            var buf = new byte[65536];
            var trace = Environment.GetEnvironmentVariable("WB_TRACE") != null
                ? new System.IO.FileStream("ctos_trace.bin",
                    System.IO.FileMode.Create, System.IO.FileAccess.Write) : null;
            try
            {
                int n;
                while ((n = bs.Read(buf, 0, buf.Length)) > 0)
                {
                    if (trace != null) { trace.Write(buf, 0, n); trace.Flush(); }
                    net.Write(buf, 0, n);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("[wb] 下行流结束: " + ex.Message); }
            try { tcp.Close(); } catch { }
        });
        down.IsBackground = true;
        down.Start();

        coreProc.WaitForExit();
        Console.Error.WriteLine("[wb] 决策核退出 code=" + coreProc.ExitCode);
        up.Join(2000);
        return 0;
    }
}
