using System.Runtime.InteropServices;

namespace Sidecar;

/// <summary>
/// 命中層視窗的 OLE 拖放目標。**唯一職責：在拖曳進行中讀出酬載（檔名清單）並往上報。**
///
/// 它**不做**檔案操作、**不決定**收層、**不決定**輪盤內容（格式判定屬 <see cref="IFormatCatalog"/>）。
///
/// 與既有路徑的關係：`WM_DROPFILES` 仍是「放手」的檔案清單權威；OLE 這條只負責
/// 拖曳途中的酬載（規格 §4.3 需要它才能顯示 `原格式 → 目標格式`）與放手事件的**交叉確認**。
/// 兩者同時存在是刻意的，收層呼叫是幂等的，因此不會出現兩次收層。
///
/// 型別與建構子一律 public：COM interop 的公開介面簽章不能使用 internal 型別（CS0051），
/// 而 CCW 也只對 COM 可見的型別可靠。記錄則由外部注入的 <c>Action&lt;string&gt;</c> 負責，
/// 因此本類別不依賴 <see cref="Evidence"/>。
/// </summary>
[ComVisible(true)]
public sealed class DropTarget : OleInterop.IDropTarget
{
    private readonly int _monitorIndex;
    private readonly Action<string> _log;
    private readonly Action<int, string[]> _onPayload;
    private readonly Action<int> _onDropObserved;
    private readonly Action<int, int, int> _onDragOver;

    public DropTarget(int monitorIndex, Action<string> log,
                      Action<int, string[]> onPayload, Action<int> onDropObserved,
                      Action<int, int, int> onDragOver)
    {
        _monitorIndex = monitorIndex;
        _log = log;
        _onPayload = onPayload;
        _onDropObserved = onDropObserved;
        _onDragOver = onDragOver;
    }

    public void DragEnter(OleInterop.IDataObject? dataObject, uint keyState, OleInterop.OlePoint pt, ref uint effect)
    {
        effect = OleInterop.DROPEFFECT_COPY;
        string[] paths = ReadDropFiles(dataObject, out string note);
        _log($"OLE DragEnter: hit#{_monitorIndex} pt=({pt.X},{pt.Y}) files={paths.Length} first={(paths.Length > 0 ? paths[0] : "-")} {note}");
        if (paths.Length > 0) _onPayload(_monitorIndex, paths);
    }

    /// <summary>
    /// 拖曳途中的游標位置（螢幕座標）。**刻意不在這裡記錄每一次移動**——DragOver 非常頻繁，
    /// 逐次寫日誌會洗版；判斷「選取有沒有變」是 presenter 的事，它只在改變時記錄。
    /// </summary>
    public void DragOver(uint keyState, OleInterop.OlePoint pt, ref uint effect)
    {
        effect = OleInterop.DROPEFFECT_COPY;
        _onDragOver(_monitorIndex, pt.X, pt.Y);
    }

    public void DragLeave() => _log($"OLE DragLeave: hit#{_monitorIndex}");

    public void Drop(OleInterop.IDataObject? dataObject, uint keyState, OleInterop.OlePoint pt, ref uint effect)
    {
        effect = OleInterop.DROPEFFECT_COPY;
        string[] paths = ReadDropFiles(dataObject, out string note);
        // `first=` 是刻意加的：放手這條路原本只記數量，於是「收到的是哪個檔」在日誌上
        // 無法被證明，S4.3 只能斷言「有東西掉下來」。工具要能被指名道姓地驗證。
        _log($"*** OLE DROP RECEIVED (IDropTarget): hit#{_monitorIndex} files={paths.Length} first={(paths.Length > 0 ? paths[0] : "-")} {note} ***");
        _onDropObserved(_monitorIndex);
    }

    /// <summary>從 IDataObject 取出 CF_HDROP。任何一步失敗都只記錄、不丟例外（拖放路徑上不可炸）。</summary>
    private string[] ReadDropFiles(OleInterop.IDataObject? dataObject, out string note)
    {
        note = "";
        if (dataObject == null) { note = "(null data object)"; return Array.Empty<string>(); }

        var format = new OleInterop.FORMATETC
        {
            cfFormat = OleInterop.CF_HDROP,
            ptd = IntPtr.Zero,
            dwAspect = OleInterop.DVASPECT_CONTENT,
            lindex = -1,
            tymed = OleInterop.TYMED_HGLOBAL
        };

        try
        {
            int q = dataObject.QueryGetData(ref format);
            if (q != 0) { note = $"(no CF_HDROP: {OleInterop.HResultText(q)})"; return Array.Empty<string>(); }

            dataObject.GetData(ref format, out OleInterop.STGMEDIUM medium);
            try
            {
                if (medium.unionmember == IntPtr.Zero) { note = "(empty HDROP)"; return Array.Empty<string>(); }
                return Native.ReadDropFileList(medium.unionmember);
            }
            finally { OleInterop.ReleaseStgMedium(ref medium); }
        }
        catch (Exception ex)
        {
            note = $"(read failed: {ex.GetType().Name}: {ex.Message})";
            return Array.Empty<string>();
        }
    }
}
