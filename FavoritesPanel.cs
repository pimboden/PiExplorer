using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ImbotechConsulting.PiExplorer;

/// <summary>
/// Fixed left navigation pane, modelled on Explorer's: Home, Gallery, OneDrive, pinned folders,
/// Dropbox, This PC (drives + libraries) and Linux. Clicking a node opens it in the active pane.
/// </summary>
internal sealed class FavoritesPanel : UserControl
{
    public event Action<string>? FavoriteChosen;                 // open in active pane ("" = This PC)
    public event Action<string>? NewPaneRequested;               // open in a new pane
    public event Func<string?>? CurrentFolderRequested;
    public event Action<string[], string, bool>? FilesDropped;   // sources, destination, move

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PiExplorer", "favorites.json");

    private const string Placeholder = "ph";
    private const string PinnedKey = "pinned";

    private Label? _headerLabel;
    private Button? _pinButton;

    private void OnLanguageChanged()
    {
        if (_headerLabel != null) _headerLabel.Text = Loc.T("nav.header");
        if (_pinButton != null) _pinButton.Text = Loc.T("nav.pin");
        BuildTree();
    }

    private readonly TreeView _tree = new();
    private readonly List<string> _pinned = new();
    private TreeNode? _pinnedHeader;

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string subApp, string? subIdList);

    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public FavoritesPanel()
    {
        var header = new Label
        {
            Text = Loc.T("nav.header"), Dock = DockStyle.Top, Height = 28, Padding = new Padding(28, 0, 0, 0),
            Image = Glyphs.MakeBitmap(Glyphs.Star, Color.Goldenrod), ImageAlign = ContentAlignment.MiddleLeft,
            TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold),
        };
        var add = new Button
        {
            Text = Loc.T("nav.pin"), Dock = DockStyle.Bottom, Height = 30, FlatStyle = FlatStyle.Standard,
            Image = Glyphs.MakeBitmap(Glyphs.Pin, Color.RoyalBlue), TextImageRelation = TextImageRelation.ImageBeforeText,
        };
        add.Click += (_, _) => { if (CurrentFolderRequested?.Invoke() is { Length: > 0 } p) Pin(p); };

        _tree.Dock = DockStyle.Fill;
        _tree.BorderStyle = BorderStyle.None;
        _tree.ImageList = IconCache.List;
        _tree.ItemHeight = 24;
        _tree.ShowLines = false;
        _tree.HideSelection = false;
        _tree.FullRowSelect = true;
        _tree.HotTracking = true;
        _tree.ShowNodeToolTips = true;
        _tree.AllowDrop = true;
        _tree.HandleCreated += (_, _) => SetWindowTheme(_tree.Handle, "explorer", null);

        _tree.BeforeExpand += (_, e) => FillChildren(e.Node!);
        _tree.NodeMouseClick += OnNodeClick;
        _tree.KeyDown += (_, e) =>
        {
            if (_tree.SelectedNode is not { } n) return;
            if (e.KeyCode == Keys.Enter && n.Tag is string p) FavoriteChosen?.Invoke(p);
            else if (e.KeyCode == Keys.Delete && n.Name == PinnedKey) Unpin((string)n.Tag!);
        };

        // drop files on any folder node = copy there (Shift = move)
        _tree.DragOver += (_, e) =>
        {
            e.Effect = NodeAt(e)?.Tag is string { Length: > 0 } && e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                ? ((e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
        };
        _tree.DragDrop += (_, e) =>
        {
            if (NodeAt(e)?.Tag is string { Length: > 0 } dest && e.Data?.GetData(DataFormats.FileDrop) is string[] src)
                FilesDropped?.Invoke(src, dest, e.Effect == DragDropEffects.Move);
        };

        Controls.Add(_tree);
        Controls.Add(add);
        _headerLabel = header;
        _pinButton = add;
        Loc.Changed += OnLanguageChanged;
        Controls.Add(header);

        LoadPinned();
        BuildTree();
    }

    private TreeNode? NodeAt(DragEventArgs e)
    {
        var pt = _tree.PointToClient(new Point(e.X, e.Y));
        return _tree.GetNodeAt(pt);
    }

    // ---------------------------------------------------------------- tree content

    private TreeNode Folder(string text, string path, bool expandable, bool real = true)
    {
        var img = IconCache.Get(path, true, path.Length <= 3, real);
        var n = new TreeNode(text) { Tag = path, ToolTipText = path, ImageIndex = img, SelectedImageIndex = img };
        if (expandable) n.Nodes.Add(new TreeNode("…") { Name = Placeholder });
        return n;
    }

    private TreeNode Header(string text) =>
        new(text) { ForeColor = SystemColors.GrayText, ImageIndex = -1, SelectedImageIndex = -1, NodeFont = new Font(Font, FontStyle.Bold) };

    private void BuildTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();

        _tree.Nodes.Add(Folder(Loc.T("nav.home"), Profile, false));
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (Directory.Exists(pictures)) _tree.Nodes.Add(Folder(Loc.T("nav.gallery"), pictures, false));
        foreach (var od in OneDriveFolders()) _tree.Nodes.Add(Folder(Path.GetFileName(od), od, true));

        _pinnedHeader = Header(Loc.T("nav.pinned"));
        _tree.Nodes.Add(_pinnedHeader);
        AddPinnedNodes();

        _tree.Nodes.Add(Header(Loc.T("nav.locations")));
        var dropbox = Path.Combine(Profile, "Dropbox");
        if (Directory.Exists(dropbox)) _tree.Nodes.Add(Folder("Dropbox", dropbox, true));

        var thisPc = new TreeNode(Loc.T("thispc")) { Tag = "", ToolTipText = Loc.T("nav.thispctip"), Name = "thispc" };
        var pcIcon = IconCache.Get(Environment.SystemDirectory, true, true, true); // system drive icon as a stand-in
        thisPc.ImageIndex = thisPc.SelectedImageIndex = pcIcon >= 0 ? pcIcon : 0;
        thisPc.Nodes.Add(new TreeNode("…") { Name = Placeholder });
        _tree.Nodes.Add(thisPc);

        if (WslAvailable()) _tree.Nodes.Add(Folder("Linux", @"\\wsl.localhost", true));

        _tree.EndUpdate();
    }

    private void AddPinnedNodes()
    {
        var at = _tree.Nodes.IndexOf(_pinnedHeader!) + 1;
        foreach (var p in _pinned)
        {
            var name = Path.GetFileName(p.TrimEnd('\\'));
            if (name.Length == 0) name = p;
            var n = Folder(name, p, false);
            n.Name = PinnedKey;
            _tree.Nodes.Insert(at++, n);
        }
    }

    private void RebuildPinned()
    {
        _tree.BeginUpdate();
        foreach (var n in _tree.Nodes.Cast<TreeNode>().Where(n => n.Name == PinnedKey).ToList()) n.Remove();
        AddPinnedNodes();
        _tree.EndUpdate();
    }

    private static IEnumerable<string> OneDriveFolders()
    {
        var found = new List<string>();
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (accounts != null)
                foreach (var name in accounts.GetSubKeyNames())
                {
                    using var k = accounts.OpenSubKey(name);
                    if (k?.GetValue("UserFolder") is string f && Directory.Exists(f)) found.Add(f);
                }
        }
        catch { }
        try { found.AddRange(Directory.GetDirectories(Profile, "OneDrive*")); } catch { }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    private static bool WslAvailable()
    {
        try { return Directory.Exists(@"\\wsl.localhost"); } catch { return false; }
    }

    // ---------------------------------------------------------------- lazy children

    private void FillChildren(TreeNode node)
    {
        if (node.Nodes.Count != 1 || node.Nodes[0].Name != Placeholder) return;
        node.Nodes.Clear();

        if (node.Name == "thispc") { FillThisPc(node); return; }
        if (node.Tag is not string path) return;

        try
        {
            foreach (var dir in new DirectoryInfo(path).EnumerateDirectories()
                         .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 || AppOptions.ShowHidden)
                         .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                node.Nodes.Add(Folder(dir.Name, dir.FullName, HasSubDirs(dir.FullName), real: false));
        }
        catch { /* access denied / vanished: leave empty */ }
    }

    private static bool HasSubDirs(string path)
    {
        try { return Directory.EnumerateDirectories(path).Any(); } catch { return false; }
    }

    private void FillThisPc(TreeNode node)
    {
        var libs = new (string Name, string Path)[]
        {
            (Loc.T("lib.desktop"), Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            (Loc.T("lib.documents"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            (Loc.T("lib.downloads"), System.IO.Path.Combine(Profile, "Downloads")),
            (Loc.T("lib.music"), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            (Loc.T("lib.pictures"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            (Loc.T("lib.videos"), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        };
        foreach (var (name, p) in libs)
            if (Directory.Exists(p)) node.Nodes.Add(Folder(name, p, true));

        foreach (var d in DriveInfo.GetDrives())
        {
            string label;
            try { label = d.IsReady && d.VolumeLabel.Length > 0 ? d.VolumeLabel : d.DriveType == DriveType.Network ? Loc.T("drive.network") : Loc.T("drive.local"); }
            catch { label = Loc.T("drive.generic"); }
            node.Nodes.Add(Folder($"{label} ({d.Name.TrimEnd('\\')})", d.Name, true));
        }
    }

    // ---------------------------------------------------------------- interaction

    private void OnNodeClick(object? s, TreeNodeMouseClickEventArgs e)
    {
        if (e.Node is not { Tag: string path } node) return;
        _tree.SelectedNode = node;

        if (e.Button == MouseButtons.Left)
        {
            if (path.Length > 0 && !Directory.Exists(path))
            {
                MessageBox.Show(this, Loc.T("err.nofolder", path), "PiExplorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            FavoriteChosen?.Invoke(path);
        }
        else if (e.Button == MouseButtons.Middle && path.Length > 0) NewPaneRequested?.Invoke(path);
        else if (e.Button == MouseButtons.Right)
        {
            var m = new ContextMenuStrip();
            m.Items.Add(Loc.T("ctx.open"), null, (_, _) => FavoriteChosen?.Invoke(path));
            if (path.Length > 0)
            {
                m.Items.Add(Loc.T("ctx.opennew"), null, (_, _) => NewPaneRequested?.Invoke(path));
                m.Items.Add(new ToolStripSeparator());
                if (node.Name == PinnedKey) m.Items.Add(Loc.T("ctx.unpin"), null, (_, _) => Unpin(path));
                else m.Items.Add(Loc.T("ctx.pin"), null, (_, _) => Pin(path));
            }
            m.Show(_tree, e.Location);
        }
    }

    private void Pin(string path)
    {
        if (_pinned.Any(p => string.Equals(p.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
        _pinned.Add(path);
        SavePinned(); RebuildPinned();
    }

    private void Unpin(string path) { _pinned.Remove(path); SavePinned(); RebuildPinned(); }

    // ---------------------------------------------------------------- persistence

    private void LoadPinned()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                _pinned.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath)) ?? new());
                return;
            }
        }
        catch { /* fall through to defaults */ }

        _pinned.AddRange(ReadQuickAccess().Concat(DefaultFolders()).Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(15));
        SavePinned();
    }

    private void SavePinned()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_pinned));
        }
        catch { }
    }

    /// <summary>First run: take the folders from Explorer's Quick Access.</summary>
    private static List<string> ReadQuickAccess()
    {
        var res = new List<string>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            var ns = shell.NameSpace("shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}");
            foreach (dynamic item in ns.Items())
            {
                string p = item.Path;
                if (Directory.Exists(p)) res.Add(p);
            }
        }
        catch { }
        return res;
    }

    private static IEnumerable<string> DefaultFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        yield return System.IO.Path.Combine(Profile, "Downloads");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }
}
