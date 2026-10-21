using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Sidecar;

/// <summary>
/// 進度顯示（規格 §4.4／§7.3）：**在輪盤收起的原位置，原地一行 `N/M`**，完成後 2 秒淡出。
///
/// 硬規則：
///  • 只有**一個**視窗、**一行**文字，內容就地更新——不得每個檔案彈一個視窗或逐行洗版；
///  • 部分失敗時顯示 `✓ 8 / ✗ 2`，但**不自動彈出清單**（清單要使用者點才展開）。
///
/// 容器（2026-10-02，待辦 5 · D1）：本類別只依賴 <see cref="ILayeredSurface"/> 的六個成員，
/// 所以藥丸可以裝在 <see cref="PillWindow"/>（點得到）上，而輪盤照舊用 <see cref="VisualLayer"/>。
/// 幾何仍然**只有一份**：畫面就是這個類別交給容器的點陣圖，容器不得自己算位置或大小。
/// </summary>
internal sealed class ProgressPresenter : IDisposable
{
    private const int FadeMs = 2000;         // 規格 §4.4：完成後 2 秒淡出
    // 這裡原本還有一個 FailureHoldMs = 5000（失敗留比較久，讓使用者看得見 ✗ 幾個）。
    // 2026-10-05 使用者真人實測後決定一律 2 秒，於是兩個值合一。刻意不留一個永遠等於
    // FadeMs 的常數：那會變成沒有人讀、也永遠不會被選到的死設定（稽核抓的就是這種）。
    private const int FadeOutMs = 350;
    private const int BitmapWidth = 168;
    private const int HorizontalPadding = 14;
    private const int BitmapHeight = 54;

    /// <summary>
    /// 清單最多顯示幾列（規格 §7.3 的實作上限）。超過的部分用 `…還有 N 筆` 收尾——
    /// 沒有限制的清單在「拖了 300 個檔案、壞了 80 個」時會長到蓋住整個畫面，
    /// 那比不顯示還糟：它會擋住使用者正在做的事，而且沒有辦法關掉。
    /// </summary>
    public const int MaxDetailRows = 8;

    private const int DetailRowHeight = 34;      // 一列的固定行高（文字置中）
    private const int DetailPadX = 16;
    private const int DetailPadY = 14;
    private const int DetailTextInsetX = 14;
    private const float DetailFontPx = 14f;

    /// <summary>
    /// 面板圓角半徑的上限（D3）。<c>radius = min(height / 2, 18)</c>：
    /// 1 列時 <c>height / 2 = 17</c>（幾乎是膠囊），8 列時被夾在 18（圓角矩形）——
    /// 兩種列數在像素上的圓角因此**只差 1 px**，是「同一個元件、同一種形狀語言」。
    ///
    /// 原本面板沿用藥丸的「半高」規則，於是高度一變圓角跟著變：1 列像膠囊、8 列像一般的
    /// 圓角矩形，同一個元件長出兩種形狀。**藥丸的半高膠囊是凍結的設計決定，不動**
    /// （見 <see cref="Compose"/>）；被改的只有面板。
    /// </summary>
    public const float PanelCornerRadiusMax = 18f;

    /// <summary>面板兩側至少留這麼多像素（D4：寬度上限＝工作區寬 − 2 × 這個值）。</summary>
    public const int ScreenMargin = 24;

    /// <summary>檔名省略時，尾端至少保留幾個字元（否則連副檔名都留不住）。</summary>
    private const int MinTailChars = 4;

    /// <summary>中略號。用一個字元（不是三個點）——三個點會讓「保留幾個字元」的計算變成猜的。</summary>
    private const char Ellipsis = '\u2026';

    /// <summary>工作區（螢幕扣除工作列）。空矩形＝查不到，此時不做任何邊界處理。</summary>
    private Rectangle _workArea;

    private readonly ILayeredSurface _layer;
    private readonly Evidence _evidence;

    private int _x, _y;
    private bool _shown;
    private bool _started;
    private int _tickCount;
    private readonly bool _trace;              // --progress-trace：每次回呼的決策（診斷用）
    private int _updates;
    private ConversionSnapshot _last = ConversionSnapshot.Idle;
    private System.Diagnostics.Stopwatch? _finishedAt;

    /// <summary>
    /// 游標是否停在藥丸／面板上（D2）。由容器的進入／離開事件維護。
    ///
    /// 為什麼需要它：規格 §4.4 的 2 秒淡出（有失敗時 5 秒）對「要點一顆小藥丸」來說太短，
    /// 這個功能會變成**看得到、點不到**。而且 hover 不能靠「最後一次移動的時間」推論：
    /// 游標停住不動時那個推論會以為使用者離開了。
    /// </summary>
    private bool _hovered;

    /// <summary>是否已經對這次轉檔寫過「暫時不淡出」那一行（避免每個 tick 洗版）。</summary>
    private bool _hoverHoldLogged;

    /// <summary>最後一次畫在螢幕上的矩形（螢幕座標）。命中區域的唯一來源，不另立第二份幾何。</summary>
    private Rectangle _lastRect;
    private bool _hasRect;

    /// <summary>清單是否展開（規格 §7.3：**不自動彈出**，只有 <see cref="ToggleDetail"/> 會改變它）。</summary>
    private bool _expanded;

