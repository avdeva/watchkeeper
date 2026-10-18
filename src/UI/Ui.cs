// Small UI toolkit: helpers, a label/field grid, a path picker and a schedule editor.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class Choice
    {
        public readonly string Value;
        public readonly string Text;
        public Choice(string value, string text) { Value = value; Text = text; }
        public override string ToString() { return Text; }
    }

    /// <summary>A value the user typed that cannot be used. Shown next to the Save button.</summary>
    public class UiInputError : Exception
    {
        public UiInputError(string message) : base(message) { }
    }

    public static class Ui
    {
        public static readonly Color Ok = Color.FromArgb(40, 160, 90);
        public static readonly Color Warn = Color.FromArgb(220, 150, 20);
        public static readonly Color Bad = Color.FromArgb(205, 60, 50);
        public static readonly Color Off = Color.FromArgb(150, 156, 165);
        public static readonly Color Accent = Color.FromArgb(0, 103, 192);

        public static Font BaseFont()
        {
            try { return SystemFonts.MessageBoxFont; }
            catch (Exception) { return Control.DefaultFont; }
        }

        public static Font Derive(Font f, float scale, FontStyle style)
        {
            return new Font(f.FontFamily, f.Size * scale, style);
        }

        public static Font MonoFont(Font like)
        {
            foreach (string name in new[] { "Cascadia Mono", "Consolas", "DejaVu Sans Mono", "Courier New" })
            {
                try
                {
                    Font f = new Font(name, like.Size);
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
                    f.Dispose();
                }
                catch (Exception) { }
            }
            return new Font(FontFamily.GenericMonospace, like.Size);
        }

        public static Label Lbl(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.UseMnemonic = false;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(0, 6, 8, 6);
            return l;
        }

        public static Label Hint(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.UseMnemonic = false;
            l.AutoSize = true;
            l.MaximumSize = new Size(640, 0);
            l.ForeColor = SystemColors.GrayText;
            l.Margin = new Padding(0, 0, 0, 6);
            return l;
        }

        public static Button Btn(string text, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(88, 28);
            b.Padding = new Padding(6, 0, 6, 0);
            b.UseVisualStyleBackColor = true;
            b.Margin = new Padding(0, 0, 8, 0);
            if (click != null) b.Click += click;
            return b;
        }

        public static CheckBox Chk(string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Anchor = AnchorStyles.Left;
            c.Margin = new Padding(0, 4, 0, 4);
            return c;
        }

        public static RadioButton Radio(string text)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.AutoSize = true;
            r.Margin = new Padding(0, 2, 16, 2);
            return r;
        }

        public static NumericUpDown Num(decimal min, decimal max, decimal step, int decimals)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Increment = step;
            n.DecimalPlaces = decimals;
            n.Width = 104;
            n.TextAlign = HorizontalAlignment.Left;
            n.Anchor = AnchorStyles.Left;
            n.Margin = new Padding(0, 3, 6, 3);
            return n;
        }

        public static void SetNum(NumericUpDown n, double v)
        {
            decimal d = (decimal)v;
            n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, d));
        }

        public static ComboBox Combo(params Choice[] items)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (Choice i in items) c.Items.Add(i);
            if (c.Items.Count > 0) c.SelectedIndex = 0;
            c.Anchor = AnchorStyles.Left;
            c.Margin = new Padding(0, 3, 6, 3);
            int w = 120;
            foreach (Choice i in items) w = Math.Max(w, (int)(TextRenderer.MeasureText(i.Text, c.Font).Width * 1.12) + 50);
            c.Width = w;
            return c;
        }

        public static string Get(ComboBox c)
        {
            Choice ch = c.SelectedItem as Choice;
            return ch != null ? ch.Value : "";
        }

        public static void Set(ComboBox c, string value)
        {
            for (int i = 0; i < c.Items.Count; i++)
            {
                Choice ch = c.Items[i] as Choice;
                if (ch != null && ch.Value == value) { c.SelectedIndex = i; return; }
            }
            if (c.Items.Count > 0) c.SelectedIndex = 0;
        }

        public static DateTimePicker TimeBox()
        {
            DateTimePicker t = new DateTimePicker();
            t.Format = DateTimePickerFormat.Custom;
            t.CustomFormat = "HH:mm";
            t.ShowUpDown = true;
            t.Width = 70;
            t.Anchor = AnchorStyles.Left;
            t.Margin = new Padding(0, 3, 6, 3);
            return t;
        }

        public static void SetTime(DateTimePicker t, string hhmm)
        {
            TimeSpan ts;
            try { ts = Util.ParseTime(hhmm); } catch (FormatException) { ts = TimeSpan.Zero; }
            t.Value = DateTime.Today.Add(ts);
        }

        public static string GetTime(DateTimePicker t)
        {
            return t.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        public static TextBox Text(int width)
        {
            TextBox t = new TextBox();
            t.Width = width;
            t.Anchor = AnchorStyles.Left;
            t.Margin = new Padding(0, 3, 6, 3);
            return t;
        }

        public static FlowLayoutPanel Flow(params Control[] items)
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = false;
            f.FlowDirection = FlowDirection.LeftToRight;
            f.Margin = new Padding(0);
            f.Anchor = AnchorStyles.Left;
            f.Controls.AddRange(items);
            return f;
        }

        public static void Info(IWin32Window owner, string text)
        {
            MessageBox.Show(owner, text, "Watchkeeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static void Error(IWin32Window owner, string text)
        {
            MessageBox.Show(owner, text, "Watchkeeper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        public static bool Confirm(IWin32Window owner, string text)
        {
            return MessageBox.Show(owner, text, "Watchkeeper", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        public static double ParseNumber(string text, string what)
        {
            double d;
            string t = (text ?? "").Trim();
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out d) ||
                double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new UiInputError(what + ": '" + t + "' is not a number");
        }

        /// <summary>"14:03:12", "yesterday 03:00", "Mon 6 Oct 03:00" or "never".</summary>
        public static string When(double epoch)
        {
            if (epoch <= 0) return "never";
            DateTime t = Util.FromEpoch(epoch);
            DateTime today = DateTime.Today;
            if (t.Date == today) return t.ToString("HH:mm:ss", Util.Inv);
            if (t.Date == today.AddDays(-1)) return "yesterday " + t.ToString("HH:mm", Util.Inv);
            if (t.Date == today.AddDays(1)) return "tomorrow " + t.ToString("HH:mm", Util.Inv);
            return t.ToString("ddd d MMM HH:mm", Util.Inv);
        }

        public static string Ago(double epoch)
        {
            if (epoch <= 0) return "";
            double s = Util.Now() - epoch;
            if (s < 0) s = 0;
            if (s < 90) return ((int)s) + " s";
            if (s < 5400) return ((int)(s / 60)) + " min";
            if (s < 172800) return ((int)(s / 3600)) + " h";
            return ((int)(s / 86400)) + " days";
        }

        public static string JoinExts(List<string> exts)
        {
            return string.Join(", ", exts.ToArray());
        }

        public static List<string> SplitList(string text)
        {
            List<string> o = new List<string>();
            foreach (string w in (text ?? "").Split(',', ';', ' '))
                if (w.Trim().Length > 0) o.Add(w.Trim());
            return o;
        }
    }

    /// <summary>Red note under a program or script path when an account that is not an administrator could change it
    /// (the service then refuses to run it as Local System). Empty when the path is protected or the check does not apply.</summary>
    public class TrustNote : Label
    {
        string last = "\0";

        public TrustNote()
        {
            AutoSize = true;
            MaximumSize = new Size(640, 0);
            ForeColor = Ui.Bad;
            Margin = new Padding(0, 0, 0, 6);
            UseMnemonic = false;
            Tag = "nodirty";
            Visible = false;
        }

        /// <summary>applies: the entry would run as Local System (or with full administrator rights).</summary>
        public void Check(string path, bool applies)
        {
            string p = (path ?? "").Trim().Trim('"');
            string key = (applies ? "1" : "0") + p;
            if (key == last) return;
            last = key;
            string why = null;
            if (applies && p.Length > 0 && System.IO.Path.IsPathRooted(p))
            {
                try { why = PathTrust.Check(p); }
                catch (Exception ex) { why = ex.Message; }
            }
            Text = why == null ? "" : "Not protected, so the service will not run it: " + why + ".";
            Visible = why != null;
        }
    }

    /// <summary>Two-column label/field layout that grows with its content.</summary>
    public class FormGrid : TableLayoutPanel
    {
        public FormGrid()
        {
            ColumnCount = 2;
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Top;
            Margin = new Padding(0);
            Padding = new Padding(0, 0, 0, 8);
        }

        void AddRowStyle() { RowStyles.Add(new RowStyle(SizeType.AutoSize)); RowCount = RowStyles.Count; }

        public void Header(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.UseMnemonic = false;
            l.AutoSize = true;
            l.Font = Ui.Derive(Font, 1.0f, FontStyle.Bold);
            l.Margin = new Padding(0, RowStyles.Count == 0 ? 0 : 14, 0, 4);
            AddRowStyle();
            Controls.Add(l, 0, RowStyles.Count - 1);
            SetColumnSpan(l, 2);
        }

        public void Row(string label, Control c)
        {
            AddRowStyle();
            int r = RowStyles.Count - 1;
            if (label != null) Controls.Add(Ui.Lbl(label), 0, r);
            Controls.Add(c, 1, r);
            if (label == null) { SetColumn(c, 0); SetColumnSpan(c, 2); }
        }

        public void Row(string label, Control c, string hint)
        {
            Row(label, c);
            if (!string.IsNullOrEmpty(hint)) Note(hint);
        }

        public void Note(string hint)
        {
            AddRowStyle();
            Label h = Ui.Hint(hint);
            Controls.Add(h, 1, RowStyles.Count - 1);
        }

        public void Wide(Control c)
        {
            AddRowStyle();
            Controls.Add(c, 0, RowStyles.Count - 1);
            SetColumnSpan(c, 2);
        }
    }

    /// <summary>Text box with a Browse button and a marker showing whether the path exists.</summary>
    public class PathBox : TableLayoutPanel
    {
        readonly TextBox box = new TextBox();
        readonly Button browse;
        readonly Label mark = new Label();
        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
        readonly bool folder;
        readonly string filter;
        int generation;

        public event EventHandler ValueChanged;
        /// <summary>The user chose a path with the Browse button.</summary>
        public event EventHandler Picked;

        public PathBox(bool folder, string filter) : this(folder, filter, false) { }

        /// <summary>compact: the found / not found marker goes under the box, for narrow places.</summary>
        public PathBox(bool folder, string filter, bool compact)
        {
            this.folder = folder;
            this.filter = filter;
            ColumnCount = compact ? 2 : 3;
            RowCount = compact ? 2 : 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            if (!compact) ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            if (compact)
            {
                RowStyles.Add(new RowStyle(SizeType.AutoSize));
                RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Anchor = AnchorStyles.Left | AnchorStyles.Right;
            MaximumSize = new Size(900, 0);
            Margin = new Padding(0, 1, 0, 1);

            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 3, 6, 3);
            box.TextChanged += delegate
            {
                mark.Text = "";
                debounce.Stop();
                debounce.Start();
                EventHandler h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            };
            browse = Ui.Btn("Browse...", delegate { Pick(); });
            browse.MinimumSize = new Size(80, 26);
            browse.Margin = new Padding(0, 2, 6, 2);
            mark.AutoSize = true;
            mark.Anchor = AnchorStyles.Left;
            mark.Margin = new Padding(0, 0, 0, 0);
            Controls.Add(box, 0, 0);
            Controls.Add(browse, 1, 0);
            if (compact)
            {
                mark.Margin = new Padding(2, 0, 0, 2);
                mark.MinimumSize = new Size(0, Font.Height + 2);
                Controls.Add(mark, 0, 1);
                SetColumnSpan(mark, 2);
            }
            else
            {
                mark.MinimumSize = new Size(110, 0);
                Controls.Add(mark, 2, 0);
            }

            debounce.Interval = 400;
            debounce.Tick += delegate { debounce.Stop(); Probe(); };
        }

        public override string Text
        {
            get { return box.Text; }
            set { box.Text = value ?? ""; }
        }

        public void Probe()
        {
            string p = box.Text.Trim().Trim('"');
            int gen = ++generation;
            if (p.Length == 0) { mark.Text = ""; return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool exists;
                try { exists = folder ? Directory.Exists(p) : File.Exists(p); } catch (Exception) { exists = false; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (gen != generation) return;
                        mark.ForeColor = exists ? Ui.Ok : Ui.Bad;
                        mark.Text = exists ? "found" : "not found";
                    });
                }
                catch (Exception) { }
            });
        }

        void Pick()
        {
            if (folder)
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.ShowNewFolderButton = true;
                    string cur = box.Text.Trim().Trim('"');
                    if (cur.Length > 0 && Directory.Exists(cur)) d.SelectedPath = cur;
                    if (d.ShowDialog(FindForm()) == DialogResult.OK) { box.Text = d.SelectedPath; RaisePicked(); }
                }
            }
            else
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = filter ?? "All files (*.*)|*.*";
                    string cur = box.Text.Trim().Trim('"');
                    try
                    {
                        if (cur.Length > 0 && File.Exists(cur))
                        {
                            d.InitialDirectory = Path.GetDirectoryName(cur);
                            d.FileName = Path.GetFileName(cur);
                        }
                    }
                    catch (Exception) { }
                    if (d.ShowDialog(FindForm()) == DialogResult.OK) { box.Text = d.FileName; RaisePicked(); }
                }
            }
        }

        void RaisePicked()
        {
            EventHandler h = Picked;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) debounce.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Manual / every N minutes / daily at HH:mm / weekly on a day at HH:mm.</summary>
    public class ScheduleEditor : FlowLayoutPanel
    {
        readonly ComboBox mode;
        readonly ComboBox day;
        readonly DateTimePicker time = Ui.TimeBox();
        readonly NumericUpDown minutes = Ui.Num(1, 10080, 1, 0);
        readonly Label every = Ui.Lbl("every");
        readonly Label unit = Ui.Lbl("minutes");
        readonly Label at = Ui.Lbl("at");
        readonly Label on = Ui.Lbl("on");
        readonly CheckBox catchUp = Ui.Chk("Catch up if missed");
        readonly bool restart;
        bool loading;

        public event EventHandler ValueChanged;

        public ScheduleEditor() : this(false) { }

        /// <summary>restart: the choices for a program's scheduled restart (none, daily, weekly).</summary>
        public ScheduleEditor(bool restart)
        {
            this.restart = restart;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            WrapContents = false;
            Margin = new Padding(0);
            Anchor = AnchorStyles.Left;
            if (restart)
                mode = Ui.Combo(new Choice("manual", "No scheduled restart"), new Choice("daily", "Restart daily"), new Choice("weekly", "Restart weekly"));
            else
                mode = Ui.Combo(new Choice("daily", "Daily"), new Choice("weekly", "Weekly"), new Choice("interval", "Repeat every N minutes"),
                                new Choice("manual", "Only when I click Run"));
            List<Choice> days = new List<Choice>();
            foreach (string d in Cfg.Days) days.Add(new Choice(d, char.ToUpperInvariant(d[0]) + d.Substring(1)));
            day = Ui.Combo(days.ToArray());
            every.Margin = new Padding(6, 6, 4, 6);
            at.Margin = new Padding(6, 6, 4, 6);
            on.Margin = new Padding(6, 6, 4, 6);
            unit.Margin = new Padding(0, 6, 0, 6);
            catchUp.Margin = new Padding(10, 5, 0, 0);
            minutes.DecimalPlaces = 2;
            minutes.Minimum = 0.25m;
            minutes.Width = 104;
            Controls.Add(mode);
            Controls.Add(on);
            Controls.Add(day);
            Controls.Add(at);
            Controls.Add(time);
            Controls.Add(every);
            Controls.Add(minutes);
            Controls.Add(unit);
            if (!restart) Controls.Add(catchUp);
            mode.SelectedIndexChanged += delegate { UpdateVisibility(); Raise(); };
            day.SelectedIndexChanged += delegate { Raise(); };
            time.ValueChanged += delegate { Raise(); };
            minutes.ValueChanged += delegate { Raise(); };
            catchUp.CheckedChanged += delegate { Raise(); };
            UpdateVisibility();
        }

        void Raise()
        {
            if (loading) return;
            EventHandler h = ValueChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        void UpdateVisibility()
        {
            string m = Ui.Get(mode);
            at.Visible = time.Visible = m == "daily" || m == "weekly";
            on.Visible = day.Visible = m == "weekly";
            every.Visible = minutes.Visible = unit.Visible = m == "interval";
            catchUp.Visible = !restart && (m == "daily" || m == "weekly");
        }

        public void Load(ScheduleCfg s)
        {
            loading = true;
            Ui.Set(mode, s.Mode);
            Ui.Set(day, s.Day);
            Ui.SetTime(time, s.Time);
            Ui.SetNum(minutes, s.IntervalMinutes);
            catchUp.Checked = s.CatchUp;
            UpdateVisibility();
            loading = false;
        }

        public ScheduleCfg Value()
        {
            ScheduleCfg s = new ScheduleCfg(Ui.Get(mode), Ui.GetTime(time), (double)minutes.Value);
            s.Day = Ui.Get(day);
            s.CatchUp = catchUp.Checked;
            return s;
        }
    }

    /// <summary>Small coloured dot used for status.</summary>
    public class StatusDot : Control
    {
        Color color = Ui.Off;
        public StatusDot()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Color.Transparent;
            Size = new Size(14, 14);
            Margin = new Padding(0, 4, 8, 0);
        }

        public Color Dot
        {
            get { return color; }
            set { if (color != value) { color = value; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Brush b = new SolidBrush(color))
                e.Graphics.FillEllipse(b, 1, 1, Width - 3, Height - 3);
        }
    }
}
