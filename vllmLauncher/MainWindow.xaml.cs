using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using vllmLauncher.Models;
using vllmLauncher.Services;
using vllmLauncher.ViewModel;

namespace vllmLauncher
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        #region Win32 P/Invoke for window activation

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();

        #endregion

        // Drag-and-drop state
        private ModelConfigViewModel? _dragSourceItem;
        private Point _dragStartPoint;
        private bool _isDragReady;
        private readonly DispatcherTimer _longPressTimer;

        // Editor (RichTextBox) command-link state
        private bool _suppressEditorSync;   // set while pushing editor -> viewmodel
        private bool _loadingDocument;      // set while rebuilding the document from the model
        private int _nextGroupId = 1;
        private CommandGroup? _pendingUnwrapGroup;
        private CommandGroup? _pendingCollapseGroup;
        private CommandGroup? _hoverGroup;
        private readonly HashSet<int> _collapsedGroupIds = new();

        // Link click state: execution happens on mouse-up (same group + tiny movement +
        // single click) so that a double-click executes exactly once and a drag never executes.
        private CommandGroup? _pendingLinkGroup;
        private Point _pendingLinkPos;
        private int _pendingLinkClickCount;
        private CommandGroup? _lastLinkExecutedGroup;
        private DateTime _lastLinkExecuteTime = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();

            var configService = new ConfigService();
            var psService = new PowerShellService();
            _viewModel = new MainViewModel(configService, psService);

            DataContext = _viewModel;

            // 2-second long press timer
            _longPressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(2000)
            };
            _longPressTimer.Tick += LongPressTimer_Tick;

            ModelList.PreviewMouseLeftButtonDown += ModelList_PreviewMouseLeftButtonDown;
            ModelList.PreviewMouseLeftButtonUp += ModelList_PreviewMouseLeftButtonUp;
            ModelList.MouseDoubleClick += ModelList_MouseDoubleClick;

            // Editor: keep document <-> viewmodel in sync, force plain-text paste
            ScriptEditor.TextChanged += ScriptEditor_TextChanged;
            DataObject.AddPastingHandler(ScriptEditor, Editor_Pasting);
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            ScriptEditor.PreviewMouseMove += Editor_PreviewMouseMove;
            ScriptEditor.MouseLeave += Editor_MouseLeave;
            ScriptEditor.PreviewMouseLeftButtonDown += Editor_PreviewMouseLeftButtonDown;
            ScriptEditor.PreviewMouseLeftButtonUp += Editor_PreviewMouseLeftButtonUp;
            // A lost window can miss the editor's MouseLeave; make sure the hand override
            // never outlives the window being active.
            Deactivated += (s, e) => Mouse.OverrideCursor = null;
            LoadDocumentFromModel();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.Connect();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsPanel.Visibility == Visibility.Visible)
            {
                SettingsPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                SettingsPanel.Visibility = Visibility.Visible;
            }
        }

        private void PasteWaitTimeTextBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            // Allow only digits and decimal point
            Regex regex = new Regex("[^0-9.]");
            e.Handled = regex.IsMatch(e.Text);
        }

        #region Editor command links (RichTextBox sync + 命令/取消命令)

        // --- Document <-> ViewModel synchronization ---

        private void ScriptEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressEditorSync || _loadingDocument) return;
            SyncDocumentToModel();
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.EditorText) && !_suppressEditorSync && !_loadingDocument)
                LoadDocumentFromModel();
        }

        /// <summary>Rebuild the editor document from the selected model's FullText + command markers.</summary>
        private void LoadDocumentFromModel()
        {
            _loadingDocument = true;
            _hoverGroup = null;
            ScriptEditor.Cursor = null;
            Mouse.OverrideCursor = null;
            try
            {
                var doc = new FlowDocument
                {
                    PagePadding = new Thickness(12, 10, 12, 10)
                };
                string text = _viewModel.SelectedModel?.FullText ?? string.Empty;
                foreach (var line in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
                    doc.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0) });
                ScriptEditor.Document = doc;

                var markers = _viewModel.SelectedModel?.Config.Commands ?? new List<CommandMarker>();
                _nextGroupId = 1;
                foreach (var m in markers)
                    if (m.GroupId >= _nextGroupId) _nextGroupId = m.GroupId + 1;

                // Segments sharing a GroupId share one CommandGroup (group hover / execute)
                var groups = new Dictionary<int, CommandGroup>();
                foreach (var m in markers.OrderBy(m => m.Start))
                {
                    if (!groups.TryGetValue(m.GroupId, out var g))
                    {
                        g = new CommandGroup(m.GroupId);
                        groups[m.GroupId] = g;
                    }
                    WrapRange(g, m.Start, m.Start + m.Length);
                }

                // Rolled-up groups show only their first line plus a chevron to expand again
                _collapsedGroupIds.Clear();
                foreach (var cg in _viewModel.SelectedModel?.Config.CollapsedGroups ?? new List<CollapsedGroup>())
                    _collapsedGroupIds.Add(cg.GroupId);
                if (_collapsedGroupIds.Count > 0)
                    AppendCollapsedSuffixes(groups);
            }
            finally
            {
                _loadingDocument = false;
            }
        }

        /// <summary>Push current document text and hyperlink offsets back to the selected model (saves config).</summary>
        private void SyncDocumentToModel()
        {
            _suppressEditorSync = true;
            try
            {
                var markers = new List<CommandMarker>();
                foreach (var info in EnumerateHyperlinks())
                    markers.Add(new CommandMarker { Start = info.AbsStart, Length = info.Length, GroupId = info.Group.Id });

                _viewModel.UpdateSelectedCommands(markers);
                _viewModel.EditorText = GetPlainText(); // setter syncs FullText and calls SaveConfig

                // A rolled-up group must stay one contiguous marker; any edit or re-marking
                // that breaks that shape silently expands it (text itself is untouched).
                DropStaleCollapsedRecords();
            }
            finally
            {
                _suppressEditorSync = false;
            }
        }

        private void Editor_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            // Always paste as plain text so the document only ever contains Runs and command Hyperlinks
            if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
            {
                string? text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
                e.CancelCommand();
                if (text != null)
                    ScriptEditor.Selection.Text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            }
            else
            {
                e.CancelCommand();
            }
        }

        // --- Context menu: 命令 / 取消命令 ---

        private void EditorContextMenu_Opening(object sender, ContextMenuEventArgs e)
        {
            // The popup shares this process, so an active hand override would also apply
            // to the menu items; drop it while the menu is up.
            Mouse.OverrideCursor = null;
            _hoverGroup = null;
            _pendingUnwrapGroup = null;
            _pendingCollapseGroup = null;
            var sel = ScriptEditor.Selection;

            if (!sel.IsEmpty)
            {
                CommandToggleMenuItem.Header = "命令";
                CommandToggleMenuItem.IsEnabled = true;
                CollapseToggleMenuItem.IsEnabled = false;
            }
            else
            {
                // Prefer the hyperlink under the right-click position; fall back to caret position
                var group = GetGroupFromHit(e.OriginalSource) ?? GetGroupAtOffset(GetTextOffset(sel.Start));
                if (group != null)
                {
                    _pendingUnwrapGroup = group;
                    CommandToggleMenuItem.Header = "取消命令";
                    CommandToggleMenuItem.IsEnabled = true;

                    var segs = FindGroupSegmentInfos(group);
                    bool collapsed = _collapsedGroupIds.Contains(group.Id);
                    _pendingCollapseGroup = group;
                    CollapseToggleMenuItem.Header = collapsed ? "展开" : "卷起";
                    CollapseToggleMenuItem.IsEnabled = collapsed || CanCollapseGroup(group, segs);
                }
                else
                {
                    CommandToggleMenuItem.Header = "命令";
                    CommandToggleMenuItem.IsEnabled = false;
                    CollapseToggleMenuItem.IsEnabled = false;
                }
            }
        }

        /// <summary>Walk up from the hit element to find the command hyperlink it belongs to.</summary>
        private static CommandGroup? GetGroupFromHit(object? source)
        {
            DependencyObject? d = source as DependencyObject;
            while (d != null)
            {
                if (d is Hyperlink hl && hl.Tag is CommandGroup g) return g;
                if (d is FrameworkContentElement fce) d = fce.Parent;
                else d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        private void CommandToggle_Click(object sender, RoutedEventArgs e)
        {
            if ((string)CommandToggleMenuItem.Header == "取消命令" && _pendingUnwrapGroup != null)
            {
                var group = _pendingUnwrapGroup;
                _pendingUnwrapGroup = null;

                // A rolled-up group keeps whole lines outside the document; unwrapping it
                // without expanding first would silently delete those lines.
                if (_collapsedGroupIds.Contains(group.Id))
                {
                    ExpandGroup(group);                  // rebuilds the document
                    group = FindGroupById(group.Id);     // groups are recreated by the reload
                    if (group == null) return;
                }

                UnwrapGroup(group);
                SyncDocumentToModel();
            }
            else
            {
                WrapSelectionAsCommand();
            }
        }

        /// <summary>The group with this id in the current document (ids survive a reload).</summary>
        private CommandGroup? FindGroupById(int id)
            => EnumerateHyperlinks().Select(i => i.Group).FirstOrDefault(g => g.Id == id);

        private void CollapseToggle_Click(object sender, RoutedEventArgs e)
        {
            var group = _pendingCollapseGroup;
            _pendingCollapseGroup = null;
            if (group == null) return;

            if (_collapsedGroupIds.Contains(group.Id))
                ExpandGroup(group);
            else
                CollapseGroup(group);
        }

        // --- Wrap / unwrap command segments ---

        private void WrapSelectionAsCommand()
        {
            var sel = ScriptEditor.Selection;
            if (sel.IsEmpty) return;

            int s = GetTextOffset(sel.Start);
            int e = GetTextOffset(sel.End);
            if (s > e) (s, e) = (e, s);
            if (s >= e) return;

            // Unwrap existing commands that the selection overlaps, so links never nest
            var overlapping = EnumerateHyperlinks()
                .Where(h => h.AbsStart < e && h.AbsStart + h.Length > s)
                .Select(h => h.Group)
                .Distinct()
                .ToList();

            // Rolled-up groups keep their following lines outside the document: expand them
            // first so that text is never dropped. The selection is lost by the reload, so
            // the wrap itself is left for the user to redo on the expanded text.
            var rolledUp = overlapping.Where(g => _collapsedGroupIds.Contains(g.Id)).ToList();
            if (rolledUp.Count > 0)
            {
                foreach (var g in rolledUp) ExpandGroup(g);
                return;
            }

            foreach (var g in overlapping)
                UnwrapGroup(g); // text content unchanged, so offsets s/e stay valid

            WrapRange(new CommandGroup(_nextGroupId++), s, e);
            SyncDocumentToModel();
        }

        private void UnwrapGroup(CommandGroup group)
        {
            // Rebuild each affected paragraph: hyperlink is replaced by its children
            foreach (var p in EnumerateHyperlinks()
                         .Where(i => ReferenceEquals(i.Group, group))
                         .Select(i => i.Para).Distinct().ToList())
            {
                var seq = new List<Inline>();
                foreach (var inline in p.Inlines.ToList())
                {
                    if (inline is Hyperlink hl && ReferenceEquals(hl.Tag, group))
                        seq.AddRange(hl.Inlines.ToList());
                    else
                        seq.Add(inline);
                }

                while (p.Inlines.Count > 0) p.Inlines.Remove(p.Inlines.FirstInline);
                foreach (var inl in seq) p.Inlines.Add(inl);
            }
            // A group without markers has no record anchor left; ConfigService/sync drops it
            _collapsedGroupIds.Remove(group.Id);
        }

        #region 多行命令卷起 / 展开

        /// <summary>Segments of one group in the live document, in reading order.</summary>
        private List<HyperlinkInfo> FindGroupSegmentInfos(CommandGroup group)
            => EnumerateHyperlinks()
               .Where(i => ReferenceEquals(i.Group, group))
               .OrderBy(i => i.ParaIndex).ThenBy(i => i.AbsStart)
               .ToList();

        /// <summary>
        /// Rolling up means "hide every line after the group's first one". That is safe when
        /// each hidden line is either fully made of the group's segments or completely blank
        /// (a separator line, hidden and restored verbatim). The first line may be selected
        /// only partially, since it never gets hidden.
        /// </summary>
        private bool CanCollapseGroup(CommandGroup group, List<HyperlinkInfo> segs)
        {
            if (segs.Count < 2) return false;
            if (segs[^1].ParaIndex == segs[0].ParaIndex) return false; // single line

            var blocks = ScriptEditor.Document.Blocks.ToList();
            int firstPara = segs[0].ParaIndex;
            int lastPara = segs[^1].ParaIndex;
            if (firstPara < 0 || lastPara >= blocks.Count) return false;

            var covered = new Dictionary<int, int>();
            foreach (var s in segs)
                covered[s.ParaIndex] = covered.TryGetValue(s.ParaIndex, out int v) ? v + s.Length : s.Length;

            // Every line that would be hidden: fully covered by the group's segments, or blank.
            // (Uncovered text in between would be swallowed by the cut, so it disqualifies.)
            for (int pi = firstPara + 1; pi <= lastPara; pi++)
            {
                int blockLen = GetBlockTextLength(blocks[pi]);
                if (blockLen == 0) continue; // blank separator: hidden as-is, restored on expand
                if (!covered.TryGetValue(pi, out int len) || len != blockLen) return false;
            }

            // The group's segments on one line must be adjacent (no hole in the middle).
            for (int i = 1; i < segs.Count; i++)
            {
                var prev = segs[i - 1];
                var cur = segs[i];
                if (cur.ParaIndex == prev.ParaIndex && cur.AbsStart != prev.AbsStart + prev.Length) return false;
            }
            return true;
        }

        /// <summary>
        /// Roll the group up: the first line stays in the document, all following lines of
        /// the group are moved verbatim into the record (hidden) until it is expanded again.
        /// </summary>
        private void CollapseGroup(CommandGroup group)
        {
            var model = _viewModel.SelectedModel;
            if (model == null) return;

            var segs = FindGroupSegmentInfos(group);
            if (!CanCollapseGroup(group, segs)) return;

            string text = model.FullText;
            int firstPara = segs[0].ParaIndex;
            int lastPara = segs[^1].ParaIndex;

            // plain-text line boundaries (document paragraph i == line i)
            var lineStart = new List<int>();
            var lineLen = new List<int>();
            for (int pos = 0; ;)
            {
                int nl = text.IndexOf('\n', pos);
                lineStart.Add(pos);
                lineLen.Add((nl < 0 ? text.Length : nl) - pos);
                if (nl < 0) break;
                pos = nl + 1;
            }
            if (lastPara >= lineStart.Count) return;

            int cutStart = lineStart[firstPara] + lineLen[firstPara]; // the '\n' ending line 1
            int cutEnd = lineStart[lastPara] + lineLen[lastPara];     // end of the group's last line
            if (cutEnd < cutStart) return;

            var record = new CollapsedGroup { GroupId = group.Id };
            for (int pi = firstPara + 1; pi <= lastPara; pi++)
            {
                record.HiddenLines.Add(text.Substring(lineStart[pi], lineLen[pi]));
                record.HiddenSegments.Add(segs.Where(s => s.ParaIndex == pi).Select(s => s.Length).ToList());
            }
            if (!record.IsConsistent()) return;

            int removed = cutEnd - cutStart;
            string newText = text.Remove(cutStart, removed);

            var newMarkers = new List<CommandMarker>();
            foreach (var m in model.Config.Commands)
            {
                if (m.GroupId == group.Id && m.Start >= cutStart) continue; // markers on hidden lines
                int start = m.Start >= cutEnd ? m.Start - removed : m.Start;
                newMarkers.Add(new CommandMarker { Start = start, Length = m.Length, GroupId = m.GroupId });
            }

            model.FullText = newText;
            model.Config.Commands = newMarkers;
            var records = model.Config.CollapsedGroups
                .Where(r => r.GroupId != group.Id).Append(record).ToList();
            _collapsedGroupIds.Add(group.Id);

            _viewModel.SetSelectedCollapsedGroups(records);
            _suppressEditorSync = true;
            try { _viewModel.EditorText = newText; }
            finally { _suppressEditorSync = false; }
            LoadDocumentFromModel();
        }

        /// <summary>Expand a rolled-up group: its hidden lines go back right after the first line.</summary>
        private void ExpandGroup(CommandGroup group)
        {
            var model = _viewModel.SelectedModel;
            if (model == null) return;
            var record = model.Config.CollapsedGroups.FirstOrDefault(r => r.GroupId == group.Id);
            if (record == null)
            {
                _collapsedGroupIds.Remove(group.Id);
                return;
            }

            string text = model.FullText;
            var own = model.Config.Commands.Where(m => m.GroupId == group.Id).OrderBy(m => m.Start).ToList();

            // Insert after the whole line the group starts on, so later edits to that line
            // (even adding text) still restore the continuation right below it.
            int anchor = EndOfLineContaining(text, own.Count > 0 ? own[0].Start : text.Length);
            string insert = "\n" + string.Join("\n", record.HiddenLines);
            string newText = text.Insert(anchor, insert);

            var newMarkers = new List<CommandMarker>();
            foreach (var m in model.Config.Commands)
            {
                int start = m.Start > anchor ? m.Start + insert.Length : m.Start;
                newMarkers.Add(new CommandMarker { Start = start, Length = m.Length, GroupId = m.GroupId });
            }
            int linePos = anchor + 1;
            for (int i = 0; i < record.HiddenLines.Count; i++)
            {
                int pos = linePos;
                foreach (int len in record.HiddenSegments[i])
                {
                    newMarkers.Add(new CommandMarker { Start = pos, Length = len, GroupId = group.Id });
                    pos += len;
                }
                linePos += record.HiddenLines[i].Length + 1;
            }

            model.FullText = newText;
            model.Config.Commands = newMarkers.OrderBy(m => m.Start).ToList();
            model.Config.CollapsedGroups = model.Config.CollapsedGroups.Where(r => r.GroupId != group.Id).ToList();
            _collapsedGroupIds.Remove(group.Id);

            _viewModel.SetSelectedCollapsedGroups(model.Config.CollapsedGroups);
            _suppressEditorSync = true;
            try { _viewModel.EditorText = newText; }
            finally { _suppressEditorSync = false; }
            LoadDocumentFromModel();
        }

        /// <summary>Offset of the '\n' ending the line that contains 'offset' (text end if none).</summary>
        private static int EndOfLineContaining(string text, int offset)
        {
            offset = Math.Max(0, Math.Min(offset, text.Length));
            int nl = text.IndexOf('\n', offset);
            return nl < 0 ? text.Length : nl;
        }

        /// <summary>Appends the expand chevron to the visible line of every rolled-up group.</summary>
        private void AppendCollapsedSuffixes(Dictionary<int, CommandGroup> groups)
        {
            foreach (var pair in groups)
            {
                if (!_collapsedGroupIds.Contains(pair.Key)) continue;
                var last = FindGroupSegmentInfos(pair.Value).LastOrDefault();
                if (last == null) continue;

                Paragraph p = last.Para;
                var seq = new List<Inline>();
                bool placed = false;
                foreach (var inline in p.Inlines.ToList())
                {
                    seq.Add(inline);
                    if (!placed && ReferenceEquals(inline, last.Link))
                    {
                        seq.Add(CreateExpandButton(pair.Value));
                        placed = true;
                    }
                }
                if (!placed) continue;

                while (p.Inlines.Count > 0) p.Inlines.Remove(p.Inlines.FirstInline);
                foreach (var inl in seq) p.Inlines.Add(inl);
            }
        }

        /// <summary>
        /// Vector chevron at the end of a rolled-up command's first line; clicking it expands
        /// the group. It is decoration, not content, so text/offset helpers count it as zero.
        /// </summary>
        private Inline CreateExpandButton(CommandGroup group)
        {
            var idle = (Brush)Application.Current.FindResource("Gray500");
            var hover = (Brush)Application.Current.FindResource("Gray800");

            var chevron = new Path
            {
                Data = Geometry.Parse("M 0 0 L 5 5.5 L 10 0"),
                Stroke = idle,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 10,
                Height = 6,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
            };

            var button = new Button
            {
                Style = (Style)Application.Current.FindResource("ChevronBtn"),
                Content = chevron,
                Tag = group,
                ToolTip = "展开",
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = Cursors.Hand,
            };
            // The button has no text content: give it an accessible name.
            button.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "展开命令");
            button.MouseEnter += (s, e) => chevron.Stroke = hover;
            button.MouseLeave += (s, e) => chevron.Stroke = idle;
            button.Click += (s, e) =>
            {
                e.Handled = true;
                if (s is Button b && b.Tag is CommandGroup g) ExpandGroup(g);
            };

            return new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.Center };
        }

        /// <summary>Drop records whose group lost its markers or whose record no longer fits.</summary>
        private void DropStaleCollapsedRecords()
        {
            var model = _viewModel.SelectedModel;
            if (model == null || model.Config.CollapsedGroups.Count == 0) return;

            var ids = new HashSet<int>(model.Config.Commands.Select(m => m.GroupId));
            var stale = model.Config.CollapsedGroups
                .Where(r => !ids.Contains(r.GroupId) || !r.IsConsistent())
                .Select(r => r.GroupId)
                .ToList();
            if (stale.Count == 0) return;

            model.Config.CollapsedGroups = model.Config.CollapsedGroups.Where(r => !stale.Contains(r.GroupId)).ToList();
            foreach (int id in stale) _collapsedGroupIds.Remove(id);
        }

        #endregion

        /// <summary>Wrap text range [s, e) (document plain-text offsets) into Hyperlinks sharing one group.</summary>
        private void WrapRange(CommandGroup group, int s, int e)
        {
            int offset = 0;
            bool first = true;
            // Snapshot the blocks: WrapParagraphRange mutates the document and would
            // invalidate the live TextElementEnumerator ("Collection was modified").
            foreach (Block block in ScriptEditor.Document.Blocks.ToList())
            {
                if (!first) offset += 1; // paragraph separator '\n'
                first = false;

                if (block is Paragraph p)
                {
                    int pStart = offset;
                    int pLen = GetBlockTextLength(p);
                    int cs = Math.Max(s, pStart);
                    int ce = Math.Min(e, pStart + pLen);
                    if (cs < ce) WrapParagraphRange(p, cs - pStart, ce - pStart, group);
                    offset = pStart + pLen;
                }
            }
        }

        private void WrapParagraphRange(Paragraph p, int colStart, int colEnd, CommandGroup group)
        {
            if (colStart >= colEnd) return;

            // Rebuild the whole inline sequence rather than mutating in place:
            // WPF's InlineCollection.InsertAfter/InsertBefore can throw
            // "PreviousSibling not in this TextElementCollection" for valid references.
            var seq = new List<Inline>();      // final paragraph content
            var wrapped = new List<Run>();     // command-text runs that go inside the hyperlink
            int hlIndex = -1;                  // position of the hyperlink within seq
            int pos = 0;

            foreach (var inline in p.Inlines.ToList())
            {
                int len = GetInlineLength(inline);
                int start = pos, end = pos + len;
                pos = end;

                if (end <= colStart || start >= colEnd || inline is not Run run)
                {
                    seq.Add(inline); // untouched inline (or foreign element), kept as-is
                    continue;
                }

                string t = run.Text;
                int ls = Math.Max(colStart, start) - start;
                int le = Math.Min(colEnd, end) - start;

                if (ls > 0) seq.Add(new Run(t.Substring(0, ls)));
                if (hlIndex < 0) hlIndex = seq.Count;
                wrapped.Add(new Run(t.Substring(ls, le - ls)));
                if (le < len) seq.Add(new Run(t.Substring(le)));
            }

            if (wrapped.Count == 0) return;

            var hl = new Hyperlink
            {
                Style = (Style)Application.Current.FindResource("CommandLink"),
                Tag = group
            };
            foreach (var inl in wrapped) hl.Inlines.Add(inl);
            seq.Insert(hlIndex, hl);

            while (p.Inlines.Count > 0) p.Inlines.Remove(p.Inlines.FirstInline);
            foreach (var inl in seq) p.Inlines.Add(inl);
        }

        // --- Hyperlink interaction: click = 启动, hover = whole group highlight ---
        // Editable RichTextBox swallows content-element mouse events, so hover and
        // click are driven from the editor's Preview events using hit-test results
        // (same mechanism the context menu hit-test relies on).

        private void Editor_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            // While the left button is down the user is selecting text: keep the normal
            // text cursor instead of the hand.
            var group = e.LeftButton == MouseButtonState.Pressed ? null : GetGroupFromHit(e.OriginalSource);
            if (ReferenceEquals(group, _hoverGroup)) return;

            if (_hoverGroup != null) SetGroupHover(_hoverGroup, false);
            _hoverGroup = group;
            if (group != null) SetGroupHover(group, true);
            SetEditorTextCursor(group != null ? Cursors.Hand : null);
        }

        private void Editor_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_hoverGroup != null) { SetGroupHover(_hoverGroup, false); _hoverGroup = null; }
            ScriptEditor.Cursor = null;
            SetEditorTextCursor(null);
        }

        /// <summary>
        /// .NET's RichTextBox renders text with the island text stack (PageVisual/
        /// ParagraphVisual): that layer shows the I-beam regardless of any element-level
        /// Cursor, so the hand must be applied through the mouse override while a link
        /// is hovered. Cleared on editor leave, model switch and window deactivation.
        /// </summary>
        private void SetEditorTextCursor(Cursor? cursor)
        {
            if (!ReferenceEquals(Mouse.OverrideCursor, cursor))
                Mouse.OverrideCursor = cursor;
        }

        /// <summary>Highlight/un-highlight every hyperlink segment belonging to the group.</summary>
        private void SetGroupHover(CommandGroup group, bool hover)
        {
            var brush = hover ? (System.Windows.Media.Brush)Application.Current.FindResource("SelectedBg") : null;
            foreach (var info in EnumerateHyperlinks())
            {
                if (ReferenceEquals(info.Group, group))
                    info.Link.Background = brush;
            }
        }

        private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var group = GetGroupFromHit(e.OriginalSource);
            if (group == null)
            {
                _pendingLinkGroup = null; // press not on a link invalidates a stale candidate
                return;
            }

            e.Handled = true; // don't move the caret onto the link / start a selection there

            // Only record the candidate; execution happens on mouse-up so a double-click
            // cannot fire twice and a press-drag doesn't launch anything.
            _pendingLinkGroup = group;
            _pendingLinkPos = e.GetPosition(ScriptEditor);
            _pendingLinkClickCount = e.ClickCount;
        }

        private void Editor_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var group = _pendingLinkGroup;
            _pendingLinkGroup = null;
            if (group == null) return;

            // Only the first click of a click pair executes (second press of a
            // double-click carries ClickCount >= 2).
            if (_pendingLinkClickCount != 1) return;

            // Press and release must stay on the same link group with at most a tiny
            // movement in between (more than that = drag -> no execution).
            Point pos = e.GetPosition(ScriptEditor);
            if (Math.Abs(pos.X - _pendingLinkPos.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(pos.Y - _pendingLinkPos.Y) > SystemParameters.MinimumVerticalDragDistance)
                return;
            if (!ReferenceEquals(GetGroupFromHit(e.OriginalSource), group)) return;

            // Double-click safety net: a second click inside the system double-click time
            // that WPF counts as a fresh single click (cursor drifted between clicks) must
            // not re-execute the same group.
            DateTime now = DateTime.UtcNow;
            if (ReferenceEquals(_lastLinkExecutedGroup, group) &&
                (now - _lastLinkExecuteTime).TotalMilliseconds < GetDoubleClickTime())
                return;

            string command = GetFullGroupCommand(group);
            if (string.IsNullOrWhiteSpace(command)) return;

            _lastLinkExecutedGroup = group;
            _lastLinkExecuteTime = now;

            // A link whose whole command is a single http(s) address opens in the default
            // browser; anything else is pasted into the terminal.
            if (TryOpenInBrowser(command)) return;

            // Clicking a rolled-up link runs its complete command (hidden lines included);
            // expansion is only via the chevron button or the context menu.
            _viewModel.ExecuteScript(command);
        }

        /// <summary>
        /// The group's command as it should be executed: the text currently in the document
        /// plus, when the group is rolled up, the hidden lines stored in its record.
        /// </summary>
        private string GetFullGroupCommand(CommandGroup group)
        {
            string text = GetGroupText(group);
            var record = _viewModel.SelectedModel?.Config.CollapsedGroups
                .FirstOrDefault(r => r.GroupId == group.Id);
            if (record != null && record.HiddenLines.Count > 0)
                text += "\n" + string.Join("\n", record.HiddenLines);
            return text;
        }

        /// <summary>
        /// When the complete command is just a single web address (http:// or https://, no
        /// spaces, no extra lines), open it in the system default browser. Returns false for
        /// anything else, or when no browser could be started, so the caller can still run
        /// it as a terminal command.
        /// </summary>
        private static bool TryOpenInBrowser(string command)
        {
            string url = command.Trim();
            if (url.Length == 0 || url.IndexOfAny(new[] { '\n', '\r', ' ', '\t' }) >= 0) return false;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch (Exception)
            {
                return false; // no browser/file association -> execute it in the terminal instead
            }
        }

        private string GetGroupText(CommandGroup group)
        {
            var infos = EnumerateHyperlinks()
                .Where(i => ReferenceEquals(i.Group, group))
                .OrderBy(i => i.AbsStart)
                .ToList();

            var sb = new StringBuilder();
            int? prevParaIndex = null;
            foreach (var info in infos)
            {
                if (prevParaIndex != null && info.ParaIndex != prevParaIndex) sb.Append('\n');
                sb.Append(GetInlineText(info.Link));
                prevParaIndex = info.ParaIndex;
            }
            return sb.ToString();
        }

        // --- Document text/offset helpers (plain text = paragraphs joined with '\n') ---

        private sealed class HyperlinkInfo
        {
            public Hyperlink Link = null!;
            public Paragraph Para = null!;
            public int ParaIndex;
            public int AbsStart;
            public int Length;
            public CommandGroup Group = null!;
        }

        private List<HyperlinkInfo> EnumerateHyperlinks()
        {
            var list = new List<HyperlinkInfo>();
            int offset = 0, paraIndex = 0;
            bool first = true;

            foreach (Block block in ScriptEditor.Document.Blocks)
            {
                if (!first) offset += 1; // paragraph separator '\n'
                first = false;

                if (block is Paragraph p)
                {
                    foreach (Inline inline in p.Inlines)
                    {
                        int len = GetInlineLength(inline);
                        if (inline is Hyperlink hl && hl.Tag is CommandGroup g)
                        {
                            list.Add(new HyperlinkInfo
                            {
                                Link = hl,
                                Para = p,
                                ParaIndex = paraIndex,
                                AbsStart = offset,
                                Length = len,
                                Group = g
                            });
                        }
                        offset += len;
                    }
                }
                paraIndex++;
            }
            return list;
        }

        private CommandGroup? GetGroupAtOffset(int offset)
        {
            foreach (var info in EnumerateHyperlinks())
                if (offset >= info.AbsStart && offset < info.AbsStart + info.Length)
                    return info.Group;
            return null;
        }

        private int GetTextOffset(TextPointer p)
        {
            int offset = 0, blockIndex = 0;
            foreach (Block block in ScriptEditor.Document.Blocks)
            {
                if (p.CompareTo(block.ContentStart) >= 0 && p.CompareTo(block.ContentEnd) <= 0)
                    return offset + blockIndex + new TextRange(block.ContentStart, p).Text.Length - DecorationCharsBefore(block, p);
                offset += GetBlockTextLength(block);
                blockIndex++;
            }
            return offset + blockIndex;
        }

        /// <summary>
        /// Characters a raw TextRange counts but the plain-text model does not: the embedded
        /// roll-up chevron appears as a single object-replacement character.
        /// </summary>
        private static int DecorationCharsBefore(Block block, TextPointer p)
        {
            if (block is not Paragraph par) return 0;
            int n = 0;
            foreach (Inline inline in par.Inlines)
                if (inline is InlineUIContainer && inline.ContentStart.CompareTo(p) < 0) n += 1;
            return n;
        }

        private string GetPlainText()
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (Block block in ScriptEditor.Document.Blocks)
            {
                if (!first) sb.Append('\n');
                first = false;

                if (block is Paragraph p)
                {
                    foreach (Inline inline in p.Inlines)
                        sb.Append(GetInlineText(inline));
                }
                else
                {
                    sb.Append(new TextRange(block.ContentStart, block.ContentEnd).Text);
                }
            }
            return sb.ToString();
        }

        private int GetBlockTextLength(Block block)
        {
            if (block is Paragraph p)
            {
                int len = 0;
                foreach (Inline inline in p.Inlines) len += GetInlineLength(inline);
                return len;
            }
            return new TextRange(block.ContentStart, block.ContentEnd).Text.Length;
        }

        private static string GetInlineText(Inline inline)
        {
            if (inline is InlineUIContainer) return string.Empty;   // chevron button: not text
            if (inline is Run run) return run.Text;
            if (inline is Hyperlink hl)
            {
                var sb = new StringBuilder();
                foreach (Inline child in hl.Inlines) sb.Append(GetInlineText(child));
                return sb.ToString();
            }
            return new TextRange(inline.ContentStart, inline.ContentEnd).Text;
        }

        private static int GetInlineLength(Inline inline)
        {
            if (inline is InlineUIContainer) return 0;              // chevron button: no width in text
            if (inline is Run run) return run.Text.Length;
            if (inline is Hyperlink hl)
            {
                int len = 0;
                foreach (Inline child in hl.Inlines) len += GetInlineLength(child);
                return len;
            }
            return new TextRange(inline.ContentStart, inline.ContentEnd).Text.Length;
        }

        #endregion

        private void ModelList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox == null) return;

            // Walk up visual tree to find ListBoxItem and select it before TextBox steals the click
            var depObj = (DependencyObject)e.OriginalSource;
            while (depObj != null && depObj != listBox)
            {
                if (depObj is ListBoxItem lbi)
                {
                    lbi.IsSelected = true;
                    break;
                }
                depObj = VisualTreeHelper.GetParent(depObj);
            }
        }

        private void ModelList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox == null) return;

            // Find the TextBox under the double-click position
            var depObj = e.OriginalSource as DependencyObject;
            while (depObj != null && depObj != listBox)
            {
                if (depObj is TextBox tb)
                {
                    // Make TextBox editable: focusable, give focus, select all text
                    tb.Focusable = true;
                    tb.Focus();
                    tb.SelectAll();
                    tb.LostFocus += EditingTextBox_LostFocus;
                    tb.PreviewKeyDown += EditingTextBox_PreviewKeyDown;
                    return;
                }
                depObj = VisualTreeHelper.GetParent(depObj);
            }
        }

        private void EditingTextBox_LostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                tb.LostFocus -= EditingTextBox_LostFocus;
                tb.PreviewKeyDown -= EditingTextBox_PreviewKeyDown;
                tb.Focusable = false;
            }
        }

        private void EditingTextBox_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            // Press Enter or Escape to finish editing
            if (e.Key == Key.Enter || e.Key == Key.Escape)
            {
                if (sender is TextBox tb)
                {
                    Keyboard.ClearFocus();
                }
            }
        }

        private void ModelList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox == null) return;

            _isDragReady = false;
            _dragStartPoint = e.GetPosition(null);
            _dragSourceItem = null;

            // Find the ListBoxItem under the mouse
            var depObj = e.OriginalSource as DependencyObject;
            while (depObj != null && depObj != listBox)
            {
                if (depObj is ListBoxItem lbi && lbi.Content is ModelConfigViewModel vm)
                {
                    _dragSourceItem = vm;
                    break;
                }
                depObj = VisualTreeHelper.GetParent(depObj);
            }

            if (_dragSourceItem != null)
            {
                // Start 2-second timer
                _longPressTimer.Start();
            }
        }

        private void ModelList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Always restore cursor and cancel timer on mouse release
            _longPressTimer.Stop();
            Mouse.OverrideCursor = null;
            _isDragReady = false;
        }

        private void LongPressTimer_Tick(object? sender, EventArgs e)
        {
            _longPressTimer.Stop();
            _isDragReady = true;
            Mouse.OverrideCursor = Cursors.Hand;
            ModelList.MouseMove += ModelList_MouseMoveForDrag;
        }

        private void ModelList_MouseMoveForDrag(object sender, MouseEventArgs e)
        {
            if (!_isDragReady) return;

            // Start drag operation immediately when mouse moves after long press
            ModelList.MouseMove -= ModelList_MouseMoveForDrag;

            if (_dragSourceItem != null)
            {
                _dragSourceItem.IsDragSource = true;

                var data = new DataObject();
                data.SetData(typeof(ModelConfigViewModel), _dragSourceItem);
                DragDrop.DoDragDrop(ModelList, data, DragDropEffects.Move);

                _dragSourceItem.IsDragSource = false;
                _dragSourceItem = null;
                Mouse.OverrideCursor = null;
                _isDragReady = false;
            }
        }

        private void ListBox_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetData(typeof(ModelConfigViewModel)) is ModelConfigViewModel ? DragDropEffects.Move : DragDropEffects.None;

            // Clear previous drop target
            foreach (var m in _viewModel.Models)
            {
                if (m.IsDropTarget) m.IsDropTarget = false;
            }

            // Set new drop target
            if (sender is ListBox listBox &&
                FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource) is ListBoxItem targetItem &&
                targetItem.Content is ModelConfigViewModel targetVm)
            {
                targetVm.IsDropTarget = true;
            }
        }

        private void ListBox_Drop(object sender, DragEventArgs e)
        {
            var draggedItem = e.Data.GetData(typeof(ModelConfigViewModel)) as ModelConfigViewModel;
            if (draggedItem == null) return;

            // Find drop target
            var targetItem = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
            var targetVm = targetItem?.Content as ModelConfigViewModel;

            // Clear visual states
            draggedItem.IsDragSource = false;
            foreach (var m in _viewModel.Models)
            {
                m.IsDropTarget = false;
            }

            if (targetVm == null || targetVm == draggedItem) return;

            var targetIndex = _viewModel.Models.IndexOf(targetVm);
            var oldIndex = _viewModel.Models.IndexOf(draggedItem);
            if (oldIndex < 0 || targetIndex < 0) return;

            // Determine if we should insert before or after the target
            if (targetIndex > oldIndex)
            {
                targetIndex--;
            }

            _viewModel.MoveModel(draggedItem, targetIndex);
        }

        // --- Splitter drag-to-resize ---
        private bool _isResizing = false;

        private void Splitter_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isResizing = true;
            Mouse.OverrideCursor = Cursors.SizeWE;
            if (sender is UIElement ui)
            {
                ui.CaptureMouse();
            }
        }

        private void Splitter_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isResizing) return;

            var mousePos = Mouse.GetPosition(MainContentGrid);
            var newWidth = mousePos.X;

            // Clamp between min and max
            const double minWidth = 180;
            double maxWidth = MainContentGrid.ActualWidth - 180 - 6; // 6 for splitter width
            if (newWidth < minWidth) newWidth = minWidth;
            if (newWidth > maxWidth) newWidth = maxWidth;

            var firstCol = MainContentGrid.ColumnDefinitions[0];
            firstCol.Width = new GridLength(newWidth);
        }

        private void Splitter_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isResizing = false;
            Mouse.OverrideCursor = null;
            if (sender is UIElement ui && ui.IsMouseCaptured)
            {
                ui.ReleaseMouseCapture();
            }
        }

        private void Splitter_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isResizing)
            {
                Mouse.OverrideCursor = Cursors.SizeWE;
            }
        }

        private void Splitter_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isResizing)
            {
                Mouse.OverrideCursor = null;
            }
        }

        private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
        {
            var current = start;
            while (current != null)
            {
                if (current is T found) return found;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            // Restoring from the taskbar doesn't always activate the window;
            // if it ends up not in the foreground, force it to the top.
            if (WindowState != WindowState.Normal) return;

            Dispatcher.BeginInvoke(new Action(EnsureForegroundAfterRestore), DispatcherPriority.ApplicationIdle);
        }

        private void EnsureForegroundAfterRestore()
        {
            if (IsActive) return;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd == hwnd) return;

            // Attach current thread input to the foreground/target threads to
            // bypass SetForegroundWindow restrictions
            uint fgThreadId = GetWindowThreadProcessId(fgHwnd, out _);
            uint targetThreadId = GetWindowThreadProcessId(hwnd, out _);
            uint currentThreadId = GetCurrentThreadId();

            bool attachedFg = fgThreadId != 0 && fgThreadId != currentThreadId
                && AttachThreadInput(fgThreadId, currentThreadId, true);
            bool attachedTarget = targetThreadId != 0 && targetThreadId != currentThreadId && targetThreadId != fgThreadId
                && AttachThreadInput(targetThreadId, currentThreadId, true);

            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attachedTarget)
                {
                    AttachThreadInput(targetThreadId, currentThreadId, false);
                }
                if (attachedFg)
                {
                    AttachThreadInput(fgThreadId, currentThreadId, false);
                }
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _longPressTimer.Stop();
            _longPressTimer.Tick -= LongPressTimer_Tick;
            ModelList.PreviewMouseLeftButtonDown -= ModelList_PreviewMouseLeftButtonDown;
            ModelList.PreviewMouseLeftButtonUp -= ModelList_PreviewMouseLeftButtonUp;
            ModelList.MouseDoubleClick -= ModelList_MouseDoubleClick;
            ModelList.MouseMove -= ModelList_MouseMoveForDrag;
            ScriptEditor.TextChanged -= ScriptEditor_TextChanged;
            DataObject.RemovePastingHandler(ScriptEditor, Editor_Pasting);
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ScriptEditor.PreviewMouseMove -= Editor_PreviewMouseMove;
            ScriptEditor.MouseLeave -= Editor_MouseLeave;
            ScriptEditor.PreviewMouseLeftButtonDown -= Editor_PreviewMouseLeftButtonDown;
            ScriptEditor.PreviewMouseLeftButtonUp -= Editor_PreviewMouseLeftButtonUp;
            _viewModel.SaveConfig();
            base.OnClosing(e);
        }
    }
}
