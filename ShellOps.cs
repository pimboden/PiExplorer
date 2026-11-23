using System.Runtime.InteropServices;

namespace ImbotechConsulting.PiExplorer;

/// <summary>Options shared by all panes.</summary>
internal static class AppOptions
{
    public static bool FoldersOpenInNewPane = true;
    public static bool ShowHidden = false;
}

internal static class TerminalLauncher
{
    /// <summary>
    /// Opens Windows Terminal in <paramref name="folder"/> using the user's default profile
    /// (PowerShell, cmd, Git Bash, WSL...), so bash-based profiles work too. Falls back to cmd.exe.
    /// </summary>
    public static void Open(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("wt.exe") { WorkingDirectory = folder, UseShellExecute = false };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(folder); // ArgumentList handles quoting, incl. drive roots like C:\
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Windows Terminal not installed
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = true })?.Dispose();
        }
    }
}

/// <summary>Native Explorer copy/move/delete (progress dialog, conflict prompts, recycle bin).</summary>
internal static class ShellOps
{
    private const uint FoMove = 1, FoCopy = 2, FoDelete = 3;
    private const ushort FofAllowundo = 0x40, FofNoconfirmmkdir = 0x200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Shfileopstruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref Shfileopstruct op);

    private static bool Run(uint func, IEnumerable<string> src, string? dest, ushort flags, IntPtr hwnd)
    {
        var op = new Shfileopstruct
        {
            hwnd = hwnd,
            wFunc = func,
            pFrom = string.Join("\0", src) + "\0\0",
            pTo = dest == null ? null : dest + "\0\0",
            fFlags = flags,
        };
        return SHFileOperationW(ref op) == 0 && op.fAnyOperationsAborted == 0;
    }

    public static bool Copy(IEnumerable<string> src, string destDir, IntPtr hwnd) =>
        Run(FoCopy, src, destDir, FofAllowundo | FofNoconfirmmkdir, hwnd);

    public static bool Move(IEnumerable<string> src, string destDir, IntPtr hwnd) =>
        Run(FoMove, src, destDir, FofAllowundo | FofNoconfirmmkdir, hwnd);

    public static bool Delete(IEnumerable<string> src, IntPtr hwnd) =>
        Run(FoDelete, src, null, FofAllowundo, hwnd);
}

/// <summary>Shared small-icon list backed by the shell's icons.</summary>
internal static class IconCache
{
    public static readonly ImageList List = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
    private static readonly Dictionary<string, int> Map = new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Shfileinfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref Shfileinfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    public static int Get(string fullPath, bool isDir, bool isDrive, bool real = false)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var unique = isDrive || (!isDir && ext is ".exe" or ".lnk" or ".ico" or ".msi" or "");
        var key = real ? "real:" + fullPath : isDrive ? "drive:" + fullPath : isDir ? "\0dir" : unique ? fullPath : ext;
        if (Map.TryGetValue(key, out var idx)) return idx;

        var info = new Shfileinfo();
        uint flags = 0x100 | 0x1; // ICON | SMALLICON
        uint attrs = 0;
        if (!real && (!unique || (isDir && !isDrive)))
        {
            flags |= 0x10; // USEFILEATTRIBUTES
            attrs = isDir ? 0x10u : 0x80u;
        }
        SHGetFileInfo(fullPath, attrs, ref info, (uint)Marshal.SizeOf<Shfileinfo>(), flags);
        if (info.hIcon == IntPtr.Zero) { Map[key] = -1; return -1; }
        using (var ic = Icon.FromHandle(info.hIcon)) List.Images.Add(ic);
        DestroyIcon(info.hIcon);
        return Map[key] = List.Images.Count - 1;
    }
}
