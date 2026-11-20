namespace ImbotechConsulting.PiExplorer;

internal sealed class MainForm : Form
{
    private readonly TableLayoutPanel _host = new() { Dock = DockStyle.Fill, RowCount = 1, Padding = new Padding(2) };
    private readonly List<FilePane> _panes = new();
    private FilePane? _active, _previous;

    public MainForm()
    {
        Text = Loc.T("app.title");
        Size = new Size(1400, 800);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2) };
        var session = LoadSession();
        if (session != null)
        {
            AppOptions.FoldersOpenInNewPane = session.FoldersOpenInNewPane;
            AppOptions.ShowHidden = session.ShowHidden;
        }
        Loc.Apply(session?.Language ?? "auto"); // "auto" = Windows display language

        tools.ImageScalingSize = new Size(20, 20);
        tools.Items.Add(Btn(Glyphs.Add, Color.SeaGreen, "tb.newpane", NewPaneFromActive));
        tools.Items.Add(Btn(Glyphs.Close, Color.Firebrick, "tb.closepane", () => { if (_active != null) ClosePane(_active); }));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(Btn(Glyphs.Copy, Color.RoyalBlue, "tb.copy", () => Transfer(false)));
        tools.Items.Add(Btn(Glyphs.Move, Color.DarkOrange, "tb.move", () => Transfer(true)));
        tools.Items.Add(Btn(Glyphs.NewFolder, Color.Goldenrod, "tb.newfolder", () => _active?.CreateFolder()));
        tools.Items.Add(Btn(Glyphs.Delete, Color.Firebrick, "tb.delete", () => _active?.DeleteSelected()));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(Toggle(Glyphs.NewWindow, "tb.foldersnew", AppOptions.FoldersOpenInNewPane, v => AppOptions.FoldersOpenInNewPane = v));
        tools.Items.Add(Toggle(Glyphs.Hidden, "tb.hidden", AppOptions.ShowHidden, v => { AppOptions.ShowHidden = v; foreach (var p in _panes) p.Refresh(); }));

        var favs = new FavoritesPanel { Dock = DockStyle.Left, Width = 240 };
        favs.FavoriteChosen += path => { if (_active != null) { _active.Navigate(path); _active.FocusList(); } };
        favs.NewPaneRequested += path => AddPane(path, _active).FocusList();
        favs.CurrentFolderRequested += () => _active?.CurrentPath;
        favs.FilesDropped += (src, dest, move) =>
        {
            if (move) ShellOps.Move(src, dest, Handle); else ShellOps.Copy(src, dest, Handle);
            RefreshAll();
        };
        var splitter = new Splitter { Dock = DockStyle.Left, Width = 4 };

        Controls.Add(_host);
        Controls.Add(splitter);
        Controls.Add(favs);
        Controls.Add(tools);
        Controls.Add(BuildMenu()); // added last = docked at the very top
        Loc.Changed += ApplyLanguage;
        ApplyLanguage();

        foreach (var path in session?.Panes ?? new())
            AddPane(NearestExisting(path), null);
        if (_panes.Count == 0)
        {
            AddPane(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null);
            AddPane(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), null);
        }
        var start = _panes[Math.Clamp(session?.Active ?? 0, 0, _panes.Count - 1)];
        SetActive(start);
        Shown += (_, _) => start.FocusList();

        RestoreWindow(session);
        FormClosing += (_, _) => SaveSession();
    }

    // toolbar/menu items whose text follows the selected language (item -> translation key)
    private readonly List<(ToolStripItem Item, string Key)> _localized = new();
    private readonly Dictionary<string, ToolStripMenuItem> _langItems = new();

    private ToolStripButton Btn(string glyph, Color color, string key, Action a)
    {
        var b = new ToolStripButton(Loc.T(key), Glyphs.MakeBitmap(glyph, color)) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
        b.Click += (_, _) => a();
        _localized.Add((b, key));
        return b;
    }

    private ToolStripButton Toggle(string glyph, string key, bool initial, Action<bool> set)
    {
        var b = new ToolStripButton(Loc.T(key), Glyphs.MakeBitmap(glyph, Color.SlateGray))
            { CheckOnClick = true, Checked = initial, DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
        b.CheckedChanged += (_, _) => set(b.Checked);
        _localized.Add((b, key));
        return b;
    }

    private MenuStrip BuildMenu()
    {
        var settings = new ToolStripMenuItem();
        var language = new ToolStripMenuItem();
        _localized.Add((settings, "menu.settings"));
        _localized.Add((language, "menu.language"));

        void AddLang(string code, string? key, string? name)
        {
            var mi = new ToolStripMenuItem(name ?? Loc.T(key!));
            if (key != null) _localized.Add((mi, key));
            mi.Click += (_, _) => Loc.Apply(code);
            _langItems[code] = mi;
            language.DropDownItems.Add(mi);
        }
        AddLang("auto", "lang.auto", null);
        language.DropDownItems.Add(new ToolStripSeparator());
        AddLang("en", null, "English");
        AddLang("de", null, "Deutsch");
        AddLang("es", null, "Español");

        settings.DropDownItems.Add(language);
        var menu = new MenuStrip();
        menu.Items.Add(settings);
        return menu;
    }

    private void ApplyLanguage()
    {
        Text = Loc.T("app.title");
        foreach (var (item, key) in _localized) { item.Text = Loc.T(key); item.ToolTipText = Loc.T(key); }
        foreach (var (code, mi) in _langItems) mi.Checked = code == Loc.Setting;
    }

    // ---------------------------------------------------------------- session (remembered between runs)

    private sealed class SessionData
    {
        public List<string> Panes { get; set; } = new();
        public int Active { get; set; }
        public bool FoldersOpenInNewPane { get; set; } = true;
        public bool ShowHidden { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }
        public string Language { get; set; } = "auto";
    }

    private static readonly string SessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PiExplorer", "session.json");

    private static SessionData? LoadSession()
    {
        try { return File.Exists(SessionPath) ? System.Text.Json.JsonSerializer.Deserialize<SessionData>(File.ReadAllText(SessionPath)) : null; }
        catch { return null; }
    }

    private void SaveSession()
    {
        try
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var s = new SessionData
            {
                Panes = _panes.Select(p => p.CurrentPath).ToList(),
                Active = _active == null ? 0 : Math.Max(0, _panes.IndexOf(_active)),
                FoldersOpenInNewPane = AppOptions.FoldersOpenInNewPane,
                ShowHidden = AppOptions.ShowHidden,
                X = b.X, Y = b.Y, Width = b.Width, Height = b.Height,
                Maximized = WindowState == FormWindowState.Maximized,
                Language = Loc.Setting,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            File.WriteAllText(SessionPath, System.Text.Json.JsonSerializer.Serialize(s));
        }
        catch { /* not critical */ }
    }

    private void RestoreWindow(SessionData? s)
    {
        if (s == null || s.Width < 300 || s.Height < 200) return;
        var r = new Rectangle(s.X, s.Y, s.Width, s.Height);
        if (!Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r))) return; // monitor no longer there
        StartPosition = FormStartPosition.Manual;
        Bounds = r;
        if (s.Maximized) WindowState = FormWindowState.Maximized;
    }

    /// <summary>A remembered folder may be gone: fall back to the closest parent that still exists.</summary>
    private static string NearestExisting(string path)
    {
        if (path.Length == 0) return "";
        try
        {
            var p = path;
            while (!string.IsNullOrEmpty(p) && !Directory.Exists(p)) p = Path.GetDirectoryName(p);
            return p ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        catch { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
    }

    // ---------------------------------------------------------------- panes

    private FilePane AddPane(string path, FilePane? after)
    {
        var p = new FilePane { Dock = DockStyle.Fill, Margin = new Padding(2) };
        p.OpenInNewPane += (src, dir) => AddPane(dir, src).FocusList();
        p.CloseRequested += ClosePane;
        p.Activated += SetActive;
        p.CopyToOther += src => Transfer(false, src);
        p.MoveToOther += src => Transfer(true, src);
        p.FilesChanged += RefreshAll;
        p.PaneDropped += MovePane;
        p.Navigate(path);

        var idx = after == null ? _panes.Count : _panes.IndexOf(after) + 1;
        _panes.Insert(idx, p);
        Rebuild();
        SetActive(p);
        return p;
    }

    /// <summary>Drag-and-drop reorder: the dragged pane takes the slot of the pane it was dropped on.</summary>
    private void MovePane(FilePane dragged, FilePane onto)
    {
        var to = _panes.IndexOf(onto);
        if (to < 0 || !_panes.Remove(dragged)) return;
        _panes.Insert(to, dragged);
        Rebuild();
        SetActive(dragged);
        dragged.FocusList();
        UpdateMarkers();
    }

    private void NewPaneFromActive() => AddPane(_active?.CurrentPath ?? "", _active).FocusList();

    private void ClosePane(FilePane p)
    {
        if (_panes.Count <= 1) return;
        var idx = _panes.IndexOf(p);
        _panes.Remove(p);
        if (_previous == p) _previous = null;
        var wasActive = _active == p;
        if (wasActive) _active = null;
        _host.Controls.Remove(p);
        p.Dispose();
        Rebuild();
        if (wasActive) { var n = _panes[Math.Min(idx, _panes.Count - 1)]; SetActive(n); n.FocusList(); }
        else UpdateMarkers();
    }

    private void Rebuild()
    {
        _host.SuspendLayout();
        _host.Controls.Clear();
        _host.ColumnStyles.Clear();
        _host.RowStyles.Clear();
        _host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _host.ColumnCount = _panes.Count;
        for (var i = 0; i < _panes.Count; i++)
        {
            _host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / _panes.Count));
            _host.Controls.Add(_panes[i], i, 0);
        }
        _host.ResumeLayout(true);
    }

    private void SetActive(FilePane p)
    {
        if (_active == p) return;
        if (_active != null && _panes.Contains(_active)) _previous = _active;
        _active = p;
        UpdateMarkers();
    }

    private void UpdateMarkers()
    {
        var target = _active == null ? null : GetTarget(_active);
        foreach (var p in _panes) { p.IsActive = p == _active; p.IsTarget = p == target; }
    }

    /// <summary>Where files go: the pane you used before this one, else the neighbour on the right/left.</summary>
    private FilePane? GetTarget(FilePane src)
    {
        if (_previous != null && _previous != src && _panes.Contains(_previous)) return _previous;
        var i = _panes.IndexOf(src);
        if (i + 1 < _panes.Count) return _panes[i + 1];
        if (i > 0) return _panes[i - 1];
        return null;
    }

    private void RefreshAll() { foreach (var p in _panes) p.Refresh(); }

    // ---------------------------------------------------------------- copy / move

    private void Transfer(bool move, FilePane? src = null)
    {
        src ??= _active;
        if (src == null) return;
        var items = src.SelectedPaths;
        if (items.Count == 0) { MessageBox.Show(this, Loc.T("msg.selectfirst"), Text); return; }
        var target = GetTarget(src);
        if (target == null) { MessageBox.Show(this, Loc.T("msg.needsecond"), Text); return; }
        if (target.CurrentPath.Length == 0) { MessageBox.Show(this, Loc.T("msg.targetpc"), Text); return; }

        if (move) ShellOps.Move(items, target.CurrentPath, Handle); else ShellOps.Copy(items, target.CurrentPath, Handle);
        RefreshAll();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5: Transfer(false); return true;
            case Keys.F6: Transfer(true); return true;
            case Keys.F7: _active?.CreateFolder(); return true;
            case Keys.Control | Keys.T: NewPaneFromActive(); return true;
            case Keys.Control | Keys.W: if (_active != null) ClosePane(_active); return true;
            case Keys.Control | Keys.Tab:
            case Keys.Control | Keys.Shift | Keys.Tab:
                if (_active != null && _panes.Count > 1)
                {
                    var step = keyData.HasFlag(Keys.Shift) ? -1 : 1;
                    _panes[(_panes.IndexOf(_active) + step + _panes.Count) % _panes.Count].FocusList();
                }
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
