// Editable list of items: the list is on the left, the fields of the selected item on the right.
// Buttons below the list add, duplicate, remove and reorder entries.
//
// Layout uses docking only (no auto-sizing tables around the list), so it behaves the same on every
// Windows version and DPI. The owner of the editor puts its field controls into Detail.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class ListEditor<T> : Panel where T : class
    {
        readonly ListBox list = new ListBox();
        readonly Panel left = new Panel();
        readonly Panel host = new Panel();
        readonly Label empty = new Label();
        readonly Button addBtn, copyBtn, removeBtn, upBtn, downBtn;
        readonly Font boldFont;
        List<T> items = new List<T>();
        int suppress;
        T lastSelected;

        /// <summary>The owner adds its field controls here.</summary>
        public readonly Panel Detail = new Panel();

        public Func<T> Create;
        public Func<T, T> Copy;
        public Func<T, string> Title = delegate(T t) { return t.ToString(); };
        public Func<T, string> Subtitle = delegate(T t) { return ""; };
        /// <summary>Short text on the right of the title line (for example "7 days").</summary>
        public Func<T, string> Badge = delegate(T t) { return ""; };
        public Func<T, bool> IsOn = delegate(T t) { return true; };

        /// <summary>Shown when the list is empty.</summary>
        public string EmptyText
        {
            get { return empty.Text; }
            set { empty.Text = value; }
        }

        /// <summary>Extra sentence in the confirmation shown before an entry is removed.</summary>
        public string RemoveNote = "";

        /// <summary>The selected entry changed (also raised once after SetItems).</summary>
        public event EventHandler SelectedChanged;
        /// <summary>Entries were added, removed or reordered by the user.</summary>
        public event EventHandler Changed;

        public ListEditor(string noun) : this(noun, false, 0) { }

        /// <summary>stacked: list on top (listHeight pixels, 0 = automatic), buttons in one row under it, fields below.</summary>
        public ListEditor(string noun, bool stacked, int listHeight)
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(0);

            Font f = Ui.BaseFont();
            Font = f;
            boldFont = Ui.Derive(f, 1.0f, FontStyle.Bold);
            int line = f.Height;

            // ---- detail side (added first: it fills whatever the list side leaves over)
            host.Dock = DockStyle.Fill;
            host.Padding = stacked ? new Padding(0, 10, 0, 0) : new Padding(16, 0, 0, 0);
            Detail.Dock = DockStyle.Fill;
            Detail.AutoScroll = true;
            Detail.MinimumSize = new Size(160, 120);    // a nearly empty scroll area trips a scrollbar bug on some runtimes
            empty.Dock = DockStyle.Fill;
            empty.ForeColor = SystemColors.GrayText;
            empty.TextAlign = ContentAlignment.MiddleCenter;
            empty.Text = "Nothing in the list yet.";
            host.Controls.Add(Detail);
            host.Controls.Add(empty);
            Controls.Add(host);

            // ---- list side
            left.Dock = stacked ? DockStyle.Top : DockStyle.Left;
            if (stacked) left.Height = listHeight > 0 ? listHeight : (boldFont.Height + line + 14) * 3 + line * 2 + 22;
            else left.Width = Math.Max(250, line * 17);

            list.Dock = DockStyle.Fill;
            list.Font = f;
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = boldFont.Height + line + 14;
            list.IntegralHeight = false;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.BackColor = SystemColors.Window;
            list.DrawItem += OnDrawItem;
            // Only a different entry counts as a selection change (the control can repeat the event).
            list.SelectedIndexChanged += delegate
            {
                if (suppress == 0 && !ReferenceEquals(Selected, lastSelected)) RaiseSelected();
                UpdateButtons();
            };

            addBtn = Cell("Add " + noun, delegate { Add(); });
            copyBtn = Cell("Duplicate", delegate { Duplicate(); });
            removeBtn = Cell("Remove", delegate { Remove(); });
            upBtn = Cell("Move up", delegate { MoveItem(-1); });
            downBtn = Cell("Move down", delegate { MoveItem(1); });

            int rowH = line * 2 + 6;
            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Bottom;
            if (stacked)
            {
                grid.Height = rowH + 8;
                grid.Padding = new Padding(0, 6, 0, 0);
                grid.ColumnCount = 5;
                grid.RowCount = 1;
                for (int c = 0; c < 5; c++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
                grid.Controls.Add(addBtn, 0, 0);
                grid.Controls.Add(copyBtn, 1, 0);
                grid.Controls.Add(removeBtn, 2, 0);
                grid.Controls.Add(upBtn, 3, 0);
                grid.Controls.Add(downBtn, 4, 0);
            }
            else
            {
                grid.Height = rowH * 2 + 14;
                grid.Padding = new Padding(0, 8, 0, 0);
                grid.ColumnCount = 3;
                grid.RowCount = 2;
                for (int c = 0; c < 3; c++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
                grid.Controls.Add(addBtn, 0, 0);
                grid.SetColumnSpan(addBtn, 2);
                grid.Controls.Add(removeBtn, 2, 0);
                grid.Controls.Add(copyBtn, 0, 1);
                grid.Controls.Add(upBtn, 1, 1);
                grid.Controls.Add(downBtn, 2, 1);
            }

            left.Controls.Add(list);     // Fill first
            left.Controls.Add(grid);     // Bottom after
            Controls.Add(left);          // Left after the Fill side

            // Some runtimes draw the list scrolled when items were selected before it was first shown.
            list.VisibleChanged += delegate { ScrollToSelection(); };
            list.HandleCreated += delegate { ScrollToSelection(); };
            UpdateButtons();
        }

        void ScrollToSelection()
        {
            try
            {
                if (list.Items.Count == 0) return;
                int sel = Math.Max(0, list.SelectedIndex);
                list.TopIndex = 0;
                if (sel > 2) list.TopIndex = sel - 2;
            }
            catch (Exception) { }
        }

        static Button Cell(string text, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(0, 0, 6, 6);
            b.UseVisualStyleBackColor = true;
            b.Click += click;
            return b;
        }

        // ------------------------------------------------------------------ data
        public List<T> Items { get { return items; } }

        public T Selected
        {
            get
            {
                int i = list.SelectedIndex;
                return i >= 0 && i < items.Count ? items[i] : null;
            }
        }

        public int SelectedIndex { get { return list.SelectedIndex; } }

        /// <summary>Redraw every entry (status shown in titles changed).</summary>
        public void RefreshAll() { list.Invalidate(); }

        public string AddText { get { return addBtn.Text; } set { addBtn.Text = value; } }

        /// <summary>Show these entries. The list is edited in place.</summary>
        public void SetItems(List<T> source)
        {
            int keep = list.SelectedIndex;      // stay on the same row after a save or reload
            items = source ?? new List<T>();
            Rebuild(items.Count == 0 ? -1 : (keep >= 0 && keep < items.Count ? keep : 0));
        }

        public void Select(T item)
        {
            int i = items.IndexOf(item);
            if (i >= 0 && i != list.SelectedIndex) list.SelectedIndex = i;
        }

        /// <summary>Redraw the selected entry after its fields changed.</summary>
        public void RefreshSelected()
        {
            int i = list.SelectedIndex;
            if (i < 0 || i >= list.Items.Count) return;
            try { list.Invalidate(list.GetItemRectangle(i)); }
            catch (Exception) { list.Invalidate(); }
        }

        void Rebuild(int select)
        {
            suppress++;
            try
            {
                list.BeginUpdate();
                list.Items.Clear();
                foreach (T t in items) list.Items.Add(t);
                list.EndUpdate();
                if (select >= 0 && select < list.Items.Count) list.SelectedIndex = select;
                if (list.Items.Count > 0) list.TopIndex = 0;
                if (select >= 0 && select < list.Items.Count) list.TopIndex = Math.Max(0, select - 2);
            }
            finally { suppress--; }
            ShowDetail();
            UpdateButtons();
            RaiseSelected();
        }

        void ShowDetail()
        {
            bool any = items.Count > 0;
            Detail.Visible = any;
            empty.Visible = !any;
        }

        void RaiseSelected()
        {
            lastSelected = Selected;
            EventHandler h = SelectedChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        void RaiseChanged()
        {
            EventHandler h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        void UpdateButtons()
        {
            int i = list.SelectedIndex;
            copyBtn.Enabled = i >= 0 && Copy != null;
            removeBtn.Enabled = i >= 0;
            upBtn.Enabled = i > 0;
            downBtn.Enabled = i >= 0 && i < items.Count - 1;
            addBtn.Enabled = Create != null;
        }

        // --------------------------------------------------------------- actions
        void Add()
        {
            if (Create == null) return;
            T item = Create();
            if (item == null) return;
            items.Add(item);
            Rebuild(items.Count - 1);
            RaiseChanged();
        }

        void Duplicate()
        {
            T cur = Selected;
            if (cur == null || Copy == null) return;
            T item = Copy(cur);
            if (item == null) return;
            int at = list.SelectedIndex + 1;
            items.Insert(at, item);
            Rebuild(at);
            RaiseChanged();
        }

        void Remove()
        {
            T cur = Selected;
            if (cur == null) return;
            string name = Title(cur);
            string text = "Remove \"" + (name.Length > 0 ? name : "this entry") + "\" from the list?";
            if (RemoveNote.Length > 0) text += " " + RemoveNote;
            if (!Ui.Confirm(FindForm(), text)) return;
            int at = list.SelectedIndex;
            items.RemoveAt(at);
            Rebuild(Math.Min(at, items.Count - 1));
            RaiseChanged();
        }

        void MoveItem(int delta)
        {
            int i = list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= items.Count) return;
            T t = items[i];
            items.RemoveAt(i);
            items.Insert(j, t);
            suppress++;
            try
            {
                list.BeginUpdate();
                list.Items.Clear();
                foreach (T x in items) list.Items.Add(x);
                list.EndUpdate();
                list.SelectedIndex = j;
            }
            finally { suppress--; }
            UpdateButtons();
            RaiseChanged();
        }

        // --------------------------------------------------------------- drawing
        void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= items.Count) return;
            T it = items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            bool on = IsOn(it);

            using (Brush b = new SolidBrush(sel ? SystemColors.Highlight : SystemColors.Window))
                e.Graphics.FillRectangle(b, e.Bounds);

            Color fore = sel ? SystemColors.HighlightText : (on ? SystemColors.WindowText : SystemColors.GrayText);
            Color sub = sel ? SystemColors.HighlightText : SystemColors.GrayText;

            int pad = 10, d = 9;
            Rectangle dot = new Rectangle(e.Bounds.X + pad, e.Bounds.Y + (e.Bounds.Height - d) / 2, d, d);
            System.Drawing.Drawing2D.SmoothingMode old = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Brush b = new SolidBrush(on ? Ui.Ok : Ui.Off)) e.Graphics.FillEllipse(b, dot);
            e.Graphics.SmoothingMode = old;

            int x = dot.Right + 9;
            int w = Math.Max(10, e.Bounds.Right - x - pad);
            int top = e.Bounds.Y + 6;
            string badge = Badge(it) ?? "";
            int bw = 0;
            if (badge.Length > 0) bw = Math.Min(w / 2, TextRenderer.MeasureText(badge, list.Font).Width + 6);
            Rectangle r1 = new Rectangle(x, top, w - bw, boldFont.Height + 2);
            Rectangle rb = new Rectangle(x + w - bw, top, bw, boldFont.Height + 2);
            Rectangle r2 = new Rectangle(x, r1.Bottom, w, list.Font.Height + 2);
            TextFormatFlags fl = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                                 TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;

            string title = Title(it) ?? "";
            if (title.Trim().Length == 0) title = "(no name)";
            if (!on) title += "  (off)";
            string subtitle = Subtitle(it) ?? "";
            TextRenderer.DrawText(e.Graphics, title, boldFont, r1, fore, fl);
            if (bw > 0)
                TextRenderer.DrawText(e.Graphics, badge, list.Font, rb, sub, TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(e.Graphics, subtitle, list.Font, r2, sub, fl);

            using (Pen p = new Pen(SystemColors.ControlLight))
                e.Graphics.DrawLine(p, e.Bounds.X, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) boldFont.Dispose();
            base.Dispose(disposing);
        }
    }
}
