using System.Drawing;
using System.Windows.Forms;

namespace DragSim;

/// <summary>
/// 合成 OLE 拖曳器。用途＝讓「檔案被拖到接收層」可以重複自動驗證，不必依賴人工手勢。
///
/// 關鍵條件（M0.5 兩次失敗後於 spike v3 找出）：<c>DoDragDrop</c> 必須在**同一個執行緒
/// 回應 WM_LBUTTONDOWN 時同步呼叫**。從別處（計時器、其他執行緒）呼叫即使滑鼠狀態正確也
/// 不會開始拖曳——那不是平台限制，是呼叫位置錯了。
///
/// 用法:
///   DragSim --drag &lt;檔案&gt; &lt;起點X&gt; &lt;起點Y&gt; &lt;目標X&gt; &lt;目標Y&gt;
///   DragSim --target &lt;x&gt; &lt;y&gt; &lt;秒數&gt;      對照組：已知可收拖放的視窗
///
/// 注意: 起點請選在沒有圖示的桌面空白處，避免合成點擊誤觸桌面項目。
/// </summary>
internal static class Program
{
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const uint WM_LBUTTONDOWN = 0x0201;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct INPUT { public uint type; public MOUSEINPUT mi; }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static void MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var input = new INPUT
        {
            type = 0,
            mi = new MOUSEINPUT
            {
                dx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1)),
                dy = (int)Math.Round((y - vy) * 65535.0 / (vh - 1)),
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
            }
        };
        SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    private static void Button(uint flag)
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flag } };
        SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    /// <summary>拖曳來源視窗。DoDragDrop 就在 WM_LBUTTONDOWN 裡同步呼叫——這是會動的關鍵。</summary>
    private sealed class SourceForm : Form
    {
        public DataObject? Payload;
        public DragDropEffects Effect;
        public bool Started;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_LBUTTONDOWN)
            {
                Started = true;
                try { Effect = DoDragDrop(Payload!, DragDropEffects.Copy); }
                catch (Exception ex) { Console.Error.WriteLine("DoDragDrop threw: " + ex.Message); }
                Console.WriteLine("drageffect=" + Effect);
                return;
            }
            base.WndProc(ref m);
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 5 && args[0] == "--drag")
            return RunDrag(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));

        if (args.Length >= 4 && args[0] == "--target")
            return RunControlTarget(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]));

        Console.Error.WriteLine("usage: DragSim --drag <file> <startX> <startY> <targetX> <targetY>");
        Console.Error.WriteLine("       DragSim --target <x> <y> <seconds>");
        return 2;
    }

    private static int RunDrag(string file, int sx, int sy, int tx, int ty)
    {
        if (!File.Exists(file))
        {
            Console.Error.WriteLine("no such file: " + file);
            return 2;
        }

        Console.WriteLine($"DragSim: file={file}");
        Console.WriteLine($"DragSim: virtual screen=({GetSystemMetrics(SM_XVIRTUALSCREEN)},{GetSystemMetrics(SM_YVIRTUALSCREEN)}) " +
                          $"{GetSystemMetrics(SM_CXVIRTUALSCREEN)}x{GetSystemMetrics(SM_CYVIRTUALSCREEN)}");
        Console.WriteLine($"DragSim: start=({sx},{sy}) target=({tx},{ty})");

        var form = new SourceForm
        {
            Text = "DragSim source",
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(sx - 40, sy - 40),
            Size = new Size(120, 120),
            BackColor = Color.DimGray,
            TopMost = true,
            Payload = new DataObject(DataFormats.FileDrop, new[] { file })
        };

        var gate = new ManualResetEventSlim(false);
        var input = new Thread(() =>
        {
            Thread.Sleep(800);
            MoveTo(sx, sy);
            Thread.Sleep(250);
            Button(MOUSEEVENTF_LEFTDOWN);          // -> SourceForm.WndProc -> DoDragDrop
            for (int i = 1; i <= 12; i++)           // 分步移動，跨過拖曳門檻並讓接收層跟上
            {
                MoveTo(sx + (tx - sx) * i / 12, sy + (ty - sy) * i / 12);
                Thread.Sleep(70);
            }
            Thread.Sleep(500);                      // 停在目標上，讓接收層有機會解析 drop
            Button(MOUSEEVENTF_LEFTUP);
            gate.Set();
        });
        input.IsBackground = true;
        input.Start();

        var close = new System.Windows.Forms.Timer { Interval = 12000 };
        close.Tick += (_, _) => { close.Stop(); form.Close(); };
        close.Start();

        Application.Run(form);
        gate.Wait(3000);

        if (!form.Started)
        {
            Console.Error.WriteLine("drag never started (WM_LBUTTONDOWN not delivered to the source window)");
            return 1;
        }
        return form.Effect == DragDropEffects.None ? 3 : 0;
    }

    /// <summary>
    /// 對照組：一個已知可正常接收 OLE 拖放的 WinForms 視窗。
    /// 若連它都收不到 drop，代表問題在拖曳器，而不是被測的接收層。
    /// </summary>
    private static int RunControlTarget(int x, int y, int holdSeconds)
    {
        bool got = false;
        var form = new Form
        {
            Text = "DragSim control target",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(x, y),
            Size = new Size(400, 300),
            BackColor = Color.DarkSlateBlue,
            TopMost = true
        };
        form.AllowDrop = true;
        form.DragEnter += (_, e) =>
        {
            e.Effect = DragDropEffects.Copy;
            Console.WriteLine("CONTROL DragEnter");
        };
        form.DragDrop += (_, e) =>
        {
            got = true;
            var files = (string[]?)e.Data?.GetData(DataFormats.FileDrop);
            Console.WriteLine("CONTROL DROP RECEIVED: " + (files is null ? "(null)" : string.Join(",", files)));
        };

        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, holdSeconds) * 1000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Console.WriteLine(got ? "CONTROL RESULT: PASS" : "CONTROL RESULT: FAIL");
            form.Close();
        };
        timer.Start();

        Console.WriteLine($"CONTROL target window at ({x},{y}) size 400x300, hold {holdSeconds}s -> drop at ({x + 200},{y + 150})");
        Application.Run(form);
        return got ? 0 : 1;
    }
}
