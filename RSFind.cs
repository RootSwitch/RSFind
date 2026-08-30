// RSFind - a manual text search over a folder.
//
// The window is one screen: where to look, what to find, how to narrow it, and
// the results. No index, no service, no background watcher. It runs when you
// run it and it stops when you close it, which is the entire point - it is the
// tool you reach for precisely because the indexer did not have the answer.
//
// C# 5 only (in-box csc).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RSFind
{
    public class MainForm : Form
    {
        Settings _settings;

        TextBox _folder, _query, _include, _exclude, _replace;
        InputHost _folderHost, _queryHost, _includeHost, _excludeHost, _replaceHost;
        ThemedButton _browse, _find, _cancel, _menuButton, _preview;
        ThemedCheck _matchCase, _wholeWord, _regex, _subfolders, _skipBinary, _stripAnsi;
        ThemedCheck _preserveCase;
        Label _replaceLabel;
        SpinBox _maxMb, _before, _after;
        Label _folderLabel, _queryLabel, _maskLabel, _excludeLabel, _mbLabel, _contextLabel, _summary;
        Label _sortLabel;
        ThemedDropdown _sortBy, _sortOrder;

        // Same order as the ResultSort enum, so the dropdown index is the key
        // and neither list can be reordered without the other noticing.
        static readonly string[] SortLabels =
            { "Name", "Modified", "Created", "Size", "Hit Count" };
        Panel _top;
        Panel _resultsHost, _filterBar;
        TextBox _filter;
        InputHost _filterHost;
        Label _filterLabel, _filterCount;
        ResultsView _results;
        ContextMenuStrip _menu;
        ToolStripMenuItem _explorerItem;

        CancellationTokenSource _cts;
        System.Windows.Forms.Timer _pump;
        readonly List<FileHits> _pending = new List<FileHits>();
        readonly List<string> _errors = new List<string>();
        readonly object _pendingLock = new object();
        SearchProgress _progress;
        string _activeQuery = "";
        // The matcher that produced the results on screen, kept so a Replace
        // uses the pattern the hits came from rather than whatever is in the
        // search box by the time the person gets round to replacing.
        Matcher _activeMatcher;
        readonly List<FileHits> _found = new List<FileHits>();
        bool _running;
        IntPtr _iconHandle;

        const string RegistryKeyName = "RSFind";
        const string ExplorerVerbLabel = "Find Text with RSFind";

        public MainForm(string startFolder)
        {
            _settings = Settings.Load();
            Th.Set(_settings.Theme);

            Text = "RSFind";
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            MinimumSize = new Size(Dpi.S(760), Dpi.S(420));
            ClientSize = new Size(
                _settings.WindowWidth > 0 ? _settings.WindowWidth : Dpi.S(1000),
                _settings.WindowHeight > 0 ? _settings.WindowHeight : Dpi.S(620));
            if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;

            BuildControls();
            BuildMenu();
            ApplyTheme();
            LoadSettingsIntoControls();

            string folder = startFolder;
            if (string.IsNullOrEmpty(folder)) folder = _settings.LastFolder;
            if (!string.IsNullOrEmpty(folder)) _folder.Text = folder;

            // Also at startup, not only after a replace: a folder that grew
            // before there was a retention rule would otherwise sit there until
            // the next replace happened to trim it.
            Replacer.PruneUndo(UndoRoot, Replacer.MaxUndoRuns);

            _pump = new System.Windows.Forms.Timer();
            _pump.Interval = 150;
            _pump.Tick += OnPump;

            // The window sees keys before the focused control, so Ctrl+F works
            // wherever the caret happens to be.
            KeyPreview = true;
            KeyDown += OnFormKeyDown;

            Resize += delegate { LayoutTop(); };
            FormClosing += OnFormClosing;
            Shown += delegate { _query.Focus(); };
        }

        // ---- construction --------------------------------------------------

        void BuildControls()
        {
            _top = new Panel();
            _top.Dock = DockStyle.Top;
            _top.Height = Dpi.S(150);
            Controls.Add(_top);

            _results = new ResultsView();
            _results.Dock = DockStyle.Fill;
            _results.OpenRequested += OnOpenRequested;
            _results.FindRequested += delegate { ShowFilter(); };
            _results.SortChanged += OnSortChanged;

            BuildFilterBar();

            // The filter bar belongs to the results, not to the search chrome,
            // so it lives in the same container and appears directly above
            // them. Within a container the last child docks first, so the bar
            // is added after the list it sits on top of.
            _resultsHost = new Panel();
            _resultsHost.Dock = DockStyle.Fill;
            _resultsHost.Controls.Add(_results);
            _resultsHost.Controls.Add(_filterBar);
            Controls.Add(_resultsHost);
            Controls.SetChildIndex(_resultsHost, 0);   // Fill must sit under the docked Top

            _folderLabel = NewLabel("Search in");
            _folder = NewBox();
            _folder.KeyDown += OnEnterStarts;
            _folderHost = new InputHost(_folder, Dpi.S(6), Dpi.S(4));

            _browse = NewButton("Browse", false);
            _browse.Click += OnBrowse;

            _menuButton = NewButton("Menu", false);
            _menuButton.Click += delegate
            {
                MenuTheme.Apply(_menu);
                _menu.Show(_menuButton, new Point(0, _menuButton.Height));
            };

            _queryLabel = NewLabel("Find what");
            _query = NewBox();
            _query.KeyDown += OnEnterStarts;
            _queryHost = new InputHost(_query, Dpi.S(6), Dpi.S(4));

            _find = NewButton("Find", true);
            _find.Click += delegate { StartSearch(); };

            _cancel = NewButton("Cancel", false);
            _cancel.TakesFocus = false;
            _cancel.Enabled = false;
            _cancel.Click += delegate { if (_cts != null) _cts.Cancel(); };

            _replaceLabel = NewLabel("Replace with");
            _replace = NewBox();
            _replaceHost = new InputHost(_replace, Dpi.S(6), Dpi.S(4));

            _preserveCase = NewCheck("Preserve case");
            _preserveCase.Checked = true;

            // "Replace..." rather than "Preview".
            //
            // Someone who has just typed a replacement goes looking for the
            // verb they are about to perform, and a button labeled Preview
            // reads as an optional extra sitting beside the real one - so they
            // hunt for a Replace button that does not exist. The ellipsis is
            // the standard promise that it opens something rather than acting,
            // and what it opens is unmistakably a preview: the window is
            // titled Preview Replace and its button says Apply.
            _preview = NewButton("Replace...", false);
            _preview.Enabled = false;
            _preview.Click += delegate { PreviewReplace(); };

            ToolTip tips = new ToolTip();
            tips.SetToolTip(_preview, "Shows exactly what would change before anything is written");

            _matchCase = NewCheck("Match case");
            _wholeWord = NewCheck("Match whole word");
            _regex = NewCheck("Use regex");
            _subfolders = NewCheck("Include subfolders");
            _skipBinary = NewCheck("Exclude binary files");
            _stripAnsi = NewCheck("Strip ANSI escapes");

            _maskLabel = NewLabel("File mask");
            _include = NewBox();
            _include.KeyDown += OnEnterStarts;
            _includeHost = new InputHost(_include, Dpi.S(6), Dpi.S(4));

            _excludeLabel = NewLabel("Exclude");
            _exclude = NewBox();
            _exclude.KeyDown += OnEnterStarts;
            _excludeHost = new InputHost(_exclude, Dpi.S(6), Dpi.S(4));

            _mbLabel = NewLabel("Skip over");
            _maxMb = new SpinBox(0, 4096, 50);
            _top.Controls.Add(_maxMb);

            _contextLabel = NewLabel("Context lines");
            _before = new SpinBox(0, 20, 0);
            _after = new SpinBox(0, 20, 0);
            _top.Controls.Add(_before);
            _top.Controls.Add(_after);

            _sortLabel = NewLabel("Sort by");
            _sortBy = new ThemedDropdown();
            _sortBy.SetItems(SortLabels);
            _sortBy.SelectedIndexChanged += OnSortPicked;
            _sortOrder = new ThemedDropdown();
            _sortOrder.SetItems(new string[] { "Ascending", "Descending" });
            _sortOrder.SelectedIndexChanged += OnSortPicked;
            _top.Controls.Add(_sortBy);
            _top.Controls.Add(_sortOrder);

            _summary = NewLabel("Ready.");
            _summary.AutoSize = false;

            _top.Controls.Add(_folderHost);
            _top.Controls.Add(_queryHost);
            _top.Controls.Add(_replaceHost);
            _top.Controls.Add(_includeHost);
            _top.Controls.Add(_excludeHost);
        }

        // Ctrl+F narrows what is on screen rather than stepping through it.
        //
        // Find-next would match the muscle memory, but not the job. The
        // question that raises this bar is "which of these thousand hits was
        // on LAB4", and a host appears on dozens of rows - so find-next means
        // pressing it dozens of times, while a filter answers in one go and
        // leaves a short list to double-click. Escape puts everything back.
        void BuildFilterBar()
        {
            _filterBar = new Panel();
            _filterBar.Dock = DockStyle.Top;
            _filterBar.Height = Dpi.S(34);
            _filterBar.Visible = false;
            _filterBar.BackColor = Th.T.Panel;

            _filterLabel = new Label();
            _filterLabel.Text = "Find in results";
            _filterLabel.AutoSize = true;
            _filterLabel.BackColor = Th.T.Panel;
            _filterLabel.ForeColor = Th.T.Txt;
            _filterBar.Controls.Add(_filterLabel);

            _filter = new TextBox();
            _filter.BorderStyle = BorderStyle.None;
            _filter.TextChanged += delegate
            {
                _results.SetFilter(_filter.Text);
                UpdateFilterCount();
            };
            _filter.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { HideFilter(); e.SuppressKeyPress = true; }
                // Enter moves to the list, because the next thing anyone does
                // after narrowing is open one of the rows.
                else if (e.KeyCode == Keys.Enter)
                {
                    _results.Focus();
                    _results.SelectFirst();
                    e.SuppressKeyPress = true;
                }
            };
            _filterHost = new InputHost(_filter, Dpi.S(6), Dpi.S(4));
            _filterBar.Controls.Add(_filterHost);

            _filterCount = new Label();
            _filterCount.AutoSize = false;
            _filterCount.BackColor = Th.T.Panel;
            _filterCount.ForeColor = Th.T.TxtDim;
            _filterCount.TextAlign = ContentAlignment.MiddleLeft;
            _filterBar.Controls.Add(_filterCount);

            _filterBar.Resize += delegate { LayoutFilterBar(); };
        }

        void LayoutFilterBar()
        {
            if (_filterBar == null) return;
            int pad = Dpi.S(10);
            int rowH = Dpi.S(24);
            int y = (_filterBar.Height - rowH) / 2;
            _filterLabel.SetBounds(pad, y + (rowH - _filterLabel.Height) / 2,
                                   _filterLabel.Width, _filterLabel.Height);
            int x = pad + _filterLabel.Width + Dpi.S(10);
            int countW = Dpi.S(260);
            int boxW = Math.Max(Dpi.S(80),
                _filterBar.ClientSize.Width - pad - countW - Dpi.S(10) - x);
            _filterHost.SetBounds(x, y, boxW, rowH);
            _filterCount.SetBounds(_filterHost.Right + Dpi.S(10), y, countW, rowH);
        }

        void ShowFilter()
        {
            if (_results.FileCount == 0)
            {
                SetSummary("Search something first, then Ctrl+F narrows the results.", true);
                return;
            }
            _filterBar.Visible = true;
            LayoutFilterBar();
            UpdateFilterCount();
            _filter.Focus();
            _filter.SelectAll();
        }

        void HideFilter()
        {
            if (!_filterBar.Visible) return;
            // Clearing on the way out, because a hidden filter still hiding
            // results is the same trap as a scroll position past the end.
            _filter.Text = "";
            _results.SetFilter("");
            _filterBar.Visible = false;
            _results.Focus();
        }

        void UpdateFilterCount()
        {
            if (_filterCount == null) return;
            int shown = _results.VisibleHits;
            int total = _results.TotalHits;
            _filterCount.Text = _filter.Text.Length == 0
                ? "Escape closes this"
                : "Showing " + shown.ToString("N0", CultureInfo.InvariantCulture)
                  + " of " + total.ToString("N0", CultureInfo.InvariantCulture)
                  + (total == 1 ? " hit" : " hits");
            _filterCount.ForeColor = _filter.Text.Length > 0 && shown == 0 ? Th.T.Warn : Th.T.TxtDim;
        }

        Label NewLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.TextAlign = ContentAlignment.MiddleLeft;
            _top.Controls.Add(l);
            return l;
        }

        TextBox NewBox()
        {
            TextBox b = new TextBox();
            b.BorderStyle = BorderStyle.None;
            return b;
        }

        ThemedButton NewButton(string text, bool primary)
        {
            ThemedButton b = new ThemedButton();
            b.Text = text;
            b.Primary = primary;
            _top.Controls.Add(b);
            return b;
        }

        ThemedCheck NewCheck(string text)
        {
            ThemedCheck c = new ThemedCheck();
            c.Text = text;
            _top.Controls.Add(c);
            return c;
        }

        void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.Renderer = new ToolStripProfessionalRenderer(new ThemeColorTable());
            _menu.ShowImageMargin = false;

            ToolStripMenuItem themes = new ToolStripMenuItem("Themes");
            ToolStripMenuItem group = null;
            foreach (Theme t in Themes.All)
            {
                ToolStripMenuItem item = new ToolStripMenuItem(t.Label);
                item.Tag = t.Key;
                item.Click += OnThemePicked;
                if (t.Group == null) themes.DropDownItems.Add(item);
                else
                {
                    if (group == null || group.Text != t.Group)
                    {
                        group = new ToolStripMenuItem(t.Group);
                        themes.DropDownItems.Add(group);
                    }
                    group.DropDownItems.Add(item);
                }
            }
            _menu.Items.Add(themes);

            _explorerItem = new ToolStripMenuItem("Add to Explorer Right-Click Menu");
            _explorerItem.Click += OnToggleExplorerEntry;
            _menu.Items.Add(_explorerItem);

            ToolStripMenuItem editor = new ToolStripMenuItem("Editor Command...");
            editor.Click += OnEditorCommand;
            _menu.Items.Add(editor);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem undo = new ToolStripMenuItem("Undo Last Replace");
            undo.Click += OnUndoReplace;
            _menu.Items.Add(undo);

            ToolStripMenuItem export = new ToolStripMenuItem("Export Results...");
            export.Click += OnExport;
            _menu.Items.Add(export);

            ToolStripMenuItem about = new ToolStripMenuItem("About RSFind");
            about.Click += OnAbout;
            _menu.Items.Add(about);

            _menu.Opening += delegate
            {
                MarkActiveTheme(themes);
                _explorerItem.Checked = ExplorerEntryPresent();
            };
        }

        // ---- layout ----------------------------------------------------------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            OsChrome.ApplyTitleBar(this);
            LayoutTop();
        }

        void LayoutTop()
        {
            if (_top == null || _folderHost == null) return;
            int pad = Dpi.S(10);
            int gap = Dpi.S(8);
            int rowH = Dpi.S(26);
            int labelW = Dpi.S(74);
            int buttonW = Dpi.S(84);
            int y = pad;

            int right = _top.ClientSize.Width - pad;

            // Row 1: the folder, Browse, and the menu.
            Place(_folderLabel, pad, y, rowH);
            _menuButton.SetBounds(right - buttonW, y, buttonW, rowH);
            _browse.SetBounds(_menuButton.Left - gap - buttonW, y, buttonW, rowH);
            _folderHost.SetBounds(pad + labelW, y,
                                  Math.Max(Dpi.S(80), _browse.Left - gap - pad - labelW), rowH);
            y += rowH + gap;

            // Row 2: the query, Find, and Cancel.
            Place(_queryLabel, pad, y, rowH);
            _cancel.SetBounds(right - buttonW, y, buttonW, rowH);
            _find.SetBounds(_cancel.Left - gap - buttonW, y, buttonW, rowH);
            _queryHost.SetBounds(pad + labelW, y,
                                 Math.Max(Dpi.S(80), _find.Left - gap - pad - labelW), rowH);
            y += rowH + gap;

            // Row 3: replace. Always visible rather than behind a toggle - a
            // destructive control that appears and disappears is harder to
            // reason about than one that is simply inert until a search has
            // produced something to act on.
            Place(_replaceLabel, pad, y, rowH);
            // Directly under Find, not under Cancel. These are the two verbs
            // the window offers and they belong in one column; Cancel belongs
            // to the search that is running, and lining Replace up beneath it
            // put the row's action in the row above's afterthought column.
            _preview.SetBounds(_find.Left, y, buttonW, rowH);
            _preserveCase.SizeToText();
            _preserveCase.SetBounds(_preview.Left - gap - _preserveCase.Width, y,
                                    _preserveCase.Width, rowH);
            _replaceHost.SetBounds(pad + labelW, y,
                Math.Max(Dpi.S(80), _preserveCase.Left - gap - pad - labelW), rowH);
            y += rowH + gap;

            // Row 3: the toggles, wrapping if the window is narrow.
            int x = pad + labelW;
            int rowTop = y;
            ThemedCheck[] checks = new ThemedCheck[]
                { _matchCase, _wholeWord, _regex, _subfolders, _skipBinary, _stripAnsi };
            foreach (ThemedCheck c in checks)
            {
                c.SizeToText();
                if (x + c.Width > right && x > pad + labelW)
                {
                    x = pad + labelW;
                    rowTop += rowH;
                }
                c.SetBounds(x, rowTop + (rowH - c.Height) / 2, c.Width, c.Height);
                x += c.Width + Dpi.S(14);
            }
            y = rowTop + rowH + gap;

            // Row 4: the narrowing fields.
            Place(_maskLabel, pad, y, rowH);
            int fieldW = Dpi.S(150);
            _includeHost.SetBounds(pad + labelW, y, fieldW, rowH);

            int ex = _includeHost.Right + Dpi.S(16);
            Place(_excludeLabel, ex, y, rowH);
            _excludeHost.SetBounds(ex + Dpi.S(56), y, fieldW, rowH);

            int mb = _excludeHost.Right + Dpi.S(16);
            Place(_mbLabel, mb, y, rowH);
            _maxMb.SetBounds(mb + Dpi.S(62), y, Dpi.S(72), rowH);

            int cx = _maxMb.Right + Dpi.S(16);
            Place(_contextLabel, cx, y, rowH);
            _before.SetBounds(cx + Dpi.S(82), y, Dpi.S(60), rowH);
            _after.SetBounds(_before.Right + Dpi.S(6), y, Dpi.S(60), rowH);

            // The sort controls, on the end of the same row when there is room
            // and on a row of their own when there is not. Row 4 already runs
            // wider than the minimum window, so "there is room" is a real
            // question rather than a formality.
            int sortW = Dpi.S(104);
            int orderW = Dpi.S(116);
            int sortRow = y;
            int sx = _after.Right + Dpi.S(16);
            if (sx + _sortLabel.Width + Dpi.S(8) + sortW + Dpi.S(6) + orderW > right)
            {
                sortRow += rowH + gap;
                sx = pad + labelW;
            }
            Place(_sortLabel, sx, sortRow, rowH);
            _sortBy.SetBounds(sx + _sortLabel.Width + Dpi.S(8), sortRow, sortW, rowH);
            _sortOrder.SetBounds(_sortBy.Right + Dpi.S(6), sortRow, orderW, rowH);
            y = sortRow + rowH + gap;

            // Row 5: the summary line, which is the only place the caps and
            // the cancellations get to speak.
            _summary.SetBounds(pad, y, Math.Max(Dpi.S(100), right - pad), rowH);
            y += rowH + Dpi.S(4);

            _top.Height = y;
        }

        static void Place(Label l, int x, int rowTop, int rowH)
        {
            l.SetBounds(x, rowTop + (rowH - l.Height) / 2, l.Width, l.Height);
        }

        // ---- theme -----------------------------------------------------------

        void ApplyTheme()
        {
            Theme t = Th.T;
            BackColor = t.Panel;
            ForeColor = t.Txt;
            _top.BackColor = t.Panel;
            foreach (Control c in _top.Controls)
            {
                Label l = c as Label;
                if (l != null) { l.BackColor = t.Panel; l.ForeColor = t.Txt; }
                ThemedCheck ck = c as ThemedCheck;
                if (ck != null) ck.BackColor = t.Panel;
            }
            _summary.ForeColor = t.TxtDim;

            if (_filterBar != null)
            {
                _filterBar.BackColor = t.Panel;
                _filterLabel.BackColor = t.Panel;
                _filterLabel.ForeColor = t.Txt;
                _filterCount.BackColor = t.Panel;
                UpdateFilterCount();
            }

            IntPtr old = _iconHandle;
            Icon = Brand.CreateIcon(Dpi.S(32), out _iconHandle);
            if (old != IntPtr.Zero) Native.DestroyIcon(old);

            OsChrome.ApplyTitleBar(this);
            Invalidate(true);
        }

        void OnThemePicked(object sender, EventArgs e)
        {
            ToolStripMenuItem item = (ToolStripMenuItem)sender;
            Th.Set((string)item.Tag);
            ApplyTheme();
            SaveSettings();
        }

        void MarkActiveTheme(ToolStripMenuItem themes)
        {
            foreach (ToolStripItem raw in themes.DropDownItems)
            {
                ToolStripMenuItem item = raw as ToolStripMenuItem;
                if (item == null) continue;
                if (item.HasDropDownItems)
                {
                    foreach (ToolStripItem sub in item.DropDownItems)
                    {
                        ToolStripMenuItem s = sub as ToolStripMenuItem;
                        if (s != null) s.Checked = Equals(s.Tag, Th.T.Key);
                    }
                }
                else item.Checked = Equals(item.Tag, Th.T.Key);
            }
        }

        // ---- settings ---------------------------------------------------------

        void LoadSettingsIntoControls()
        {
            _include.Text = _settings.IncludeMasks;
            _exclude.Text = _settings.ExcludeMasks;
            _matchCase.Checked = _settings.MatchCase;
            _wholeWord.Checked = _settings.WholeWord;
            _regex.Checked = _settings.UseRegex;
            _subfolders.Checked = _settings.IncludeSubfolders;
            _skipBinary.Checked = _settings.ExcludeBinary;
            _stripAnsi.Checked = _settings.StripAnsi;
            _maxMb.Value = _settings.MaxFileMegabytes;
            _before.Value = _settings.ContextBefore;
            _after.Value = _settings.ContextAfter;
            _results.SetSort(ViewRules.ParseSort(_settings.SortKey, ResultSort.Name),
                             _settings.SortDescending, false);
            ShowSortInControls();
        }

        // The dropdowns and the results right-click menu are two ways into the
        // same setting, so whichever one is used, the other has to follow. The
        // menu marks itself when it opens; these have to be told.
        void ShowSortInControls()
        {
            _sortBy.SetSelectedIndex((int)_results.SortKey, false);
            _sortOrder.SetSelectedIndex(_results.SortDescending ? 1 : 0, false);
        }

        void OnSortPicked(object sender, EventArgs e)
        {
            _results.SetSort((ResultSort)_sortBy.SelectedIndex,
                             _sortOrder.SelectedIndex == 1, true);
        }

        void OnSortChanged(object sender, EventArgs e)
        {
            ShowSortInControls();
            SaveSettings();
        }

        void SaveSettings()
        {
            _settings.Theme = Th.T.Key;
            _settings.LastFolder = _folder.Text.Trim();
            _settings.IncludeMasks = _include.Text.Trim();
            _settings.ExcludeMasks = _exclude.Text.Trim();
            _settings.MatchCase = _matchCase.Checked;
            _settings.WholeWord = _wholeWord.Checked;
            _settings.UseRegex = _regex.Checked;
            _settings.IncludeSubfolders = _subfolders.Checked;
            _settings.ExcludeBinary = _skipBinary.Checked;
            _settings.StripAnsi = _stripAnsi.Checked;
            _settings.MaxFileMegabytes = _maxMb.Value;
            _settings.ContextBefore = _before.Value;
            _settings.ContextAfter = _after.Value;
            _settings.SortKey = ViewRules.SortName(_results.SortKey);
            _settings.SortDescending = _results.SortDescending;
            _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
            if (WindowState == FormWindowState.Normal)
            {
                _settings.WindowWidth = ClientSize.Width;
                _settings.WindowHeight = ClientSize.Height;
            }
            _settings.Save();
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_cts != null) _cts.Cancel();
            SaveSettings();
            if (_iconHandle != IntPtr.Zero) Native.DestroyIcon(_iconHandle);
        }

        // ---- the search --------------------------------------------------------

        void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.F)
            {
                ShowFilter();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Escape && _filterBar.Visible)
            {
                HideFilter();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        void OnEnterStarts(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;   // or the text box beeps
            StartSearch();
        }

        void StartSearch()
        {
            if (_running) return;

            string root = _folder.Text.Trim();
            if (root.Length == 0 || !Directory.Exists(root))
            {
                SetSummary("That folder does not exist.", true);
                _folder.Focus();
                return;
            }
            if (_query.Text.Length == 0)
            {
                SetSummary("Type something to search for.", true);
                _query.Focus();
                return;
            }

            SearchOptions o = new SearchOptions();
            o.Root = root;
            o.Query = _query.Text;
            o.MatchCase = _matchCase.Checked;
            o.WholeWord = _wholeWord.Checked;
            o.UseRegex = _regex.Checked;
            o.IncludeSubfolders = _subfolders.Checked;
            o.IncludeMasks = _include.Text;
            o.ExcludeMasks = _exclude.Text;
            o.ExcludeBinary = _skipBinary.Checked;
            o.StripAnsi = _stripAnsi.Checked;
            o.MaxFileMegabytes = _maxMb.Value;
            o.ContextBefore = _before.Value;
            o.ContextAfter = _after.Value;

            SearchEngine engine;
            try
            {
                engine = new SearchEngine(o);
            }
            catch (PatternError ex)
            {
                // A half-typed regex is the normal state of a search box, so it
                // reports itself on the summary line rather than in a dialog.
                SetSummary("Bad pattern: " + ex.Message, true);
                _query.Focus();
                return;
            }

            // A filter from the previous result set would hide the new one,
            // which is the same "it found nothing" trap as a stale scroll
            // position. A new search starts unfiltered.
            HideFilter();

            _results.ClearResults();
            lock (_pendingLock) { _pending.Clear(); }
            _errors.Clear();
            _found.Clear();
            _preview.Enabled = false;
            _progress = null;
            _activeQuery = o.Query;
            _activeMatcher = new Matcher(o.Query, o.MatchCase, o.WholeWord, o.UseRegex);
            _running = true;
            _find.Enabled = false;
            _cancel.Enabled = true;
            SetSummary("Searching...", false);
            SaveSettings();

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _pump.Start();

            Task.Factory.StartNew(delegate
            {
                try
                {
                    RunSearch(engine, token);
                }
                catch (Exception ex)
                {
                    // The window leaves the running state whatever happened.
                    //
                    // Without this the task faults, nobody observes it - .NET
                    // swallows an unobserved Task exception - _progress never
                    // reaches a finished snapshot, and the pump runs forever
                    // with Find disabled and Cancel useless, because the task
                    // it would cancel is already dead. Only killing the process
                    // recovered it.
                    SearchProgress failed = new SearchProgress();
                    failed.Finished = true;
                    failed.Fault = ex.GetType().Name + ": " + ex.Message;
                    _progress = failed;
                }
            });
        }

        void RunSearch(SearchEngine engine, CancellationToken token)
        {
            SearchProgress final = engine.Run(token,
                delegate(FileHits fh) { lock (_pendingLock) { _pending.Add(fh); } },
                delegate(SearchProgress p) { _progress = p; },
                delegate(string path, string message)
                {
                    lock (_pendingLock)
                    {
                        // One line per unreadable place, capped: a scan
                        // pointed at a system folder can produce thousands
                        // and none of them are worth the memory.
                        if (_errors.Count < 200) _errors.Add(path + ": " + message);
                    }
                });
            _progress = final;
        }

        // Drains what the worker produced onto the UI thread. One timer rather
        // than an Invoke per file: the engine reports from every core at once,
        // and marshalling each one separately makes the scan slower than the
        // disk it is reading.
        void OnPump(object sender, EventArgs e)
        {
            List<FileHits> batch = null;
            lock (_pendingLock)
            {
                if (_pending.Count > 0)
                {
                    batch = new List<FileHits>(_pending);
                    _pending.Clear();
                }
            }
            if (batch != null)
            {
                _results.AddFiles(batch);
                _found.AddRange(batch);
            }

            SearchProgress p = _progress;
            if (p == null) return;

            SetSummary(Describe(p), p.Fault != null);
            if (!p.Finished) return;

            _pump.Stop();
            _running = false;
            _find.Enabled = true;
            _cancel.Enabled = false;

            // One last drain: a file that arrived between the batch above and
            // the engine finishing would otherwise never be shown.
            lock (_pendingLock)
            {
                if (_pending.Count > 0)
                {
                    List<FileHits> last = new List<FileHits>(_pending);
                    _results.AddFiles(last);
                    _found.AddRange(last);
                    _pending.Clear();
                }
            }
            // The order is applied once, here, rather than on every batch.
            // Files arrive in whatever sequence the workers finish them, and
            // re-sorting mid-scan would shuffle rows under whoever is reading
            // the ones already on screen. Completion is a boundary they can
            // feel, because the summary line stops moving at the same moment.
            _results.ApplySort();

            SetSummary(Describe(p), false);
            _preview.Enabled = _found.Count > 0;
        }

        string Describe(SearchProgress p)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Search \"").Append(_activeQuery).Append("\" (");
            sb.Append(p.Hits.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(p.Hits == 1 ? " hit in " : " hits in ");
            sb.Append(p.FilesMatched.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(p.FilesMatched == 1 ? " file of " : " files of ");
            sb.Append(p.FilesScanned.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(" searched)");

            // The timing belongs to the count, so it goes immediately after it.
            // Everything below is a caveat, and caveats read as sentences.
            if (p.Finished)
            {
                sb.Append(" in ").Append(((int)p.Elapsed.TotalMilliseconds)
                    .ToString("N0", CultureInfo.InvariantCulture)).Append(" ms");
            }

            if (p.FilesSkipped > 0)
            {
                sb.Append(". ").Append(p.FilesSkipped.ToString("N0", CultureInfo.InvariantCulture));
                sb.Append(p.FilesSkipped == 1 ? " file skipped" : " files skipped");
            }
            // Named, not folded into the skip count. A folder of PDFs that
            // answers "no hits" is not an answer; it is a missing capability
            // wearing one.
            if (p.FilesUnsupported > 0)
            {
                sb.Append(". ").Append(p.FilesUnsupported.ToString("N0", CultureInfo.InvariantCulture));
                sb.Append(p.FilesUnsupported == 1 ? " file is in a format" : " files are in formats");
                sb.Append(" RSFind cannot read");
                if (p.UnsupportedKinds.Length > 0) sb.Append(" (").Append(p.UnsupportedKinds).Append(")");
            }
            // Both of these change what the numbers above mean, so neither is
            // allowed to be silent. A short list that does not say it is short
            // reads as "that is all there is".
            if (p.Fault != null)
                sb.Append(". The search stopped early (").Append(p.Fault)
                  .Append("), so this is a partial result");
            if (p.Cancelled) sb.Append(". Cancelled - this is a partial result");
            if (p.Truncated) sb.Append(". Hit cap reached - there are more matches than are listed");
            lock (_pendingLock)
            {
                if (_errors.Count > 0)
                {
                    sb.Append(". ").Append(_errors.Count.ToString(CultureInfo.InvariantCulture));
                    sb.Append(_errors.Count == 1 ? " place could not be read" : " places could not be read");
                }
            }
            return sb.ToString();
        }

        void SetSummary(string text, bool warn)
        {
            _summary.Text = text;
            _summary.ForeColor = warn ? Th.T.Warn : Th.T.TxtDim;
        }

        // ---- replace ---------------------------------------------------------------

        static string UndoRoot { get { return Path.Combine(Settings.Dir, "undo"); } }

        void PreviewReplace()
        {
            if (_running || _found.Count == 0 || _activeMatcher == null) return;

            if (_replace.Text.Length == 0)
            {
                SetSummary("Type what to replace the matches with.", true);
                _replace.Focus();
                return;
            }

            try
            {
                _activeMatcher.ValidateReplacement(_replace.Text);
            }
            catch (PatternError ex)
            {
                SetSummary("Bad replacement: " + ex.Message, true);
                _replace.Focus();
                return;
            }

            // Refuse before planning rather than after. Planning re-reads every
            // matched file, and a run this size is one the person should narrow
            // regardless of what the preview would have shown.
            int total = 0;
            for (int i = 0; i < _found.Count; i++) total += _found[i].Hits.Count;
            if (total > ReplaceDialog.MaxPreviewChanges)
            {
                SetSummary("That would change " + total.ToString("N0", CultureInfo.InvariantCulture)
                    + " lines, which is more than the preview will show. Narrow the search with a "
                    + "file mask or a folder further in, then replace.", true);
                return;
            }

            ReplaceOptions options = new ReplaceOptions();
            options.Replacement = _replace.Text;
            options.PreserveCase = _preserveCase.Checked;

            List<ReplacePlan> plans = new List<ReplacePlan>();
            for (int i = 0; i < _found.Count; i++)
                plans.Add(Replacer.Plan(_found[i], _activeMatcher, options));

            bool anything = false;
            for (int i = 0; i < plans.Count; i++)
                if (plans[i].Refusal == null) { anything = true; break; }
            if (!anything && plans.Count > 0)
            {
                SetSummary("Nothing here can be replaced. Open the preview to see why.", true);
            }

            using (ReplaceDialog dialog = new ReplaceDialog(plans, _activeQuery, _replace.Text))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
            }

            ReplaceResult result = Replacer.Apply(plans, UndoRoot);
            ReportReplace(result);
        }

        void ReportReplace(ReplaceResult result)
        {
            StringBuilder sb = new StringBuilder();
            // Matches, not lines: two on the same line are two changes, and
            // the preview counted them that way.
            sb.Append("Replaced ").Append(result.ChangesWritten.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(result.ChangesWritten == 1 ? " match in " : " matches in ");
            sb.Append(result.FilesWritten.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(result.FilesWritten == 1 ? " file" : " files");
            if (result.UndoDirectory != null) sb.Append(". Undo is available from the menu");
            // The list is now a picture of a folder that no longer exists.
            // Saying so beats letting someone read it as current.
            if (result.FilesWritten > 0) sb.Append(". The results above show the text as it was");
            if (result.Failures.Count > 0)
            {
                sb.Append(". ").Append(result.Failures.Count.ToString(CultureInfo.InvariantCulture));
                sb.Append(result.Failures.Count == 1 ? " file was not written: " : " files were not written: ");
                sb.Append(result.Failures[0]);
                if (result.Failures.Count > 1) sb.Append(" (and others)");
            }
            SetSummary(sb.ToString(), result.Failures.Count > 0);

            // The results on screen now describe files that have changed, and
            // replacing again from them would be refused file by file. Saying
            // so beats letting someone discover it one refusal at a time.
            if (result.FilesWritten > 0)
            {
                _preview.Enabled = false;
                _found.Clear();
            }
        }

        void OnUndoReplace(object sender, EventArgs e)
        {
            string dir = Replacer.LatestUndoDirectory(UndoRoot);
            if (dir == null)
            {
                SetSummary("There is no replace to undo.", true);
                return;
            }

            if (MessageBox.Show(this,
                    "Put back the files changed by the last replace?\r\n\r\n"
                    + "Any file edited since then is left alone.",
                    "Undo Last Replace", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question) != DialogResult.OK)
                return;

            ReplaceResult result = Replacer.Undo(dir);
            StringBuilder sb = new StringBuilder();
            sb.Append("Restored ").Append(result.FilesWritten.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(result.FilesWritten == 1 ? " file" : " files");
            if (result.Failures.Count > 0)
            {
                sb.Append(". ").Append(result.Failures.Count.ToString(CultureInfo.InvariantCulture));
                sb.Append(result.Failures.Count == 1 ? " was left alone: " : " were left alone: ");
                sb.Append(result.Failures[0]);
                if (result.Failures.Count > 1) sb.Append(" (and others)");
            }
            SetSummary(sb.ToString(), result.Failures.Count > 0);
        }

        // ---- commands ------------------------------------------------------------

        void OnBrowse(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Choose the folder to search";
                dlg.ShowNewFolderButton = false;
                if (Directory.Exists(_folder.Text.Trim())) dlg.SelectedPath = _folder.Text.Trim();
                if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
            }
        }

        void OnOpenRequested(object sender, OpenHitEventArgs e)
        {
            string command = _settings.EditorCommand;
            try
            {
                if (string.IsNullOrEmpty(command) || e.ShellOnly)
                {
                    // The shell would RUN this, not open it.
                    //
                    // Refused rather than confirmed, because a confirmation on
                    // a double-click is a thing people dismiss. The message
                    // names both ways forward: an editor command opens the file
                    // safely - handing a path to a text editor does not execute
                    // it - and Open Containing Folder was already the safe
                    // route for anything else.
                    if (ViewRules.IsExecutable(e.Path,
                            Environment.GetEnvironmentVariable("PATHEXT")))
                    {
                        SetSummary("RSFind will not launch "
                            + Path.GetFileName(e.Path)
                            + " - Windows would run it rather than open it. Set an editor "
                            + "command in the menu to read it, or use Open Containing Folder.",
                            true);
                        return;
                    }
                    Process.Start(e.Path);   // whatever the shell associates
                    return;
                }
                string line = e.Line.ToString(CultureInfo.InvariantCulture);
                string expanded = command.Replace("{file}", "\"" + e.Path + "\"")
                                         .Replace("{line}", line);
                // The command is split on the first space so the editor's own
                // path can carry arguments. A quoted path is honored.
                string exe, args;
                SplitCommand(expanded, out exe, out args);
                Process.Start(exe, args);
            }
            catch (Win32Exception ex)
            {
                SetSummary("Could not open the file: " + ex.Message, true);
            }
            catch (FileNotFoundException ex)
            {
                SetSummary("Could not open the file: " + ex.Message, true);
            }
        }

        static void SplitCommand(string command, out string exe, out string args)
        {
            command = command.Trim();
            if (command.StartsWith("\"", StringComparison.Ordinal))
            {
                int close = command.IndexOf('"', 1);
                if (close > 0)
                {
                    exe = command.Substring(1, close - 1);
                    args = command.Substring(close + 1).Trim();
                    return;
                }
            }
            int space = command.IndexOf(' ');
            if (space < 0) { exe = command; args = ""; return; }
            exe = command.Substring(0, space);
            args = command.Substring(space + 1).Trim();
        }

        void OnEditorCommand(object sender, EventArgs e)
        {
            using (PromptDialog dlg = new PromptDialog(
                "Editor Command",
                "Leave blank to open files with whatever Windows associates. "
                + "Use {file} and {line} as placeholders.\r\n\r\n"
                + "Examples:  notepad++ {file} -n{line}      code -g {file}:{line}",
                _settings.EditorCommand))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _settings.EditorCommand = dlg.Value.Trim();
                _settings.Save();
            }
        }

        void OnExport(object sender, EventArgs e)
        {
            if (_results.FileCount == 0)
            {
                SetSummary("There is nothing to export yet.", true);
                return;
            }
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "Export Results";
                dlg.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                dlg.FileName = "rsfind-results.txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine(Describe(_progress));
                    sb.AppendLine();
                    sb.Append(_results.AllAsText());
                    File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                    SetSummary("Exported to " + dlg.FileName, false);
                }
                catch (IOException ex) { SetSummary("Could not write the file: " + ex.Message, true); }
                catch (UnauthorizedAccessException ex) { SetSummary("Could not write the file: " + ex.Message, true); }
            }
        }

        void OnAbout(object sender, EventArgs e)
        {
            MessageBox.Show(this,
                "RSFind searches the text inside the files in a folder, on demand.\r\n\r\n"
                + "It builds no index, installs no service, and watches nothing in the "
                + "background. It reads only the folder you point it at, and it never "
                + "touches the network.\r\n\r\n"
                + "Search queries are never written to disk.\r\n\r\n"
                + "Settings: " + Settings.FilePath + "\r\n\r\n"
                + "Replace keeps a copy of every file it changes, so a run can be "
                + "undone. The last "
                + Replacer.MaxUndoRuns.ToString(CultureInfo.InvariantCulture)
                + " runs are kept and older ones are removed:\r\n" + UndoRoot,
                "About RSFind", MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        // ---- the Explorer entry ---------------------------------------------------

        // Per-user, under HKCU. No admin prompt, nothing written outside this
        // user's hive, and removing it removes every trace - the same
        // philosophy as the rest of the family. A tool that asks for elevation
        // to add a right-click item has misjudged what it is.
        static string VerbKey(bool background)
        {
            return background
                ? @"Software\Classes\Directory\Background\shell\" + RegistryKeyName
                : @"Software\Classes\Directory\shell\" + RegistryKeyName;
        }

        static bool ExplorerEntryPresent()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(VerbKey(false)))
                    return k != null;
            }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        void OnToggleExplorerEntry(object sender, EventArgs e)
        {
            // Refused from the source path rather than written wrong.
            //
            // The handler recorded is Application.ExecutablePath, which here is
            // powershell.exe. The entry would open a PowerShell window instead
            // of RSFind, under a registry key named RSFind. A menu item that
            // does nothing useful is better than one that lies about what it
            // launches.
            if (RunningFromSource)
            {
                SetSummary("The Explorer entry needs RSFind.exe - it records the program "
                    + "to launch, and running from source that would be PowerShell. "
                    + "Run Build-RSFind.cmd, then add it from there.", true);
                return;
            }

            try
            {
                if (ExplorerEntryPresent())
                {
                    Registry.CurrentUser.DeleteSubKeyTree(VerbKey(false), false);
                    Registry.CurrentUser.DeleteSubKeyTree(VerbKey(true), false);
                    SetSummary("Removed from the Explorer right-click menu.", false);
                    return;
                }

                string exe = Application.ExecutablePath;
                WriteVerb(VerbKey(false), exe);
                WriteVerb(VerbKey(true), exe);
                SetSummary("Added to the Explorer right-click menu for folders.", false);
            }
            catch (UnauthorizedAccessException ex) { SetSummary("Registry: " + ex.Message, true); }
            catch (System.Security.SecurityException ex) { SetSummary("Registry: " + ex.Message, true); }
        }

        static void WriteVerb(string path, string exe)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(path))
            {
                if (k == null) return;
                k.SetValue(null, ExplorerVerbLabel);
                k.SetValue("Icon", exe);
                using (RegistryKey c = k.CreateSubKey("command"))
                {
                    // %V is the folder that was clicked, and it is the one that
                    // works for both the folder itself and the empty space
                    // inside it. %1 is empty for the background verb.
                    if (c != null) c.SetValue(null, "\"" + exe + "\" \"%V\"");
                }
            }
        }

        // ---- entry point -------------------------------------------------------------

        // Set by Run-From-Source.ps1 before it calls Run.
        //
        // The one thing that genuinely differs on that path is the Explorer
        // entry: it records Application.ExecutablePath as the handler, which
        // under a PowerShell host is powershell.exe. Writing that into the
        // registry would produce a right-click item that opens a PowerShell
        // window instead of RSFind, under a key named RSFind - wrong, and
        // alarming to anyone who later reads their own hive.
        public static bool RunningFromSource;

        // The whole of startup, callable without an exe.
        public static void Run(string folder)
        {
            // DPI awareness and the layout scale ship together: awareness alone
            // renders a fixed-pixel layout tiny at 150%, which is worse than the
            // blur it removes. Both must precede the first window.
            try { Native.SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { }
            Dpi.Init();
            Application.EnableVisualStyles();
            // Throws if a control already exists, which is the normal state
            // inside a PowerShell host.
            try { Application.SetCompatibleTextRenderingDefault(false); }
            catch (InvalidOperationException) { }
            OsChrome.EnableDarkModeSupport();   // must precede the first window

            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) folder = null;
            Application.Run(new MainForm(folder));
        }

        [STAThread]
        public static void Main(string[] argv)
        {
            string folder = null;
            if (argv != null && argv.Length > 0)
            {
                // Explorer hands the clicked folder as one argument. A path
                // with a trailing backslash inside quotes arrives with the
                // quote escaped onto the end, which is a Windows habit rather
                // than a mistake the user made.
                folder = argv[0].Trim().TrimEnd('"');
            }
            Run(folder);
        }
    }

    // A one-field prompt, themed. MessageBox cannot take input and a full
    // settings window would be four controls in a form of its own.
    public class PromptDialog : Form
    {
        TextBox _box;
        public string Value { get { return _box.Text; } }

        public PromptDialog(string title, string help, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(Dpi.S(520), Dpi.S(190));
            BackColor = Th.T.Panel;
            ForeColor = Th.T.Txt;

            Label label = new Label();
            label.Text = help;
            label.ForeColor = Th.T.TxtDim;
            label.SetBounds(Dpi.S(14), Dpi.S(14), ClientSize.Width - Dpi.S(28), Dpi.S(84));
            Controls.Add(label);

            _box = new TextBox();
            _box.BorderStyle = BorderStyle.None;
            _box.Text = initial;
            InputHost host = new InputHost(_box, Dpi.S(6), Dpi.S(4));
            host.SetBounds(Dpi.S(14), Dpi.S(106), ClientSize.Width - Dpi.S(28), Dpi.S(26));
            Controls.Add(host);

            ThemedButton ok = new ThemedButton();
            ok.Text = "Save";
            ok.Primary = true;
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(ClientSize.Width - Dpi.S(14) - Dpi.S(90), Dpi.S(146), Dpi.S(90), Dpi.S(26));
            Controls.Add(ok);

            ThemedButton cancel = new ThemedButton();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(ok.Left - Dpi.S(8) - Dpi.S(90), Dpi.S(146), Dpi.S(90), Dpi.S(26));
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            Shown += delegate { OsChrome.ApplyTitleBar(this); _box.Focus(); };
        }
    }
}