    /// <summary>
    /// 最後一次畫出的失敗列（規格 §7.3 清單的內容）。存下來是為了讓「展開」與「重畫」
    /// 拿得到同一份資料——兩者不一致時，畫面會顯示上一次轉檔的清單，而那種錯誤不會拋例外。
    /// </summary>
    private IReadOnlyList<ConversionFailureRow> _rows = Array.Empty<ConversionFailureRow>();

    /// <summary>清單裡有幾筆失敗（dump 與驗收用；面板只畫前 <see cref="MaxDetailRows"/> 列）。</summary>
    public int ExpandedRowCount => _rows.Count;

    /// <summary>
    /// tick 時去哪裡拉最新狀態（由 <c>ConversionRunner</c> 設定）。
    /// 這樣「進度從哪來」與「進度怎麼顯示」解耦：顯示層不需要知道 worker 或管道。
    /// </summary>
    public Func<ConversionSnapshot>? Source { get; set; }

    public ProgressPresenter(Evidence evidence, ILayeredSurface layer, bool trace = false)
    {
        _evidence = evidence;
        _layer = layer;
        _trace = trace;
        _layer.TickRequested += Tick;      // 由圖層的週期回呼驅動，不用輪詢迴圈
        if (_layer is PillWindow pill)
        {
            // hover 只有會收到滑鼠事件的容器才有（視覺層有 WS_EX_TRANSPARENT，永遠不會有）。
            // 用型別判斷而不是把事件放進介面：介面是「畫一張圖」的契約，
            // 而 hover／點擊是「這個容器會不會把滑鼠交給我們」的事實——兩者不是同一件事。
            pill.HoverChanged += OnHoverChanged;
            // **真實點擊的接線**（規格 §7.3）。少了這一行，藥丸畫得出來、也點得到，
            // 但點下去什麼都不會發生——而那種失效在畫面上與「還沒做好」完全一樣。
            pill.Clicked += ToggleDetail;
        }
    }

    /// <summary>
    /// 游標進入／離開。離開時**重設**淡出時鐘：否則「使用者把游標移開」的那一刻，
    /// 計時已經跑過好幾秒，畫面會在他還沒看清楚之前就開始淡出。
    /// </summary>
    private void OnHoverChanged(bool hover)
    {
        _hovered = hover;
        if (!hover)
        {
            _hoverHoldLogged = false;
            if (_last.Finished) _finishedAt = System.Diagnostics.Stopwatch.StartNew();
        }
        _evidence.Line($"progress: cursor {(hover ? "entered" : "left")} the pill/panel " +
                       $"(hover={hover} expanded={_expanded} shown={_shown})");
    }

    public bool IsVisible => _layer.IsVisible;
    public int Frames => _layer.Frames;

    /// <summary>游標現在是否停在藥丸／面板上（D2 的狀態，驗收要斷言它）。</summary>
    public bool IsHovered => _hovered;

    /// <summary>
    /// 淡出前的停留時間（毫秒）。<c>0</c>＝還沒完成。<c>-1</c>＝**現在不淡出**
    /// （游標停在上面）：D2 定案的行為，成功與失敗兩條路徑都適用。
    ///
    /// 2026-10-05：使用者真人實測後把失敗的 5 秒改成與成功相同的 2 秒，所以這裡只剩
    /// 「游標在不在」這一條規則（見 <see cref="FadeMs"/> 上方的說明）。
    /// 展開的清單也是同一條規則——游標停著就不淡出，離開之後同樣 2 秒。這是刻意的：
    /// 讀清單的保護由 D2 的 hover 規則提供，不是靠拉長秒數；拉長只會讓沒有在看的人
    /// 多被擋 5 秒，而真正在讀的人早就把游標放上去了。
    ///
    /// 刻意寫成單一函式：淡出時鐘、log 與驗收讀的是同一份規則。
    /// 分成兩處（例如 Tick 裡各寫一次）就會出現「log 說 5 秒、行為是 2 秒」這種分歧。
    /// </summary>
    public int HoldFor()
    {
        if (!_last.Finished || _finishedAt == null) return 0;
        if (_hovered) return -1;
        return FadeMs;
    }

    /// <summary>已經更新過幾次（用來斷言「原地更新」而不是「一直開新視窗」）。</summary>
    public int Updates => _updates;

    /// <summary>
    /// 最後顯示的矩形（螢幕座標，左上角＋尺寸）。尚未畫過時是 <see cref="Rectangle.Empty"/>。
    ///
    /// **單一幾何來源**：這個矩形就是 `_layer.Show(_x - w/2, _y - h/2, bitmap)` 當下算出來的那一個，
    /// 命中判定（<see cref="Contains"/>）直接問它，不另外用量測寬度重算——
    /// 那會變成兩份幾何，而兩份幾何在尺寸或 DPI 一變就不一致。
    /// </summary>
    public Rectangle PillRect => _hasRect ? _lastRect : Rectangle.Empty;

    /// <summary>螢幕座標 (x,y) 是否落在最後顯示的矩形（藥丸，或展開後的清單）內。</summary>
    public bool Contains(int x, int y) => _hasRect && _lastRect.Contains(x, y);

    /// <summary>
    /// 清單是否展開。規格 §7.3 的「清單**不自動彈出**」就是靠這個欄位：它只在
    /// <see cref="ToggleDetail"/> 裡被翻動，沒有任何計時器或狀態變化會去動它。
    /// </summary>
    public bool IsExpanded => _expanded;

