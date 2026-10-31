// Building blocks of the manager: a form grid (label, field, help text), a binder that ties fields to the settings being
// edited, and small dialogs.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public static class Ui
    {
        public static readonly Color Back = Color.White;
        public static readonly Color Side = Color.FromArgb(244, 245, 247);
        public static readonly Color Help = Color.FromArgb(105, 110, 118);
        public static readonly Color Good = Color.FromArgb(26, 127, 55);
        public static readonly Color Warn = Color.FromArgb(176, 108, 0);
        public static readonly Color Bad = Color.FromArgb(196, 43, 28);
        public static readonly Color Off = Color.FromArgb(130, 130, 130);
        public static readonly Color Accent = Color.FromArgb(0, 95, 184);

        static Font baseFont;
        public static Font BaseFont()
        {
            if (baseFont == null)
            {
                try { baseFont = new Font("Segoe UI", 9f); } catch (Exception) { baseFont = SystemFonts.MessageBoxFont; }
            }
            return baseFont;
        }

        public static Font Bold(float size) { return new Font(BaseFont().FontFamily, size, FontStyle.Bold); }

        public static Color StateColor(string state)
        {
            switch (state)
            {
                case "running": case "connected": case "idle": case "ok": case "done": return Good;
                case "waiting": case "starting": case "queued": case "converting": case "connecting": case "retry": case "paused": return Warn;
                case "error": case "failed": case "failing": return Bad;
            }
            return Off;
        }

        public static Label Label(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(0, 6, 8, 2);
            return l;
        }

        public static Label HelpText(string text, int width)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.MaximumSize = new Size(width, 0);
            l.ForeColor = Help;
            l.Margin = new Padding(0, 0, 0, 6);
            return l;
        }

        public static Button Button(string text, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(6, 1, 6, 1);
            b.Margin = new Padding(0, 2, 6, 2);
            b.UseVisualStyleBackColor = true;
            if (click != null) b.Click += click;
            return b;
        }

        public static FlowLayoutPanel Flow(params Control[] items)
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = false;
            f.Margin = new Padding(0);
            foreach (Control c in items) f.Controls.Add(c);
            return f;
        }

        /// <summary>Tests: answers message boxes instead of a person (kind: info | error | confirm | text); returns the answer to a question.</summary>
        public static Func<string, string, bool> MessageHook;

        public static void Info(IWin32Window owner, string text)
        {
            if (MessageHook != null) { MessageHook("info", text); return; }
            MessageBox.Show(owner, text, "Watchkeeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static void Error(IWin32Window owner, string text)
        {
            if (MessageHook != null) { MessageHook("error", text); return; }
            MessageBox.Show(owner, text, "Watchkeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public static bool Confirm(IWin32Window owner, string text)
        {
            if (MessageHook != null) return MessageHook("confirm", text);
            return MessageBox.Show(owner, text, "Watchkeeper", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        /// <summary>Show text in a resizable window (results, reports, previews).</summary>
        public static void ShowText(IWin32Window owner, string title, string text)
        {
            if (MessageHook != null) { MessageHook("text", title + "\n" + text); return; }
            using (Form f = new Form())
            {
                f.Text = title;
                f.Font = BaseFont();
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(f.Font.Height * 60, f.Font.Height * 30);
                f.ShowInTaskbar = false;
                f.MinimizeBox = false;
                TextBox t = new TextBox();
                t.Multiline = true;
                t.ReadOnly = true;
                t.ScrollBars = ScrollBars.Both;
                t.WordWrap = false;
                t.Dock = DockStyle.Fill;
                t.Font = new Font(FontFamily.GenericMonospace, 9f);
                t.Text = (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
                t.BackColor = Back;
                Panel bar = new Panel();
                bar.Dock = DockStyle.Bottom;
                bar.Height = f.Font.Height * 2 + 16;
                Button close = Button("Close", delegate { f.Close(); });
                close.Location = new Point(8, 6);
                bar.Controls.Add(close);
                f.Controls.Add(t);
                f.Controls.Add(bar);
                f.CancelButton = close;
                f.ShowDialog(owner);
            }
        }

        /// <summary>Show a dialog without blocking a test harness (the harness closes it).</summary>
        public static Func<Form, bool> DialogHook;
    }

    /// <summary>One labelled field (with optional help) in a FormGrid; hides and shows as a whole.</summary>
    public class FieldRow
    {
        public readonly List<Control> Parts = new List<Control>();
        public void Show(bool on) { foreach (Control c in Parts) c.Visible = on; }
        public Control Field;
    }

    /// <summary>Two columns: labels on the left, fields on the right, help text under a field.</summary>
    public class FormGrid : TableLayoutPanel
    {
        public int HelpWidth = 560;

        public FormGrid()
        {
            ColumnCount = 2;
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(0);
            Margin = new Padding(0);
        }

        public FieldRow Row(string label, Control field) { return Row(label, field, null); }

        public FieldRow Row(string label, Control field, string help)
        {
            FieldRow r = new FieldRow();
            r.Field = field;
            int row = RowCount++;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label l = Ui.Label(label ?? "");
            Controls.Add(l, 0, row);
            field.Margin = new Padding(0, 3, 0, 3);
            Controls.Add(field, 1, row);
            r.Parts.Add(l);
            r.Parts.Add(field);
            if (!string.IsNullOrEmpty(help))
            {
                int hr = RowCount++;
                RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Label h = Ui.HelpText(help, HelpWidth);
                Controls.Add(h, 1, hr);
                r.Parts.Add(h);
            }
            return r;
        }

        public Label Section(string title)
        {
            int row = RowCount++;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label l = new Label();
            l.Text = title;
            l.AutoSize = true;
            l.Font = Ui.Bold(10f);
            l.Margin = new Padding(0, 14, 0, 4);
            Controls.Add(l, 0, row);
            SetColumnSpan(l, 2);
            return l;
        }

        public Label Note(string text)
        {
            int row = RowCount++;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label h = Ui.HelpText(text, HelpWidth + 160);
            Controls.Add(h, 0, row);
            SetColumnSpan(h, 2);
            return h;
        }
    }

    /// <summary>Ties fields to values in the settings being edited: Load copies values into the fields; a change in a
    /// field writes the value back and calls Changed.</summary>
    public class Binder
    {
        readonly List<Action> loaders = new List<Action>();
        public bool Loading;
        public event Action Changed;
        public const string Hidden = "••••••••";

        void Fire() { if (!Loading && Changed != null) Changed(); }

        public void Load()
        {
            Loading = true;
            try { foreach (Action a in loaders) a(); }
            finally { Loading = false; }
        }

        public TextBox Text(int width, Func<string> get, Action<string> set)
        {
            TextBox t = new TextBox();
            t.Width = width;
            loaders.Add(delegate { t.Text = get() ?? ""; });
            t.TextChanged += delegate { if (!Loading) { set(t.Text); Fire(); } };
            return t;
        }

        /// <summary>A password: shown as dots when one is stored; typing replaces it; emptying removes it.</summary>
        public TextBox Password(int width, Func<string> get, Action<string> set)
        {
            TextBox t = new TextBox();
            t.Width = width;
            t.UseSystemPasswordChar = true;
            loaders.Add(delegate { t.Text = string.IsNullOrEmpty(get()) ? "" : Hidden; });
            t.TextChanged += delegate { if (!Loading && t.Text != Hidden) { set(t.Text); Fire(); } };
            t.Enter += delegate { if (t.Text == Hidden) t.SelectAll(); };
            return t;
        }

        public CheckBox Check(string label, Func<bool> get, Action<bool> set)
        {
            CheckBox c = new CheckBox();
            c.Text = label;
            c.AutoSize = true;
            loaders.Add(delegate { c.Checked = get(); });
            c.CheckedChanged += delegate { if (!Loading) { set(c.Checked); Fire(); } };
            return c;
        }

        public NumericUpDown Number(decimal min, decimal max, int decimals, Func<double> get, Action<double> set)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.DecimalPlaces = decimals;
            n.Width = 90;
            loaders.Add(delegate { n.Value = Math.Max(min, Math.Min(max, (decimal)get())); });
            n.ValueChanged += delegate { if (!Loading) { set((double)n.Value); Fire(); } };
            return n;
        }

        /// <summary>A drop-down list of (value, text) pairs.</summary>
        public ComboBox Choice(string[][] items, Func<string> get, Action<string> set)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            int w = 80;
            foreach (string[] i in items)
            {
                c.Items.Add(i[1]);
                w = Math.Max(w, TextRenderer.MeasureText(i[1], Ui.BaseFont()).Width + 40);
            }
            c.Width = w;
            loaders.Add(delegate
            {
                string v = get();
                int idx = Array.FindIndex(items, delegate(string[] i) { return i[0] == v; });
                c.SelectedIndex = idx >= 0 ? idx : 0;
            });
            c.SelectedIndexChanged += delegate { if (!Loading && c.SelectedIndex >= 0) { set(items[c.SelectedIndex][0]); Fire(); } };
            return c;
        }

        /// <summary>Re-run Changed-handlers' effects (enabling) without writing anything.</summary>
        public void Add(Action loader) { loaders.Add(loader); }
    }
}
