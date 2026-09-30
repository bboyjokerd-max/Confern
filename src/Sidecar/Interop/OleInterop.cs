using System.Runtime.InteropServices;

namespace Sidecar;

/// <summary>
/// OLE 拖放互操作（規格 §4.3「收到第一個 `DragEnter` 後 ≤100 ms 顯示」的酬載來源）。
///
/// **為什麼需要 OLE**：`DragAcceptFiles`／`WM_DROPFILES` **只在放手當下**才給檔案清單；
/// 要顯示 `原格式 → 目標格式` 與輸出檔名，必須在拖曳**途中**讀到酬載，
/// 而那只有 `IDropTarget::DragEnter(IDataObject*)` 拿得到。
///
/// 型別一律 public：COM interop 需要型別對 COM 可見，否則建立 CCW 會失敗。
/// </summary>
public static class OleInterop
{
    public const ushort CF_HDROP = 15;
    public const uint DVASPECT_CONTENT = 1;
    public const uint TYMED_HGLOBAL = 1;
    public const uint DROPEFFECT_NONE = 0;
    public const uint DROPEFFECT_COPY = 1;
    public const uint DROPEFFECT_MOVE = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct FORMATETC
    {
        public ushort cfFormat;
        public IntPtr ptd;
        public uint dwAspect;
        public int lindex;
        public uint tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STGMEDIUM
    {
        public uint tymed;
        public IntPtr unionmember;     // TYMED_HGLOBAL 時：HGLOBAL（CF_HDROP 時即 HDROP）
        public IntPtr pUnkForRelease;
    }

    /// <summary>IDataObject。方法**順序即 vtable 順序**，不可重排。</summary>
    [ComImport, Guid("0000010e-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDataObject
    {
        void GetData(ref FORMATETC format, out STGMEDIUM medium);
        void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium);
        [PreserveSig] int QueryGetData(ref FORMATETC format);
        void GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut);
        void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release);
        void EnumFormatEtc(uint direction, out IntPtr enumFormatEtc);
        void DAdvise(ref FORMATETC format, uint advf, IntPtr advSink, out uint connection);
        void DUnadvise(uint connection);
        void EnumDAdvise(out IntPtr enumAdvise);
    }

    /// <summary>
    /// 與 <c>Native.POINT</c> **版面完全相同**（兩個 int）。之所以另立一個，是因為 COM 可見的
    /// 公開介面簽章不能使用 internal 型別（CS0051），而 <c>Native</c> 是 internal。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct OlePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>IDropTarget。方法**順序即 vtable 順序**，不可重排。</summary>
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropTarget
    {
        void DragEnter(IDataObject dataObject, uint keyState, OlePoint pt, ref uint effect);
        void DragOver(uint keyState, OlePoint pt, ref uint effect);
        void DragLeave();
        void Drop(IDataObject dataObject, uint keyState, OlePoint pt, ref uint effect);
    }

    [DllImport("ole32.dll")] public static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] public static extern void OleUninitialize();
    [DllImport("ole32.dll")] public static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget dropTarget);
    [DllImport("ole32.dll")] public static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("ole32.dll")] public static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    public static string HResultText(int hr) => hr switch
    {
        0 => "S_OK",
        1 => "S_FALSE",
        unchecked((int)0x800401F0) => "CO_E_NOTINITIALIZED (OleInitialize missing?)",
        unchecked((int)0x80010106) => "RPC_E_CHANGED_MODE (thread is MTA? OLE drag/drop needs an STA)",
        unchecked((int)0x80070057) => "E_INVALIDARG",
        unchecked((int)0x80040064) => "OLE_E_ADVISENOTSUPPORTED",
        unchecked((int)0x80004005) => "E_FAIL",
        _ => $"0x{hr:X8}"
    };
}