    /// <summary>
    /// 展開／收起失敗清單（規格 §7.3）。
    ///
    /// **真實點擊的入口**（2026-10-02，待辦 5 · D1 定案）：藥丸改由專屬的
    /// <see cref="PillWindow"/> 承載，它的 <c>WM_LBUTTONUP</c> 直接呼叫這個方法——
    /// 也就是說使用者點的那一下與自測走的是同一條路徑，沒有第二條。
    ///
    /// 為什麼展開之後要**重設**淡出時鐘：展開是使用者的下一個動作（他要讀那份清單），
    /// 而清單若在他還在讀的時候淡出，這個功能等於沒做完。重設之後，游標停在上面就完全不淡出，
    /// 游標離開才重新計時（見 <see cref="HoldFor"/>）。
    /// </summary>
    public void ToggleDetail()
    {
        _expanded = !_expanded;
        _hoverHoldLogged = false;
        // 展開／收起都是一個**新的閱讀動作**：時鐘從這裡重新開始。
        if (_last.Finished) _finishedAt = System.Diagnostics.Stopwatch.StartNew();
        _evidence.Line($"progress detail: expanded={_expanded} rows={_rows.Count} " +
                       "(spec 7.3: the list only appears on an explicit toggle)");
        if (_shown) RedrawAlpha(255);
    }

    /// <summary>輪盤收起的位置（規格 §4.4：進度就顯示在這裡）。</summary>
    public void Start(int x, int y)
    {
        _x = x;
        _y = y;
        _shown = false;
        _started = true;
        _updates = 0;
        _tickCount = 0;
        _finishedAt = null;
        _last = ConversionSnapshot.Idle;
        _rows = Array.Empty<ConversionFailureRow>();
        _expanded = false;      // 新的一次轉檔＝收起的清單（§7.3：不自動彈出）
        _hovered = false;
        _hoverHoldLogged = false;
        _hasRect = false;
        _workArea = WorkAreaAt(x, y);   // D4：面板的寬度上限與位置夾取都以這裡的工作區為準
        _layer.StartTick(80);
    }

    /// <summary>
    /// 點 (x,y) 所在螢幕的**工作區**（扣除工作列）。查不到時回傳空矩形＝不做邊界處理。
    ///
    /// 幾何的唯一來源是 <see cref="MonitorLayout"/>（它拿 <c>GetMonitorInfoW</c> 的 <c>rcWork</c>），
    /// 不自己推算、也不寫死一組螢幕尺寸——寫死的那種「上限」在別台機器上只是裝飾品。
    /// </summary>
    internal static Rectangle WorkAreaAt(int x, int y)
    {
        try
        {
            MonitorEntry m = MonitorLayout.Nearest(x, y, MonitorLayout.Enumerate());
            return Rectangle.FromLTRB(m.Work.Left, m.Work.Top, m.Work.Right, m.Work.Bottom);
        }
        catch (Exception)
        {
            return Rectangle.Empty;
        }
    }

    /// <summary>
    /// 面板寬度上限（D4）：<c>工作區寬 − 2 × <see cref="ScreenMargin"/></c>。
    /// 回傳 0＝無上限（查不到工作區時不猜一個數字：猜錯的邊界比沒有邊界更難查）。
    /// </summary>
    public static int MaxPanelWidthFor(Rectangle workArea)
    {
        if (workArea.Width <= 0) return 0;
        return Math.Max(BitmapWidth, workArea.Width - ScreenMargin * 2);
    }

    /// <summary>由 UI 的 tick 呼叫：內容有變才重畫（同一張圖原地更新）。</summary>
    public void Update(ConversionSnapshot snapshot)
    {
        if (snapshot == _last && _shown) return;
        _last = snapshot;
        // 失敗列的**唯一來源**：快照自己（ConversionSnapshot.DetailRows）。這裡不做任何字串切割，
        // 否則「檔名／原因怎麼分」會變成第二份規則，與規格 §7.1 的組法默默分歧。
        _rows = snapshot.DetailRows;
        // 淡出時鐘：**第一次畫出「已完成」的那一幀**就開始計時，不靠狀態邊緣。
        //
        // 這裡原本寫成 justFinished = snapshot.Finished && !_last.Finished，是個定時炸彈：
        // 本方法的初始狀態是 ConversionSnapshot.Idle，而 Idle.Finished 本來就是 true
        // （Idle 的意思是「沒事做」）。所以只要**第一次回呼發生在轉檔已經結束之後**，
        // 那個邊緣就永遠不會出現 → _finishedAt 永遠是 null → 進度那一行永遠不淡出。
        // 這個情況不是理論：UI 執行緒在 ConversionClient.Start 等 worker 連線時被擋住
        // 約 300 ms，而 worker 的實際工作是 3–8 ms，所以經常「回呼來得太晚」。
        // 2026-10-01 由 S12.10d 抓到（當時 6 次出現 1 次）；--progress-trace 的 elapsed=-
        // 是決定性證據。改成本條件後與時序無關。
        if (snapshot.Finished && _finishedAt == null)
            _finishedAt = System.Diagnostics.Stopwatch.StartNew();
        RedrawAlpha(255);
    }

