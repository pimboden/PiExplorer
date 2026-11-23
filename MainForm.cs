namespace ImbotechConsulting.PiExplorer;

internal sealed class MainForm : Form
{
    private readonly TableLayoutPanel _host = new() { Dock = DockStyle.Fill, RowCount = 1, Padding = new Padding(2) };
    private readonly List<FilePane> _panes = new();
    private FilePane? _active, _previous;
    // read-only text box (not a label) so the path can be selected and copied
    private readonly TextBox _pathBar = new()
    {
        Dock = DockStyle.Bottom, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle,
        BackColor = SystemColors.Control,
    };

    private void UpdatePathBar()
    {
        var p = _active?.FullPathInfo ?? "";
        _pathBar.Text = p.Length == 0 ? Loc.T("thispc") : p;
    }

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
        ThemeSetting = session?.ColorMode ?? "system";
        ApplyColorMode(false);

        tools.ImageScalingSize = new Size(20, 20);
        tools.Items.Add(Btn(Glyphs.Add, Color.SeaGreen, "tb.newpane", NewPaneFromActive));
        tools.Items.Add(Btn(Glyphs.Close, Color.Firebrick, "tb.closepane", () => { if (_active != null) ClosePane(_active); }));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(Btn(Glyphs.Copy, Color.RoyalBlue, "tb.copy", () => Transfer(false)));
        tools.Items.Add(Btn(Glyphs.Move, Color.DarkOrange, "tb.move", () => Transfer(true)));
        tools.Items.Add(Btn(Glyphs.NewFolder, Color.Goldenrod, "tb.newfolder", () => _active?.CreateFolder()));
        tools.Items.Add(Btn(Glyphs.Delete, Color.Firebrick, "tb.delete", () => _active?.DeleteSelected()));
        tools.Items.Add(Btn(Glyphs.Terminal, Color.SlateGray, "tb.terminal", () => TerminalLauncher.Open(_active?.CurrentPath ?? "")));
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
        Controls.Add(_pathBar); // added after favs so it spans the full window width
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
        _skippedVersion = session?.SkippedVersion ?? "";
        Shown += async (_, _) => { start.FocusList(); await CheckForUpdates(false); };

        RestoreWindow(session);
        FormClosing += (_, _) => SaveSession();
    }

    // toolbar/menu items whose text follows the selected language (item -> translation key)
    private readonly List<(ToolStripItem Item, string Key)> _localized = new();
    private readonly Dictionary<string, ToolStripMenuItem> _langItems = new();
    private readonly Dictionary<string, ToolStripMenuItem> _colourItems = new();

    /// <summary>"system" (follow Windows), "light" or "dark".</summary>
    private string ThemeSetting = "system";

#pragma warning disable WFO5001 // dark mode API is experimental in WinForms
    private void ApplyColorMode(bool live)
    {
        if (ThemeSetting is not ("system" or "light" or "dark")) ThemeSetting = "system";
        Application.SetColorMode(ThemeSetting switch
        {
            "dark" => SystemColorMode.Dark,
            "light" => SystemColorMode.Classic,
            _ => SystemColorMode.System,
        });
        foreach (var (code, mi) in _colourItems) mi.Checked = code == ThemeSetting;
        if (live) { UpdateMarkers(); Invalidate(true); }
    }
