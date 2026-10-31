// A list of entries (watchdog entries, folder tasks, ...) on the left with Add / Duplicate / Remove / Up / Down, and the
// selected entry's fields on the right.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class ListEditor<T> : SplitContainer where T : class, new()
    {
        public readonly ListBox List = new ListBox();
        public readonly Panel Detail = new Panel();
        readonly Label empty = new Label();
        List<T> items = new List<T>();
        public Func<T, string> Title = delegate(T x) { return x.ToString(); };
        public Func<T, string> Subtitle = delegate(T x) { return ""; };
        public Func<T, Color> Mark = delegate(T x) { return Ui.Off; };
        public Func<T> Create = delegate { return new T(); };
        public Func<T, T> Copy;
        public event Action<T> Selected;
        public event Action ListChanged;
        public readonly FlowLayoutPanel Buttons;
        bool filling;

        public ListEditor(string emptyText)
        {
            Dock = DockStyle.Fill;
            FixedPanel = FixedPanel.Panel1;
            SplitterWidth = 6;
            List.Dock = DockStyle.Fill;
            List.IntegralHeight = false;
            List.DrawMode = DrawMode.OwnerDrawFixed;
            List.ItemHeight = Ui.BaseFont().Height * 2 + 10;
            List.DrawItem += DrawItem;
            List.SelectedIndexChanged += delegate { if (!filling) ShowCurrent(); };
            List.BorderStyle = BorderStyle.FixedSingle;

            Buttons = new FlowLayoutPanel();
            Buttons.Dock = DockStyle.Bottom;
            Buttons.AutoSize = true;
            Buttons.Padding = new Padding(0, 6, 0, 0);
            Buttons.Controls.Add(Ui.Button("Add", delegate { Add(Create()); }));
            Buttons.Controls.Add(Ui.Button("Duplicate", delegate { T s = Current; if (s != null && Copy != null) Add(Copy(s)); }));
            Buttons.Controls.Add(Ui.Button("Remove", delegate { Remove(); }));
            Buttons.Controls.Add(Ui.Button("Up", delegate { MoveBy(-1); }));
            Buttons.Controls.Add(Ui.Button("Down", delegate { MoveBy(1); }));
            Panel1.Padding = new Padding(0, 0, 4, 0);
            Panel1.Controls.Add(List);
            Panel1.Controls.Add(Buttons);

            Detail.Dock = DockStyle.Fill;
            Detail.AutoScroll = true;
            Detail.Padding = new Padding(12, 0, 8, 8);
            empty.Text = emptyText;
            empty.AutoSize = true;
            empty.ForeColor = Ui.Help;
            empty.Margin = new Padding(12);
            empty.Location = new Point(12, 8);
            Panel2.Controls.Add(Detail);
            Panel2.Controls.Add(empty);
            bool sized = false;
            Resize += delegate
            {
                int want = Math.Max(220, Ui.BaseFont().Height * 20);
                if (sized || Width < want + 200) return;
                sized = true;
                try { SplitterDistance = want; } catch (Exception) { }
            };
        }

        public T Current { get { int i = List.SelectedIndex; return i >= 0 && i < items.Count ? items[i] : null; } }

        public void SetItems(List<T> list)
        {
            items = list;
            Refill(list.Count > 0 ? 0 : -1);
        }

        public void Refill(int select)
        {
            filling = true;
            List.BeginUpdate();
            List.Items.Clear();
            foreach (T x in items) List.Items.Add(x);
            List.EndUpdate();
            filling = false;
            try { List.SelectedIndex = select < items.Count ? select : items.Count - 1; }
            catch (ArgumentOutOfRangeException) { }       // Mono, before the list is laid out
            ShowCurrent();
        }

        /// <summary>Redraw the titles (after a field changed).</summary>
        public void RefreshTitles() { List.Invalidate(); }

        void ShowCurrent()
        {
            T c = Current;
            Detail.Visible = c != null;
            empty.Visible = c == null;
            if (Selected != null) Selected(c);
        }

        public void Select(T x) { int i = items.IndexOf(x); if (i >= 0) List.SelectedIndex = i; }

        void Add(T x)
        {
            if (x == null) return;
            items.Add(x);
            Refill(items.Count - 1);
            if (ListChanged != null) ListChanged();
        }

        void Remove()
        {
            T c = Current;
            if (c == null) return;
            if (!Ui.Confirm(FindForm(), "Remove '" + Title(c) + "'?")) return;
            int i = items.IndexOf(c);
            items.Remove(c);
            Refill(Math.Min(i, items.Count - 1));
            if (ListChanged != null) ListChanged();
        }

        void MoveBy(int by)
        {
            T c = Current;
            if (c == null) return;
            int i = items.IndexOf(c), j = i + by;
            if (j < 0 || j >= items.Count) return;
            items[i] = items[j];
            items[j] = c;
            Refill(j);
            if (ListChanged != null) ListChanged();
        }

        void DrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= items.Count) return;
            T x = items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Color fore = sel ? SystemColors.HighlightText : SystemColors.ControlText;
            Color sub = sel ? SystemColors.HighlightText : Ui.Help;
            int h = Ui.BaseFont().Height;
            using (SolidBrush b = new SolidBrush(Mark(x))) e.Graphics.FillEllipse(b, e.Bounds.Left + 6, e.Bounds.Top + 8, 9, 9);
            using (Font bold = new Font(Ui.BaseFont(), FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, Title(x), bold, new Rectangle(e.Bounds.Left + 22, e.Bounds.Top + 3, e.Bounds.Width - 26, h + 2), fore, TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, Subtitle(x), Ui.BaseFont(), new Rectangle(e.Bounds.Left + 22, e.Bounds.Top + 4 + h, e.Bounds.Width - 26, h + 2), sub, TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        }
    }
}