    /// <summary>淡出與收尾；同時負責去來源拉最新狀態（唯一的驅動來源）。</summary>
    private void Tick()
    {
        _tickCount++;
        if (_trace)
            // 高頻行：轉檔期間每個 tick 都寫一次 → 走 Detail（診斷開啟時才寫）。
            _evidence.Detail($"progress tick #{_tickCount} started={_started} shown={_shown} " +
                           $"finished={_last.Finished} elapsed={_finishedAt?.ElapsedMilliseconds.ToString() ?? "-"} " +
                           $"timerAlive={_layer.TickAlive}");
        if (!_started) return;

        if (Source != null) Update(Source());     // Update 內部有「沒變就不重畫」的守衛
        if (!_shown) return;

        if (_last.Finished && _finishedAt != null)
        {
            // D2：游標停在藥丸／面板上就**完全不淡出**（成功與失敗都一樣）。
            // 為什麼不是「延長成 10 秒」：使用者可能正在讀那份清單，猜一個秒數只是把
            // 「來不及看清楚」延後發生；離開才是他知道自己讀完了的那一刻。
            if (_hovered)
            {
                if (!_hoverHoldLogged)
                {
                    _hoverHoldLogged = true;
                    _evidence.Line($"progress: NOT fading — the cursor is on the pill/panel " +
                                   $"(elapsed={_finishedAt.ElapsedMilliseconds} ms, expanded={_expanded})");
                }
                return;
            }
            int hold = HoldFor();      // >0：一律 2 s（成功／失敗／展開後都一樣；0/-1 在上面已排除）
            long elapsed = _finishedAt.ElapsedMilliseconds;
            if (elapsed >= hold + FadeOutMs)
            {
                _evidence.Line($"progress: hiding after {elapsed} ms " +
                               $"(updates={_updates}, ticks={_tickCount}, hold={hold} ms)");
                _layer.Hide();
                _layer.StartTick(0);
                _shown = false;
                _started = false;
                return;
            }
            if (elapsed >= hold)
            {
                int alpha = (int)(255 * (1.0 - (double)(elapsed - hold) / FadeOutMs));
                RedrawAlpha(Math.Clamp(alpha, 0, 255));
            }
        }
    }

    private void RedrawAlpha(int alpha)
    {
        // 展開／收起的決定只在 _expanded；兩條路都走**同一個** alpha 參數，
        // 所以清單與藥丸的淡出是同一個節奏（沒有第二套 alpha 規則）。
        //
        // 展開時**仍然只有一張點陣圖**：藥丸與面板疊在同一張上（ComposeExpanded），
        // 位置也由這裡決定。這是「不得有第二個幾何擁有者」的實作方式——
        // 若藥丸與面板各自是一個視窗，游標停在兩者交界處就會出現兩種 hover 狀態。
        Bitmap bitmap;
        int left, top;
        if (_expanded)
        {
            ExpandedComposite composed = ComposeExpandedAtAnchor(
                _last.Line, _last.Done, _last.Total, _last.Finished, _last.Failed,
                _rows, alpha, MaxPanelWidthFor(_workArea));
            bitmap = composed.Bitmap;
            left = composed.At.X;
            top = composed.At.Y;
        }
        else
        {
            bitmap = Compose(_last.Line, _last.Done, _last.Total, _last.Finished, _last.Failed, alpha);
            // 以輪盤中心對齊，往上偏一點，避免正好壓在游標下方
            left = _x - bitmap.Width / 2;
            top = _y - bitmap.Height / 2;
        }
        using (bitmap)
        {
            // D4：展開後的面板才夾進工作區。**藥丸自己的位置是凍結的行為，不夾**——
            // 這裡多一個 `_expanded` 不是防禦性寫法，是在保護一個已經拍板的決定。
            string? clamped = null;
            if (_expanded && _workArea.Width > 0)
            {
                int wantX = left;
                left = Math.Clamp(left, _workArea.Left, Math.Max(_workArea.Left, _workArea.Right - bitmap.Width));
                if (left != wantX) clamped = $"x {wantX}->{left}";
            }
            _layer.Show(left, top, bitmap);
            // 記下真正送出去的矩形：命中判定問的是「螢幕上那一塊」，
            // 不是「我以為它有多大」。展開後矩形變大，命中區域也跟著變大。
            _lastRect = new Rectangle(left, top, bitmap.Width, bitmap.Height);
            _hasRect = true;
            _shown = true;
            _updates++;
            _evidence.Line($"progress: {_last.Line} done={_last.Done}/{_last.Total} " +
                           $"current={_last.Current ?? "-"} finished={_last.Finished} alpha={alpha} " +
                           $"expanded={_expanded} rect={_lastRect.X},{_lastRect.Y},{_lastRect.Width},{_lastRect.Height} " +
                           $"(update #{_updates})" +
                           (clamped != null ? $" panel clamped into the work area: {clamped}" : string.Empty));
        }
    }

    public void Hide()
    {
        _layer.Hide();
        _layer.StartTick(0);
        _shown = false;
    }

    // ================= 展開狀態的合成（仍然只有一張點陣圖） =================