#pragma warning restore WFO5001

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

        var colour = new ToolStripMenuItem();
        _localized.Add((colour, "menu.colour"));
        foreach (var (code, key) in new[] { ("system", "colour.system"), ("light", "colour.light"), ("dark", "colour.dark") })
        {
            var mi = new ToolStripMenuItem(Loc.T(key));
            _localized.Add((mi, key));
            var c = code;
            mi.Click += (_, _) =>
            {
                if (c == ThemeSetting) return;
                ThemeSetting = c;
                // WinForms cannot fully re-theme existing controls at runtime, so restart (session is restored)
                SaveSession();
                Application.Restart();
                Environment.Exit(0);
            };
            _colourItems[code] = mi;
            colour.DropDownItems.Add(mi);
        }
        settings.DropDownItems.Add(colour);

        var update = new ToolStripMenuItem();
        _localized.Add((update, "menu.update"));
        update.Click += async (_, _) => await CheckForUpdates(true);
        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add(update);

        var about = new ToolStripMenuItem();
        _localized.Add((about, "menu.about"));
        about.Click += (_, _) => ShowAbout();
        settings.DropDownItems.Add(about);

        var profiles = new ToolStripMenuItem();
        _localized.Add((profiles, "menu.profiles"));
        profiles.DropDownOpening += (_, _) => FillProfilesMenu(profiles);
        profiles.DropDownItems.Add(new ToolStripMenuItem()); // placeholder so the dropdown can open

        var menu = new MenuStrip();
        menu.Items.Add(profiles);
        menu.Items.Add(settings);
        return menu;
    }

    // ---------------------------------------------------------------- about

    private void ShowAbout()
    {
        const string url = "https://github.com/pimboden/PiExplorer";
        using var f = new Form
        {
            Text = Loc.T("menu.about").TrimEnd('…'), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, ClientSize = new Size(440, 220), Icon = Icon,
        };
        var title = new Label { Text = "PiExplorer", Left = 16, Top = 14, Width = 408, Height = 30, Font = new Font(Font.FontFamily, 14f, FontStyle.Bold) };
        var ver = new Label { Text = Loc.T("about.version", Updater.Current.ToString(3)), Left = 16, Top = 46, Width = 408, AutoSize = false };
        var text = new Label { Text = Loc.T("about.text"), Left = 16, Top = 72, Width = 408, Height = 60, AutoSize = false };
        var link = new LinkLabel { Text = url, Left = 16, Top = 136, Width = 408, AutoSize = false };
        link.LinkClicked += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 349, Top = 172, Width = 75, Height = 30 };
        f.Controls.AddRange(new Control[] { title, ver, text, link, ok });
        f.AcceptButton = ok;
        f.CancelButton = ok;
        f.ShowDialog(this);
    }

    // ---------------------------------------------------------------- updates

    private string _skippedVersion = ""; // release the user declined: not offered again automatically

    /// <param name="manual">true = from the menu: ignore the skipped version and report every outcome.</param>
    private async Task CheckForUpdates(bool manual)
    {
        try
        {
            var r = await Updater.CheckAsync();
            if (r == null)
            {
                if (manual) MessageBox.Show(this, Loc.T("upd.uptodate", Updater.Current.ToString(3)), Text);
                return;
            }
            if (!manual && r.Tag == _skippedVersion) return;
            var yes = MessageBox.Show(this, Loc.T("upd.available", r.Version.ToString(3), Updater.Current.ToString(3)),
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            if (!yes) { _skippedVersion = r.Tag; return; }
            UseWaitCursor = true;
            try
            {
                _skippedVersion = "";
                await Updater.InstallAsync(r);
            }
            finally { UseWaitCursor = false; }
            SaveSession();
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            if (manual) MessageBox.Show(this, Loc.T("upd.failed", ex.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            // automatic check at startup: stay silent (offline etc.)
        }
    }

    // ---------------------------------------------------------------- profiles (named sets of panes)

    private sealed class ProfileData
    {
        public List<string> Panes { get; set; } = new();
        public int Active { get; set; }
    }

    private static readonly string ProfilesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PiExplorer", "profiles.json");

    private static Dictionary<string, ProfileData> LoadProfiles()
    {
        try
        {
            if (File.Exists(ProfilesPath))
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ProfileData>>(File.ReadAllText(ProfilesPath)) ?? new();
        }
        catch { /* unreadable file: treat as empty */ }
        return new();
    }

    private bool StoreProfiles(Dictionary<string, ProfileData> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilesPath)!);
            var tmp = ProfilesPath + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(all, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, ProfilesPath, true); // never leave a half-written file behind
            return true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); return false; }
    }

    private void FillProfilesMenu(ToolStripMenuItem root)
    {
        root.DropDownItems.Clear();
        var all = LoadProfiles();
        var save = new ToolStripMenuItem(Loc.T("prof.save"));
        save.Click += (_, _) => SaveProfile();
        root.DropDownItems.Add(save);
        root.DropDownItems.Add(new ToolStripSeparator());
        if (all.Count == 0)
        {
            root.DropDownItems.Add(new ToolStripMenuItem(Loc.T("prof.none")) { Enabled = false });
            return;
        }
        var names = all.Keys.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var name in names)
        {
            var n = name;
            var item = new ToolStripMenuItem(n) { ToolTipText = string.Join("\n", all[n].Panes) };
            item.Click += (_, _) => LoadProfile(n);
            root.DropDownItems.Add(item);
        }
        var update = new ToolStripMenuItem(Loc.T("prof.update"));
        foreach (var name in names)
        {
            var n = name;
            var u = new ToolStripMenuItem(n);
            u.Click += (_, _) =>
            {
                if (MessageBox.Show(this, Loc.T("prof.confirmupdate", n), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                var cur = LoadProfiles();
                cur[n] = CurrentProfile();
                StoreProfiles(cur);
            };
            update.DropDownItems.Add(u);
        }
        var del = new ToolStripMenuItem(Loc.T("prof.delete"));
        foreach (var name in names)
        {
            var n = name;
            var d = new ToolStripMenuItem(n);
            d.Click += (_, _) =>
            {
                if (MessageBox.Show(this, Loc.T("prof.confirmdelete", n), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                var cur = LoadProfiles();
                if (cur.Remove(n)) StoreProfiles(cur);
            };
            del.DropDownItems.Add(d);
        }
        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(update);
        root.DropDownItems.Add(del);
    }

    private ProfileData CurrentProfile() => new()
    {
        Panes = _panes.Select(p => p.CurrentPath).ToList(),
        Active = _active == null ? 0 : Math.Max(0, _panes.IndexOf(_active)),
    };

    private void SaveProfile()
    {
        var name = AskName(Loc.T("menu.profiles"), Loc.T("prof.name"));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        var all = LoadProfiles();
        var existing = all.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing != null)
        {
            if (MessageBox.Show(this, Loc.T("prof.confirmoverwrite", existing), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            all.Remove(existing);
        }
        all[name] = CurrentProfile();
        StoreProfiles(all);
    }

    private void LoadProfile(string name)
    {
        if (!LoadProfiles().TryGetValue(name, out var prof) || prof.Panes.Count == 0) return;
        var old = _panes.ToList();
        _panes.Clear();
        _active = _previous = null;
        foreach (var p in old) { _host.Controls.Remove(p); p.Dispose(); }
        foreach (var path in prof.Panes) AddPane(NearestExisting(path), null);
        var target = _panes[Math.Clamp(prof.Active, 0, _panes.Count - 1)];
        SetActive(target);
        target.FocusList();
    }

    private string? AskName(string title, string label)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, ClientSize = new Size(360, 100),
        };
        var l = new Label { Text = label, Left = 12, Top = 12, Width = 336, AutoSize = false };
        var tb = new TextBox { Left = 12, Top = 36, Width = 336 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 192, Top = 66, Width = 75 };
        var cancel = new Button { Text = Loc.T("btn.cancel"), DialogResult = DialogResult.Cancel, Left = 273, Top = 66, Width = 75 };
        f.Controls.AddRange(new Control[] { l, tb, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
    }

    private void ApplyLanguage()
    {
        Text = Loc.T("app.title");
        foreach (var (item, key) in _localized) { item.Text = Loc.T(key); item.ToolTipText = Loc.T(key); }
        foreach (var (code, mi) in _langItems) mi.Checked = code == Loc.Setting;
        foreach (var (code, mi) in _colourItems) mi.Checked = code == ThemeSetting;
        UpdatePathBar();
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
        public string ColorMode { get; set; } = "system";
        public string SkippedVersion { get; set; } = "";
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
                ColorMode = ThemeSetting,
                SkippedVersion = _skippedVersion,
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
        p.StatusChanged += src => { if (src == _active) UpdatePathBar(); };
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
        UpdatePathBar();
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
