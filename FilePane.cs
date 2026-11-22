using System.Collections;
using System.Diagnostics;

namespace ImbotechConsulting.PiExplorer;

internal sealed record Entry(string FullPath, string Name, bool IsDir, long Size, DateTime Modified, bool IsDrive = false);

/// <summary>One folder view: path bar + file list. Many of these sit side by side.</summary>
internal sealed class FilePane : UserControl
{
    public event Action<FilePane, string>? OpenInNewPane;
    public event Action<FilePane>? CloseRequested;
    public event Action<FilePane>? Activated;
    public event Action<FilePane>? CopyToOther;
    public event Action<FilePane>? MoveToOther;
    public event Action? FilesChanged;
    public event Action<FilePane, FilePane>? PaneDropped;   // (dragged pane, pane it was dropped on)

    private const string PaneFormat = "PiExplorer.Pane";
    private static FilePane? _dragging;
    private bool _dropHover;

    private readonly Label _grip = new()
    {
        Text = "", Font = Glyphs.MakeFont(10f), Width = 24, Height = 24, Margin = new Padding(1, 3, 0, 3),
        TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.SizeAll,
    };

    private readonly ToolTip _gripTip = new();
    private readonly TextBox _path = new() { Dock = DockStyle.Fill };
    private readonly Button _back = MakeButton("\uE72B", "Back (Alt+Left)");
    private readonly Button _up = MakeButton("\uE74A", "Up one folder (Backspace)");
    private readonly Button _refresh = MakeButton("\uE72C", "Refresh");
    private readonly Button _newPane = MakeButton("\uE710", "Duplicate this pane");
    private readonly Button _close = MakeButton("\uE711", "Close pane (Ctrl+W)");
    private readonly ListView _list = new();
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft };
    private readonly TableLayoutPanel _bar = new() { Dock = DockStyle.Top, Height = 30, ColumnCount = 7, RowCount = 1 };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    private readonly Stack<string> _history = new();
    private readonly ItemSorter _sorter = new();
    private FileSystemWatcher? _watcher;
    private bool _editing, _historyReady;

    public string CurrentPath { get; private set; } = "";   // "" = This PC (drive list)
    private bool _active, _target;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IsActive { get => _active; set { _active = value; UpdateColors(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IsTarget { get => _target; set { _target = value; UpdateColors(); } }

    public IReadOnlyList<string> SelectedPaths => _list.SelectedItems.Cast<ListViewItem>()
        .Select(i => (Entry)i.Tag!).Where(e => !e.IsDrive).Select(e => e.FullPath).ToList();

    private static Button MakeButton(string text, string tip)
    {
        var b = new Button
        {
            Text = text, Width = 28, Height = 24, Margin = new Padding(1, 2, 1, 2),
            Font = Glyphs.MakeFont(10f), FlatStyle = FlatStyle.System, TabStop = false,
        };
        var tt = new ToolTip();
        tt.SetToolTip(b, tip);
        b.Tag = tt;
        return b;
    }

    public FilePane()
    {
        MinimumSize = new Size(160, 100);

        for (var i = 0; i < 7; i++)
            _bar.ColumnStyles.Add(i == 4 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
        _bar.Controls.Add(_grip, 0, 0);
        _bar.Controls.Add(_back, 1, 0);
        _bar.Controls.Add(_up, 2, 0);
        _bar.Controls.Add(_refresh, 3, 0);
        _path.Margin = new Padding(2, 4, 2, 2);
        _bar.Controls.Add(_path, 4, 0);
        _bar.Controls.Add(_newPane, 5, 0);
        _bar.Controls.Add(_close, 6, 0);
        // tooltips are set in ApplyLanguage()

        // drag the grip onto another pane to reorder
        var gripDown = Point.Empty;
        _grip.MouseDown += (_, e) => { gripDown = e.Location; Activated?.Invoke(this); };
        _grip.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (Math.Abs(e.X - gripDown.X) + Math.Abs(e.Y - gripDown.Y) < 5) return;
            _dragging = this;
            try { _grip.DoDragDrop(new DataObject(PaneFormat, "pane"), DragDropEffects.Move); }
            finally { _dragging = null; }
        };
        foreach (var c in new Control[] { _bar, _status, _path, _grip })
        {
            c.AllowDrop = true;
            c.DragEnter += OnPaneDrag;
            c.DragOver += OnPaneDrag;
            c.DragLeave += (_, _) => SetDropHover(false);
            c.DragDrop += OnPaneDrop;
        }
        _list.DragLeave += (_, _) => SetDropHover(false);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = true;
        _list.LabelEdit = true;
        _list.HideSelection = false;
        _list.AllowDrop = true;
        _list.SmallImageList = IconCache.List;
        _list.ListViewItemSorter = _sorter;
        _list.Columns.Add(Loc.T("col.name"), 220);
        _list.Columns.Add(Loc.T("col.size"), 75, HorizontalAlignment.Right);
        _list.Columns.Add(Loc.T("col.modified"), 125);

        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(_bar);
        UpdateColors();

        // navigation
        _back.Click += (_, _) => GoBack();
        _up.Click += (_, _) => GoUp();
        _refresh.Click += (_, _) => Refresh();
        _newPane.Click += (_, _) => OpenInNewPane?.Invoke(this, CurrentPath);
        _close.Click += (_, _) => CloseRequested?.Invoke(this);
        _path.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (!Navigate(_path.Text)) _path.Text = CurrentPath;
                _list.Focus();
            }
            else if (e.KeyCode == Keys.Escape) { _path.Text = CurrentPath; _list.Focus(); }
        };

        // activation
        _list.GotFocus += (_, _) => Activated?.Invoke(this);
        _list.MouseDown += (_, _) => Activated?.Invoke(this);
        _path.GotFocus += (_, _) => Activated?.Invoke(this);
        _path.Enter += (_, _) => _path.SelectAll();

        // list behaviour
        _list.ColumnClick += (_, e) => { _sorter.Toggle(e.Column); _list.Sort(); };
        _list.SelectedIndexChanged += (_, _) => UpdateStatus();
        _list.ItemActivate += (_, _) => OpenSelected(!AppOptions.FoldersOpenInNewPane);
        _list.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Middle && _list.GetItemAt(e.X, e.Y) is { Tag: Entry { IsDir: true } d })
                OpenInNewPane?.Invoke(this, d.FullPath);
            else if (e.Button == MouseButtons.Right) ShowContextMenu(e.Location);
        };
        _list.KeyDown += OnListKeyDown;
        _list.BeforeLabelEdit += (_, e) =>
        {
            if (_list.Items[e.Item].Tag is Entry { IsDrive: true }) e.CancelEdit = true; else _editing = true;
        };
        _list.AfterLabelEdit += OnAfterLabelEdit;

        // drag & drop (drop onto a pane = copy there; hold Shift to move)
        _list.ItemDrag += (_, _) =>
        {
            var paths = SelectedPaths;
            if (paths.Count > 0)
                _list.DoDragDrop(new DataObject(DataFormats.FileDrop, paths.ToArray()), DragDropEffects.Copy | DragDropEffects.Move);
        };
        _list.DragEnter += OnDrag;
        _list.DragOver += OnDrag;
        _list.DragDrop += OnDrop;

        // auto refresh when the folder changes on disk
        _debounce.Tick += (_, _) => { _debounce.Stop(); if (!_editing) Refresh(); };

        Loc.Changed += OnLanguageChanged;
        ApplyLanguage();
    }

    private void OnLanguageChanged()
    {
        ApplyLanguage();
        Refresh(); // re-reads the folder: "This PC", item counts, "free" etc.
    }

    private void ApplyLanguage()
    {
        void Tip(Button b, string key) => ((ToolTip)b.Tag!).SetToolTip(b, Loc.T(key));
        Tip(_back, "pane.back"); Tip(_up, "pane.up"); Tip(_refresh, "pane.refresh");
        Tip(_newPane, "pane.dup"); Tip(_close, "tb.closepane");
        _gripTip.SetToolTip(_grip, Loc.T("pane.grip"));
        _list.Columns[0].Text = Loc.T("col.name");
        _list.Columns[1].Text = Loc.T("col.size");
        _list.Columns[2].Text = Loc.T("col.modified");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Loc.Changed -= OnLanguageChanged; _watcher?.Dispose(); _debounce.Dispose(); }
        base.Dispose(disposing);
    }

    public void FocusList() => _list.Focus();

    private void OnPaneDrag(object? s, DragEventArgs e)
    {
        var ok = e.Data?.GetDataPresent(PaneFormat) == true && _dragging != null && _dragging != this;
        e.Effect = ok ? DragDropEffects.Move : DragDropEffects.None;
        SetDropHover(ok);
    }

    private void OnPaneDrop(object? s, DragEventArgs e)
    {
        SetDropHover(false);
        if (e.Data?.GetDataPresent(PaneFormat) != true || _dragging == null || _dragging == this) return;
        var dragged = _dragging;
        BeginInvoke(() => PaneDropped?.Invoke(dragged, this));
    }

    private void SetDropHover(bool on) { if (_dropHover != on) { _dropHover = on; UpdateColors(); } }

    private void UpdateColors()
    {
#pragma warning disable WFO5001
        var dark = Application.IsDarkModeEnabled;
#pragma warning restore WFO5001
        _bar.BackColor = _dropHover ? (dark ? Color.FromArgb(52, 96, 40) : Color.FromArgb(196, 232, 170))
            : _active ? (dark ? Color.FromArgb(34, 74, 124) : Color.FromArgb(173, 214, 255))
            : _target ? (dark ? Color.FromArgb(120, 84, 24) : Color.FromArgb(255, 226, 170))
            : SystemColors.Control;
        _status.BackColor = _bar.BackColor;
    }

    // ---------------------------------------------------------------- navigation

    public bool Navigate(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length > 0)
        {
            try
            {
                if (path.Length == 2 && path[1] == ':') path += "\\";
                path = Path.GetFullPath(path);
            }
            catch { return false; }
            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, Loc.T("err.nofolder", path), "PiExplorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }
        var old = CurrentPath;
        if (!LoadDir(path, null)) return false;
        if (old != path && _historyReady) _history.Push(old);
        _historyReady = true;
        return true;
    }

    private void GoBack() { if (_history.Count > 0) LoadDir(_history.Pop(), null); }

    private void GoUp()
    {
        if (CurrentPath.Length == 0) return;
        var parent = Directory.GetParent(CurrentPath);
        Navigate(parent?.FullName ?? "");
    }

    public new void Refresh() => LoadDir(CurrentPath, null);

    private bool LoadDir(string path, string? selectName)
    {
        var entries = new List<Entry>();
        try
        {
            if (path.Length == 0)
            {
                foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    var label = string.IsNullOrEmpty(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel;
                    entries.Add(new Entry(d.Name, $"{d.Name.TrimEnd('\\')}  ({label})", true, d.AvailableFreeSpace, DateTime.MinValue, true));
                }
            }
            else
            {
                var di = new DirectoryInfo(path);
                foreach (var f in di.EnumerateFileSystemInfos())
                {
                    if (!AppOptions.ShowHidden && (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    var dir = (f.Attributes & FileAttributes.Directory) != 0;
                    entries.Add(new Entry(f.FullName, f.Name, dir, dir ? 0 : ((FileInfo)f).Length, f.LastWriteTime));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            if (path != CurrentPath) { MessageBox.Show(this, ex.Message, "PiExplorer", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            // current folder vanished: climb to nearest existing parent
            return LoadDir(Directory.GetParent(path)?.FullName ?? "", null);
        }

        var keep = selectName != null ? new HashSet<string> { selectName }
            : _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var top = _list.TopItem?.Index ?? 0;

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in entries)
        {
            var item = new ListViewItem(e.Name) { Tag = e, ImageIndex = IconCache.Get(e.FullPath, e.IsDir, e.IsDrive) };
            item.SubItems.Add(e.IsDrive ? FormatSize(e.Size) + " " + Loc.T("size.free") : e.IsDir ? "" : FormatSize(e.Size));
            item.SubItems.Add(e.IsDrive ? "" : e.Modified.ToString("yyyy-MM-dd HH:mm"));
            item.Selected = keep.Contains(e.Name);
            _list.Items.Add(item);
        }
        _list.Sort();
        if (path == CurrentPath && top < _list.Items.Count) _list.TopItem = _list.Items[top];
        _list.EndUpdate();

        CurrentPath = path;
        _path.Text = path.Length == 0 ? Loc.T("thispc") : path;
        SetupWatcher(path);
        UpdateStatus();
        return true;
    }

    private void SetupWatcher(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (path.Length == 0) return;
        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            void Poke(object? s, EventArgs e)
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
            }
            _watcher.Created += Poke; _watcher.Deleted += Poke; _watcher.Changed += Poke; _watcher.Renamed += Poke;
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* not watchable: manual refresh still works */ }
    }

    private void UpdateStatus()
    {
        var sel = _list.SelectedItems.Count;
        _status.Text = "  " + Loc.T("status.items", _list.Items.Count) + (sel > 0 ? "   |   " + Loc.T("status.sel", sel) : "");
        StatusChanged?.Invoke(this);
    }

    public event Action<FilePane>? StatusChanged;

    /// <summary>Full path of the single selected item, else the folder shown ("" = This PC).</summary>
    public string FullPathInfo => SelectedPaths is { Count: 1 } one ? one[0] : CurrentPath;

    private static string FormatSize(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.#} {u[i]}";
    }

    // ---------------------------------------------------------------- actions

    private void OpenSelected(bool inSamePane)
    {
        if (_list.SelectedItems.Count == 0) return;
        foreach (var it in _list.SelectedItems.Cast<ListViewItem>().ToList())
        {
            var e = (Entry)it.Tag!;
            if (e.IsDir)
            {
                if (inSamePane) { Navigate(e.FullPath); return; }
                OpenInNewPane?.Invoke(this, e.FullPath);
            }
            else
            {
                try { Process.Start(new ProcessStartInfo(e.FullPath) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "PiExplorer", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }
    }

    public void DeleteSelected()
    {
        var paths = SelectedPaths;
        if (paths.Count == 0) return;
        ShellOps.Delete(paths, FindForm()?.Handle ?? IntPtr.Zero);
        FilesChanged?.Invoke();
    }

    public void CreateFolder()
    {
        if (CurrentPath.Length == 0) return;
        var name = Loc.T("newfolder");
        for (var n = 2; Directory.Exists(Path.Combine(CurrentPath, name)) || File.Exists(Path.Combine(CurrentPath, name)); n++)
            name = $"{Loc.T("newfolder")} ({n})";
        try { Directory.CreateDirectory(Path.Combine(CurrentPath, name)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "PiExplorer"); return; }
        LoadDir(CurrentPath, name);
        _list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Text == name)?.BeginEdit();
    }

    private void OnListKeyDown(object? s, KeyEventArgs e)
    {
        if (_editing) return;
        switch (e.KeyCode)
        {
            case Keys.Enter: OpenSelected(!AppOptions.FoldersOpenInNewPane); e.Handled = true; break;
            case Keys.Back: GoUp(); e.Handled = true; break;
            case Keys.Left when e.Alt: GoBack(); e.Handled = true; break;
            case Keys.Delete: DeleteSelected(); e.Handled = true; break;
            case Keys.F2: _list.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.BeginEdit(); e.Handled = true; break;
            case Keys.A when e.Control: foreach (ListViewItem i in _list.Items) i.Selected = true; e.Handled = true; break;
            case Keys.L when e.Control: _path.Focus(); e.Handled = true; break;
        }
    }

    private void OnAfterLabelEdit(object? s, LabelEditEventArgs e)
    {
        _editing = false;
        var entry = (Entry)_list.Items[e.Item].Tag!;
        var name = e.Label?.Trim();
        e.CancelEdit = true; // we reload the list ourselves
        if (string.IsNullOrEmpty(name) || name == entry.Name) return;
        try
        {
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException(Loc.T("err.badname"));
            var dest = Path.Combine(CurrentPath, name);
            if (entry.IsDir) Directory.Move(entry.FullPath, dest); else File.Move(entry.FullPath, dest);
            BeginInvoke(() => LoadDir(CurrentPath, name));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Loc.T("rename.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void ShowContextMenu(Point at)
    {
        var hit = _list.GetItemAt(at.X, at.Y);
        if (hit != null && !hit.Selected) { _list.SelectedItems.Clear(); hit.Selected = true; }
        var m = new ContextMenuStrip();
        if (_list.SelectedItems.Count > 0)
        {
            m.Items.Add(Loc.T("ctx.open"), null, (_, _) => OpenSelected(false));
            m.Items.Add(Loc.T("ctx.openhere"), null, (_, _) => OpenSelected(true));
            if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is Entry { IsDir: true } d)
                m.Items.Add(Loc.T("ctx.opennew"), null, (_, _) => OpenInNewPane?.Invoke(this, d.FullPath));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Loc.T("ctx.copy"), null, (_, _) => CopyToOther?.Invoke(this));
            m.Items.Add(Loc.T("ctx.move"), null, (_, _) => MoveToOther?.Invoke(this));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Loc.T("ctx.rename"), null, (_, _) => _list.SelectedItems[0].BeginEdit());
            m.Items.Add(Loc.T("ctx.delete"), null, (_, _) => DeleteSelected());
            m.Items.Add(new ToolStripSeparator());
        }
        m.Items.Add(Loc.T("ctx.newfolder"), null, (_, _) => CreateFolder());
        m.Items.Add(Loc.T("pane.refresh"), null, (_, _) => Refresh());
        m.Show(_list, at);
    }

    // ---------------------------------------------------------------- drag & drop

    private void OnDrag(object? s, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(PaneFormat) == true) { OnPaneDrag(s, e); return; }
        if (CurrentPath.Length == 0 || e.Data?.GetDataPresent(DataFormats.FileDrop) != true) { e.Effect = DragDropEffects.None; return; }
        e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy; // Shift = move
    }

    private void OnDrop(object? s, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(PaneFormat) == true) { OnPaneDrop(s, e); return; }
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] src || CurrentPath.Length == 0) return;
        var pt = _list.PointToClient(new Point(e.X, e.Y));
        var dest = _list.GetItemAt(pt.X, pt.Y)?.Tag is Entry { IsDir: true, IsDrive: false } d ? d.FullPath : CurrentPath;
        var move = e.Effect == DragDropEffects.Move;
        var valid = src.Where(p => !SamePath(Path.GetDirectoryName(p), dest) && !SamePath(p, dest) && !IsInside(dest, p)).ToList();
        if (valid.Count == 0) return;
        BeginInvoke(() =>
        {
            if (move) ShellOps.Move(valid, dest, Handle); else ShellOps.Copy(valid, dest, Handle);
            FilesChanged?.Invoke();
        });
    }

    private static bool SamePath(string? a, string? b) =>
        a != null && b != null && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string child, string parent) =>
        child.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- sorting

    private sealed class ItemSorter : IComparer
    {
        private int _col;
        private bool _asc = true;
        public void Toggle(int col) { if (col == _col) _asc = !_asc; else { _col = col; _asc = true; } }
        public int Compare(object? x, object? y)
        {
            var a = (Entry)((ListViewItem)x!).Tag!; var b = (Entry)((ListViewItem)y!).Tag!;
            if (a.IsDrive || b.IsDrive) return string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);
            if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1; // folders always first
            var r = _col switch
            {
                1 => a.Size.CompareTo(b.Size),
                2 => a.Modified.CompareTo(b.Modified),
                _ => 0,
            };
            if (r == 0) r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return _asc ? r : -r;
        }
    }
}