    /// <summary>
    /// 展開後的畫面：**一張**點陣圖，藥丸在上、失敗清單在它下面。
    ///
    /// 為什麼合成成一張而不是兩個視窗：規格明文要求「不得有第二個幾何擁有者」。
    /// 兩個視窗的話，游標停在兩者之間會出現「藥丸說 hover、面板說沒有」這種分歧，
    /// 而 hover 決定「要不要淡出」——於是同一個畫面會有兩種行為。
    ///
    /// 為什麼是 `static`：與 <see cref="ComposeDetail"/> 完全相同的理由——要能在**沒有畫面**的
    /// 情況下 dump 出來驗版面。展開狀態是這個功能唯一的產出，看不到就等於沒驗過。
    /// </summary>
    /// <param name="centerX"><paramref name="centerY"/>：展開前藥丸的中心（螢幕座標）。</param>
    /// <returns>
    /// 合成結果：點陣圖、**建議的左上角螢幕座標**（＝畫面上那一塊矩形的左上角），
    /// 以及真正交給 GDI+ 的那些字串與藥丸那一行的幾何。
    ///
    /// 座標是「建議」而不是「決定」：D4 的邊界夾取屬於畫面層（它才知道工作區），
    /// 所以這裡把想要的左上角一起回傳，讓呼叫端只做夾取、不重新計算置中——
    /// 重算一次就是第二份幾何，那是這個專案明確禁止的東西。
    /// </returns>
    public static ExpandedComposite ComposeExpanded(
        string text, int done, int total, bool finished, int failed,
        IReadOnlyList<ConversionFailureRow> rows, int alpha, int maxWidthPx,
        int centerX, int centerY)
    {
        using Bitmap pill = Compose(text, done, total, finished, failed, alpha);
        (Bitmap Bitmap, IReadOnlyList<string> LaidOut) detail = ComposeDetail(rows, alpha, maxWidthPx);
        using Bitmap panel = detail.Bitmap;

        int width = Math.Max(pill.Width, panel.Width);
        // 面板直接貼在藥丸下面，**不留縫**：留縫會露出底下的桌面，看起來像兩個不相干的東西。
        int height = pill.Height + panel.Height;
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        int pillX = (width - pill.Width) / 2;
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            // 藥丸置中：整張圖的水平中心就是輪盤中心，也是使用者剛剛點的那一點。
            g.DrawImageUnscaled(pill, pillX, 0);
            g.DrawImageUnscaled(panel, (width - panel.Width) / 2, pill.Height);
        }
        return new ExpandedComposite(
            bitmap,
            new Point(centerX - width / 2, centerY - height / 2),
            detail.LaidOut,
            new Rectangle(pillX, 0, pill.Width, pill.Height));
    }

    /// <summary>
    /// 展開後的那一張合成圖與它的說明（見 <see cref="ComposeExpanded"/>）。
    ///
    /// <see cref="PanelRowsLaidOut"/> 是**真正交給 GDI+ 的字串**，與點陣圖用的是同一份
    /// （不是另外排版一次——算兩次就可能不一致，而驗收會驗到一份沒有被畫出來的字串）。
    /// 驗收就是靠它把「畫出來的那一列」與「文字本身」對起來。
    ///
    /// <see cref="PillInBitmap"/> 是藥丸在合成圖裡的矩形：驗收要證明「合成後藥丸仍然完整存在」
    /// （不是變成第二個幾何）以及「使用者剛剛點的那一點仍然在合成圖的藥丸上」。
    /// </summary>
    public sealed record ExpandedComposite(
        Bitmap Bitmap, Point At, IReadOnlyList<string> PanelRowsLaidOut, Rectangle PillInBitmap);

    /// <summary>
    /// <see cref="RedrawAlpha"/> 用的版本：座標由 presenter 自己的錨點（<c>_x,_y</c>）算出來。
    /// 與上面那個 static 方法共用同一份合成程式碼——dump 與畫面因此不可能長得不一樣。
    ///
    /// ⚠ 名字刻意**不叫** <c>ComposeExpanded</c>：多載解析會挑參數最少的那一個，
    /// 於是這個方法會遞迴呼叫自己（編譯器不會有任何抱怨，而症狀是堆疊爆掉）。
    /// </summary>
    private ExpandedComposite ComposeExpandedAtAnchor(
        string text, int done, int total, bool finished, int failed,
        IReadOnlyList<ConversionFailureRow> rows, int alpha, int maxWidthPx) =>
        ComposeExpanded(text, done, total, finished, failed, rows, alpha, maxWidthPx, _x, _y);

    /// <summary>
    /// 展開狀態的合成圖（**新的一張**，呼叫端負責釋放）。**只給驗收與 dump 用**：
    /// 展開是這個功能唯一的產出，它必須能被量測，而不是只能被相信。
    /// </summary>
    public ExpandedComposite RenderExpandedComposite() =>
        ComposeExpandedAtAnchor(_last.Line, _last.Done, _last.Total, _last.Finished, _last.Failed,
                                _rows, 255, MaxPanelWidthFor(_workArea));

    /// <summary>
    /// 畫出失敗清單（規格 §7.3）。
    ///
    /// 抽成 static 的理由與 <see cref="Compose"/> **完全相同**：要能在沒有畫面的情況下 dump 出來
    /// 驗字形與裁切。✓／✗ 這種符號若字型缺字會變成方框（方框有墨水，所以「檔案存在」什麼都證明不了），
    /// 而清單是這個功能唯一的產出——看不到就等於沒驗過。
    ///
    /// 語彙沿用同一套，不是新發明：
    ///   • 錯誤紅＝<see cref="WheelDesign.NoticeFill"/>（產品裡「這是錯誤」只有一個紅）；
    ///   • 圓角＝<c>min(高度 / 2, 18)</c>（D3）。**不是**藥丸的半高：面板列數一變，
    ///     半高就會讓 1 列長成膠囊、8 列長成一般圓角矩形——同一個元件兩種形狀；
    ///   • 字型＝與藥丸共用的 <see cref="MakeFontPx"/>；alpha＝與藥丸相同的算法；
    ///   • 寬度＝**依文字量測**（固定寬度會裁掉中文原因，那就等於使用者看不到原因），
    ///     但總寬夾在 <paramref name="maxWidthPx"/>（D4：工作區寬 − 2 × 24）；
    ///   • 太寬的一列**只縮檔名**（中略，保留副檔名），**原因永遠不縮**；
    ///   • 上限＝<see cref="MaxDetailRows"/> 列 ＋ `…還有 N 筆`，避免清單蓋住整個畫面。
    /// </summary>
    /// <param name="maxWidthPx">
    /// 面板總寬上限。<c>&lt;= 0</c>＝無上限（呼叫端查不到工作區時不猜數字）。
    /// </param>
    /// <returns>
    /// 點陣圖，以及**真正交給 GDI+ 的那些字串**（<c>LaidOut</c>）。
    ///
    /// 為什麼要把字串一起回傳：驗收要斷言的是「畫出來的那一行還帶著完整原因」，
    /// 而「我打算省略檔名」是自我報告——自我報告在這個專案裡是弱證據。
    /// 回傳的是**排完版的那一份**，與點陣圖用的是同一份字串，不是另外算一次
    /// （算兩次就可能不一致，而驗收會驗到一份沒有被畫出來的字串）。
    /// </returns>
    public static (Bitmap Bitmap, IReadOnlyList<string> LaidOut) ComposeDetail(
        IReadOnlyList<ConversionFailureRow> rows, int alpha, int maxWidthPx = 0)
    {
        int shown = Math.Min(rows.Count, MaxDetailRows);
        int hidden = Math.Max(0, rows.Count - shown);
        int lines = shown + (hidden > 0 ? 1 : 0);

        // 字型的唯一來源（面板與藥丸共用）。排版函式拿到的是**同一支**字型，
        // 所以「量出來的字」與「畫出來的字」不可能因為字型不同而分歧。
        using var font = MakeFontPx(DetailFontPx);
        IReadOnlyList<string> texts = LayoutRows(rows, maxWidthPx, font);
        int width = PanelWidthFor(rows, maxWidthPx, font);
        int height = lines * DetailRowHeight + DetailPadY * 2;
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        int a = Math.Clamp(alpha, 0, 255);
        // 錯誤紅與藥丸同一個來源、同一個不透明度（236）：清單是藥丸的展開，
        // 兩者色差會讓人以為是兩個不同的東西。
        Color panel = Color.FromArgb((int)(236 * a / 255), WheelDesign.NoticeFill);
        // D3：圓角半徑＝min(高度 / 2, 18)。1 列（高 62）→ 17、8 列（高 300）→ 18，
        // 兩種列數的圓角在像素上只差 1 px。
        //
        // **藥丸不是這樣**：藥丸的半高膠囊是凍結的設計決定（見 Compose）。這裡改的只有面板。
        // 原本面板借用 `BitmapHeight / 2`（＝27，一個與面板高度無關的常數），
        // 結果 1 列的面板圓角比半高還大（變成膠囊）、8 列的面板相對就變成一般圓角矩形。
        float radius = Math.Min(height / 2f, PanelCornerRadiusMax);
        using (var path = RoundedRect(new RectangleF(1, 1, width - 2, height - 2), radius))
        using (var brush = new SolidBrush(panel))
        {
            g.FillPath(brush, path);
        }

        using (var ink = new SolidBrush(Color.FromArgb(a, 245, 247, 250)))
        using (var format = RowFormat())
        {
            for (int i = 0; i < lines; i++)
            {
                // 文字框左右各內縮 DetailTextInsetX → 可用文字寬度＝width − 28。
                // 這條界線**必須與 LayoutRows 用來決定的那一條相同**：排版說「這樣塞得下」，
                // 這裡就必須真的塞得下；否則 GDI+ 會自己截掉尾巴，被吃掉的是**原因**，
                // 而那正是這個變更要防的靜默失敗。
                var row = new RectangleF(DetailTextInsetX, DetailPadY + i * DetailRowHeight,
                                         width - DetailTextInsetX * 2, DetailRowHeight);
                g.DrawString(texts[i], font, ink, row, format);
            }
        }

        // 分隔線：讓「還有 N 筆」看得出來是收尾，而不是第 9 筆失敗。
        if (hidden > 0)
        {
            float y = DetailPadY + shown * DetailRowHeight;
            using var hair = new Pen(Color.FromArgb((int)(90 * a / 255), 255, 255, 255), 1f);
            g.DrawLine(hair, DetailPadX, y, width - DetailPadX, y);
        }

        return (bitmap, texts);
    }

    /// <summary>
    /// 面板排版：把列資料變成**真正要畫的那些字串**。純函式——同樣的輸入永遠得到同樣的輸出，
    /// 不碰到畫面、不碰到狀態。
    ///
    /// 規則（規格 §7.3 ＋ 這個變更的 D4）：
    ///   • 塞得下就原封不動（**正常長度的那一列一個字都不准動**——過度截斷也是一種缺陷）；
    ///   • 塞不下時**只縮檔名**：中略（`2026-09-30 家庭旅遊…最終版.png`），副檔名要留著；
    ///   • **原因永遠不縮**：規格 §7.1 存在的目的就是讓使用者看得到原因，
    ///     一個被截掉的原因就是這個專案明文禁止的靜默失敗。
    /// </summary>
    /// <param name="maxWidthPx">面板總寬上限；<c>&lt;= 0</c>＝無上限。</param>
    public static IReadOnlyList<string> LayoutRows(
        IReadOnlyList<ConversionFailureRow> rows, int maxWidthPx, Font font)
    {
        bool cap = maxWidthPx > 0;
        // 文字可用寬度＝面板寬 − 左右內縮。上限換算成「文字可以多寬」只有這一處，
        // 畫的那一端用的是同一條界線（見 ComposeDetail 的 rect）。
        int available = cap ? Math.Max(1, maxWidthPx - DetailTextInsetX * 2) : 0;

        using var measureBitmap = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(measureBitmap);
        measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = RowFormat();

        int shown = Math.Min(rows.Count, MaxDetailRows);
        int hidden = Math.Max(0, rows.Count - shown);
        int lines = shown + (hidden > 0 ? 1 : 0);
        var texts = new string[lines];
        for (int i = 0; i < shown; i++)
            texts[i] = LayoutRow(rows[i], cap ? available : 0, font, measure, format);
        if (hidden > 0) texts[lines - 1] = UiStrings.Format("progress.moreHidden", hidden);
        return texts;
    }

    private static string LayoutRow(ConversionFailureRow row, int available, Font font,
                                    Graphics measure, StringFormat format)
    {
        if (available <= 0) return row.Text;          // 無上限：完全照原樣

        string full = row.Text;
        if (MeasureText(measure, full, font, format) <= available) return full;   // 塞得下就一個字都不動

        if (row.FileName.Length == 0) return row.Text;   // 沒有檔名可縮（只有原因）→ 不捏造

        string suffix = row.Reason.Length == 0 ? string.Empty : " " + row.Reason;
        // 原因**不縮**。這裡先把「原因加一個空白」的寬度扣掉，剩下的才是檔名的額度。
        int allowance = available - (int)Math.Ceiling(MeasureText(measure, suffix, font, format));
        if (allowance < 1) return row.Text;           // 原因自己就超寬：縮檔名也救不了（原因優先）

        string shortened = ShortenFileName(row.FileName, allowance, font, measure, format);
        if (shortened.Length == 0) return row.Text;   // 連一個字都放不下 → 不要畫出空的一列
        if (shortened == row.FileName) return row.Text;
        return shortened + suffix;
    }

    /// <summary>
    /// 檔名的中略：保留開頭、一個 U+2026、以及**含副檔名的結尾**。
    /// 尾端至少留 <see cref="MinTailChars"/> 個字元，否則連 `.png` 都會被吃掉。
    /// 找不到放得下的長度時回傳空字串（呼叫端會退回原字串，不畫出半個檔名）。
    /// </summary>
    private static string ShortenFileName(string fileName, int allowance, Font font,
                                          Graphics measure, StringFormat format)
    {
        int best = 0;
        for (int keep = fileName.Length - 1; keep >= 1; keep--)
        {
            int head = Math.Max(1, keep - keep / 2);
            int tail = Math.Max(MinTailChars, keep - head);
            if (head + tail > fileName.Length) continue;
            string candidate = fileName.Substring(0, head) + Ellipsis +
                               fileName.Substring(fileName.Length - tail);
            if (MeasureText(measure, candidate, font, format) <= allowance) { best = keep; break; }
        }
        if (best == 0) return string.Empty;

        int h = Math.Max(1, best - best / 2);
        int t = Math.Max(MinTailChars, best - h);
        // 副檔名優先：尾端若放得下，至少留到最後一個 '.' 之前的那一段（`….png`），
        // 也就是「使用者還看得出這是哪一種檔」。放不下就維持 MinTailChars。
        int dot = fileName.LastIndexOf('.');
        if (dot > 0)
        {
            int dotTail = fileName.Length - dot;
            if (dotTail > t && dotTail <= Math.Max(MinTailChars, best / 2)) t = dotTail;
        }
        if (h + t > fileName.Length) t = Math.Max(MinTailChars, fileName.Length - h);
        if (h + t > fileName.Length) h = Math.Max(1, fileName.Length - t);
        return fileName.Substring(0, h) + Ellipsis + fileName.Substring(fileName.Length - t);
    }

    /// <summary>
    /// 面板的總寬：依文字量測，但不超過 <paramref name="maxWidthPx"/>（D4）。
    ///
    /// 「依文字量測」不可改成固定寬度：規格 §7.1 的原因字串比檔名長得多，
    /// 裁掉就等於使用者看不到原因，而那種失敗在畫面上看起來只是「字被切掉」——
    /// 不會有任何錯誤訊息。上限則是「極端檔名不得無限變寬」那一條。
    /// </summary>
    public static int PanelWidthFor(IReadOnlyList<ConversionFailureRow> rows, int maxWidthPx, Font font)
    {
        using var measureBitmap = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(measureBitmap);
        measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = RowFormat();

        int shown = Math.Min(rows.Count, MaxDetailRows);
        int hidden = Math.Max(0, rows.Count - shown);

        int width = BitmapWidth;
        for (int i = 0; i < shown; i++)
            width = Math.Max(width, (int)Math.Ceiling(MeasureText(measure, rows[i].Text, font, format))
                                    + DetailPadX * 2 + HorizontalPadding);
        if (hidden > 0)
            width = Math.Max(width, (int)Math.Ceiling(MeasureText(measure, UiStrings.Format("progress.moreHidden", hidden), font, format))
                                    + DetailPadX * 2 + HorizontalPadding);
        if (maxWidthPx > 0) width = Math.Min(width, Math.Max(BitmapWidth, maxWidthPx));
        return width;
    }

    /// <summary>
    /// 量一行字的寬度。用**與繪製完全相同**的 <see cref="StringFormat"/>（同一組 trimming、
    /// 同一個 NoWrap、同一個 Near/Center）：若量測與繪製用不同的格式，排版就會說「塞得下」
    /// 而 GDI+ 實際上截掉了尾巴，被截掉的正好是原因。
    /// </summary>
    private static float MeasureText(Graphics measure, string text, Font font, StringFormat format)
        => measure.MeasureString(text, font, int.MaxValue, format).Width;

    /// <summary>
    /// 量一列排版後的字有多寬（像素）。給 dump 的 JSON 用：那個數字必須與畫面同源，
    /// 否則「字串對了但畫出來的東西不對」就驗不出來。
    /// </summary>
    public static float MeasureDetailRowWidth(string text)
    {
        using var font = MakeFontPx(DetailFontPx);
        using var bitmap = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bitmap);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = RowFormat();
        return MeasureText(g, text, font, format);
    }

    /// <summary>面板文字用的 <see cref="StringFormat"/>：量測與繪製**共用同一個定義**。</summary>
    private static StringFormat RowFormat() => new()
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Center,
        // NoWrap ＋ EllipsisCharacter：真的塞不下時**截斷並加省略號**，不是折到第二行。
        // 折行的代價不只是難看：那一列會需要兩倍行高，而固定的行高會把第二行裁掉，
        // 使用者看到的是半截中文——比「被截斷且有省略號」更難判斷自己在看什麼。
        // （這段註解是驗收套件用「固定寬度」的假故障逼出來的：那時 real 那一列
        //   在像素上變成兩個墨跡帶，也就是文字被折行了。正常寬度下永遠不會發生。）
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap
    };

    /// <summary>
    /// 畫出那一行。抽成 static 是為了讓 `--progress-dump` 能在**沒有畫面**的情況下驗證字形
    /// （✓／✗ 這種符號若字型缺字會變成方框，光看程式碼看不出來）。
    /// </summary>
    public static Bitmap Compose(string text, int done, int total, bool finished, int failed, int alpha)
    {
        using var font = MakeFontPx(15f);
        // 寬度依文字量測：規格 §7.1 的失敗訊息（`broken.png 無法解碼`）比 `✓ 1` 長得多，
        // 固定寬度會把它裁掉 —— 那就等於使用者看不到原因。
        int width;
        using (var measureBitmap = new Bitmap(1, 1))
        using (var measure = Graphics.FromImage(measureBitmap))
        {
            float needed = measure.MeasureString(text, font).Width;
            width = Math.Max(BitmapWidth, (int)Math.Ceiling(needed) + HorizontalPadding * 2);
        }
        var bitmap = new Bitmap(width, BitmapHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        int a = Math.Clamp(alpha, 0, 255);
        // 同一個產品裡的「錯誤紅」只有一個來源（視覺方向 D 的 WheelDesign）。
        Color pill = failed > 0
            ? Color.FromArgb((int)(236 * a / 255), WheelDesign.NoticeFill)
            : Color.FromArgb((int)(236 * a / 255), 24, 26, 32);

        // 圓角改成整顆藥丸（半高）：與方向 D 的膠囊／鍵帽／預覽列同一套語彙。
        // 原本固定 12px，在同一個畫面裡與輪盤的元件不是同一種形狀語言。
        using (var path = RoundedRect(new RectangleF(1, 1, width - 2, BitmapHeight - 2), (BitmapHeight - 2) / 2f))
        using (var brush = new SolidBrush(pill))
        {
            g.FillPath(brush, path);
        }

        using (var textBrush = new SolidBrush(Color.FromArgb(a, 245, 247, 250)))
        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        })
        {
            g.DrawString(text, font, textBrush,
                         new RectangleF(0, 2, width, BitmapHeight - 14), format);
        }

        // 底部細長進度條：完成比例一目了然（沒有 total 時不畫，避免假的 100%）
        if (total > 0)
        {
            var track = new RectangleF(12, BitmapHeight - 9, width - 24, 3);
            using (var trackBrush = new SolidBrush(Color.FromArgb((int)(90 * a / 255), 255, 255, 255)))
                g.FillRectangle(trackBrush, track);

            float ratio = Math.Clamp((float)done / total, 0f, 1f);
            if (ratio > 0)
            {
                Color barColor = failed > 0
                    ? Color.FromArgb((int)(235 * a / 255), 239, 83, 80)
                    : Color.FromArgb((int)(235 * a / 255), 102, 187, 106);
                using var barBrush = new SolidBrush(barColor);
                g.FillRectangle(barBrush, new RectangleF(track.X, track.Y, track.Width * ratio, track.Height));
            }
        }

        return bitmap;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// **單位是像素**，與 <see cref="WheelPresenter"/> 一致。原本用 Point：
    /// 在 96 DPI 下 15pt ＝ 20px（比輪盤的 17px 膠囊字還大），而且會隨 DPI 縮放——
    /// 150 % 縮放時同一張 54px 高的卡片會裝不下。
    /// </summary>
    private static Font MakeFontPx(float px)
    {
        try { return new Font(Typeface.Primary, px, FontStyle.Bold, GraphicsUnit.Pixel); }
        catch (ArgumentException) { return new Font(FontFamily.GenericSansSerif, px, FontStyle.Bold, GraphicsUnit.Pixel); }
    }

    public void Dispose()
    {
        _layer.TickRequested -= Tick;
        if (_layer is PillWindow pill)
        {
            pill.HoverChanged -= OnHoverChanged;
            pill.Clicked -= ToggleDetail;
        }
    }
}
