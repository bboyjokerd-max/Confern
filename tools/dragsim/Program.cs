using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DragSim;

/// <summary>
/// 自動化驗證器：以真實的 OLE DoDragDrop 拖曳一個檔案，並用 SendInput 移動/放開滑鼠。
/// 用途＝讓「全螢幕接收層能否被拖放命中」可以重複驗證，不必依賴人工手勢。
///
/// 用法: DragSim &lt;檔案路徑&gt; &lt;目標X&gt; &lt;目標Y&gt; [起點X] [起點Y]
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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public MOUSEINPUT mi; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

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
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void Button(uint flag)
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flag } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--target")
            return RunControlTarget(int.Parse(args[1]), int.Parse(args[2]),
                                    args.Length > 3 ? int.Parse(args[3]) : 20);

        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: DragSim <file> <targetX> <targetY> [startX] [startY]");
            Console.Error.WriteLine("       DragSim --target <x> <y> [holdSeconds]");
            return 2;
        }

        string file = args[0];
        int tx = int.Parse(args[1]);
        int ty = int.Parse(args[2]);
        int sx = args.Length > 3 ? int.Parse(args[3]) : Math.Max(2, tx - 400);
        int sy = args.Length > 4 ? int.Parse(args[4]) : ty;

        if (!File.Exists(file))
        {
            Console.Error.WriteLine("no such file: " + file);
            return 2;
        }

        Console.WriteLine($"DragSim: file={file}");
        Console.WriteLine($"DragSim: virtual screen=({GetSystemMetrics(SM_XVIRTUALSCREEN)},{GetSystemMetrics(SM_YVIRTUALSCREEN)}) " +
                          $"{GetSystemMetrics(SM_CXVIRTUALSCREEN)}x{GetSystemMetrics(SM_CYVIRTUALSCREEN)}");
        Console.WriteLine($"DragSim: start=({sx},{sy}) target=({tx},{ty})");

        var data = new DataObject(DataFormats.FileDrop, new[] { file });
        DragDropEffects effect = DragDropEffects.None;
        Exception? dragError = null;

        // 更真實的手勢：來源視窗要「可見」且就壓在起點上，
        // 並且在 DoDragDrop 之前先產生位移（觸發拖曳門檻）。
        var ready = new ManualResetEventSlim(false);
        var go = new ManualResetEventSlim(false);

        var dragThread = new Thread(() =>
        {
            var owner = new Form
            {
                Text = "DragSim source",
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(sx - 20, sy - 20),
                Size = new Size(80, 80),
                BackColor = Color.DimGray,
                TopMost = true
            };
            owner.Show();
            ready.Set();
            go.Wait(5000);
            try { effect = owner.DoDragDrop(data, DragDropEffects.Copy); }
            catch (Exception ex) { dragError = ex; }
            finally { owner.Dispose(); }
        });
        dragThread.SetApartmentState(ApartmentState.STA);
        dragThread.IsBackground = true;
        dragThread.Start();

        if (!ready.Wait(3000)) Console.Error.WriteLine("warning: source window not ready");

        MoveTo(sx, sy);
        Thread.Sleep(300);
        Button(MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(200);
        Console.WriteLine($"DragSim: LEFTDOWN sent, GetAsyncKeyState(LBUTTON)=0x{GetAsyncKeyState(0x01) & 0xFFFF:X4}");
        MoveTo(sx + 6, sy);                 // 先有位移，拖曳門檻才算被跨過
        Thread.Sleep(200);
        go.Set();                           // 現在才讓 DoDragDrop 接手

        const int steps = 12;
        for (int i = 1; i <= steps; i++)
        {
            MoveTo(sx + (tx - sx) * i / steps, sy + (ty - sy) * i / steps);
            Thread.Sleep(60);
        }

        GetCursorPos(out var landed);
        Console.WriteLine($"DragSim: cursor landed at ({landed.X},{landed.Y})");
        Thread.Sleep(500);              // 停在目標上，讓接收層有機會解析 drop
        Button(MOUSEEVENTF_LEFTUP);
        Thread.Sleep(300);

        if (!dragThread.Join(5000))
            Console.Error.WriteLine("warning: drag thread did not finish within 5 s");

        if (dragError is not null) Console.Error.WriteLine("DoDragDrop threw: " + dragError.Message);
        Console.WriteLine("drageffect=" + effect);
        return 0;
    }

    /// <summary>
    /// 對照組：一個已知可正常接收 OLE 拖放的 WinForms 視窗。
    /// 若連它都收不到 drop，代表問題在合成拖曳器，而不是接收層命題。
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
