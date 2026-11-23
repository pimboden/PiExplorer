using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ImbotechConsulting.PiExplorer;

/// <summary>
/// Preview of the selected file. Images are drawn directly; everything else (PDF, Office, text...)
/// is hosted through the Windows shell preview handlers, i.e. what Explorer's preview pane uses.
/// </summary>
internal sealed class PreviewPanel : Panel
{
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico" };

    private readonly Panel _handlerHost = new() { Dock = DockStyle.Fill };
    private readonly PictureBox _image = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Visible = false };
    private readonly Label _message = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText };
    private IPreviewHandler? _handler;
    private IStream? _stream;
    private string? _shown;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string> NoPreviewText { get; set; } = () => "No preview";

    public PreviewPanel()
    {
        Controls.Add(_handlerHost);
        Controls.Add(_image);
        Controls.Add(_message);
        _message.BringToFront();
        _handlerHost.Resize += (_, _) => ResizeHandler();
    }

    public void ShowFile(string? path)
    {
        if (path == _shown) return;
        _shown = path;
        Clear();
        if (path == null || !File.Exists(path)) { Message(""); return; }

        var ext = Path.GetExtension(path);
        try
        {
            if (ImageExts.Contains(ext)) { ShowImage(path); return; }
            if (!ShowWithHandler(path, ext)) Message(NoPreviewText());
        }
        catch { Clear(); Message(NoPreviewText()); }
    }

    private void Message(string text)
    {
        _message.Text = text;
        _message.Visible = true;
        _message.BringToFront();
    }

    private void ShowImage(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var img = Image.FromStream(fs);
        _image.Image = new Bitmap(img); // copy, so the file is not locked
        _image.Visible = true;
        _message.Visible = false;
        _image.BringToFront();
    }

    private bool ShowWithHandler(string path, string ext)
    {
        var clsid = FindHandlerClsid(ext);
        if (clsid == null) return false;
        var type = Type.GetTypeFromCLSID(clsid.Value);
        if (type == null) return false;

        var obj = Activator.CreateInstance(type);
        if (obj == null) return false;

        try
        {
            if (obj is IInitializeWithStream withStream)
            {
                SHCreateStreamOnFileEx(path, 0x40 /* STGM_READ|SHARE_DENY_NONE */, 0, false, IntPtr.Zero, out var stream);
                _stream = stream;
                withStream.Initialize(stream, 0);
            }
            else if (obj is IInitializeWithFile withFile) withFile.Initialize(path, 0);
            else { Marshal.FinalReleaseComObject(obj); return false; }

            var handler = (IPreviewHandler)obj;
            var r = HostRect();
            handler.SetWindow(_handlerHost.Handle, ref r);
            handler.DoPreview();
            _handler = handler;
            _message.Visible = false;
            _image.Visible = false;
            _handlerHost.BringToFront();
            return true;
        }
        catch
        {
            try { Marshal.FinalReleaseComObject(obj); } catch { }
            return false;
        }
    }

    private Rect HostRect() => new() { Left = 0, Top = 0, Right = _handlerHost.ClientSize.Width, Bottom = _handlerHost.ClientSize.Height };

    private void ResizeHandler()
    {
        if (_handler == null) return;
        try { var r = HostRect(); _handler.SetRect(ref r); } catch { }
    }

    private void Clear()
    {
        if (_handler != null)
        {
            try { _handler.Unload(); } catch { }
            try { Marshal.FinalReleaseComObject(_handler); } catch { }
            _handler = null;
        }
        if (_stream != null) { try { Marshal.FinalReleaseComObject(_stream); } catch { } _stream = null; }
        _image.Image?.Dispose();
        _image.Image = null;
        _image.Visible = false;
        _message.Text = "";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Clear();
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- shell interop

    private static Guid? FindHandlerClsid(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return null;
        uint len = 128;
        var sb = new StringBuilder((int)len);
        // ASSOCSTR_SHELLEXTENSION = 16, extra = the IPreviewHandler interface id
        if (AssocQueryStringW(0, 16, ext, "{8895b1c6-b41f-4c1c-a562-0d564250836f}", sb, ref len) != 0) return null;
        return Guid.TryParse(sb.ToString(), out var g) ? g : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(uint flags, int str, string assoc, string? extra, StringBuilder? output, ref uint len);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attrs, [MarshalAs(UnmanagedType.Bool)] bool create,
        IntPtr template, out IStream stream);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref Rect rect);
        void SetRect(ref Rect rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] uint TranslateAccelerator(IntPtr pmsg);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode);
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        void Initialize(IStream stream, uint mode);
    }
}
