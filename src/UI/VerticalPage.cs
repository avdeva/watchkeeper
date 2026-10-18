// Vertical conversion page: queue, which files, video settings and GPU choice.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class VerticalPage : SectionPage
    {
        readonly TabControl tabs = new TabControl();
        readonly TextBox preview = new TextBox();

        // ---- files & rules
        readonly CheckBox enabled = Ui.Chk("Watch the ingest folder and convert new recordings automatically");
        readonly PathBox watch = new PathBox(true, null);
        readonly PathBox outDir = new PathBox(true, null);
        readonly TextBox exts = Ui.Text(220);
        readonly NumericUpDown poll = Ui.Num(1, 3600, 1, 0);
        readonly NumericUpDown retries = Ui.Num(0, 20, 1, 0);
        readonly RadioButton afterDelete = Ui.Radio("Delete it");
        readonly RadioButton afterKeep = Ui.Radio("Keep it");
        readonly RadioButton afterMove = Ui.Radio("Move it to the folder below");
        readonly PathBox moveTo = new PathBox(true, null);
        readonly ListEditor<SourceRule> sources = new ListEditor<SourceRule>("source");
        readonly CheckBox srcOn = Ui.Chk("Convert recordings from this source");
        readonly TextBox srcName = Ui.Text(240);
        readonly TextBox srcMatch = Ui.Text(240);
        readonly TextBox srcPrefix = Ui.Text(240);
        readonly Label srcExample = new Label();
        int srcFill;
        readonly CheckBox otherOn = Ui.Chk("Also convert every file that matches none of the entries");
        readonly TextBox otherPrefix = Ui.Text(200);

        // ---- video
        readonly ComboBox codec, profile, rotation, resMode, rateMode, preset, audioMode;
        readonly NumericUpDown width = Ui.Num(0, 8192, 2, 0);
        readonly NumericUpDown height = Ui.Num(0, 8192, 2, 0);
        readonly NumericUpDown fps = Ui.Num(0, 240, 1, 2);
        readonly NumericUpDown percent = Ui.Num(5, 400, 5, 0);
        readonly NumericUpDown fixedMbps = Ui.Num(0.1m, 500, 0.5m, 1);
        readonly NumericUpDown quality = Ui.Num(1, 51, 1, 0);
        readonly NumericUpDown fallbackMbps = Ui.Num(0.1m, 500, 0.5m, 1);
        readonly NumericUpDown audioKbps = Ui.Num(32, 512, 32, 0);
        readonly TextBox extraIn = Ui.Text(520);
        readonly TextBox extraOut = Ui.Text(520);
        readonly Label percentSign = Ui.Lbl("% of the source bitrate");
        readonly Label fixedUnit = Ui.Lbl("Mbps");
        readonly Label qualityNote = Ui.Lbl("(lower is better; 18-28 is typical)");
        readonly Label sizeX = Ui.Lbl("x");

        // ---- gpu
        readonly RadioButton rbQsv = Ui.Radio("Intel Quick Sync (QSV)");
        readonly RadioButton rbNv = Ui.Radio("NVIDIA NVENC");
        readonly RadioButton rbAmf = Ui.Radio("AMD AMF");
        readonly RadioButton rbCpu = Ui.Radio("CPU only (slow)");
        readonly RadioButton rbAuto = Ui.Radio("Automatic: Intel, then NVIDIA, then AMD, else CPU");
        readonly ComboBox adapter = new ComboBox();
        readonly NumericUpDown devIndex = Ui.Num(-1, 32, 1, 0);
        readonly CheckBox hwDecode = Ui.Chk("Decode on the GPU as well (faster; turn off if a file will not convert)");
        readonly CheckBox fallback = Ui.Chk("If the GPU fails, retry that file on the CPU");
        readonly ComboBox session;
        readonly NumericUpDown cpuThreads = Ui.Num(0, 64, 1, 0);
        readonly ComboBox priority;
        readonly Label detected = new Label();
        readonly Button detectBtn, testBtn;
        readonly Label testResult = new Label();
        readonly Button testDetailBtn;
        string testDetail = "";
        List<Adapter> adapters = new List<Adapter>();
        string adapterWanted = "";

        // ---- queue
        readonly Label watcherState = new Label();
        readonly StatusDot watcherDot = new StatusDot();
        readonly Label currentLine = new Label();
        readonly ProgressBar currentBar = new ProgressBar();
        readonly ListView ingestList = new ListView();
        readonly ListView historyList = new ListView();
        readonly Button startBtn, stopBtn, stopNowBtn, retryBtn, detailsBtn;
        string ingestSig = "", historySig = "";

        public VerticalPage(Manager m)
            : base(m, "vertical", "Vertical conversion",
                   "Rotates new recordings to portrait on the GPU you choose and saves them to the output folder.")
        {
            codec = Ui.Combo(new Choice("h264", "H.264 (widest compatibility)"), new Choice("hevc", "HEVC / H.265 (smaller files)"));
            profile = Ui.Combo(new Choice("high", "High"), new Choice("main", "Main"), new Choice("baseline", "Baseline"));
            rotation = Ui.Combo(new Choice("cclock", "90° counter-clockwise"), new Choice("clock", "90° clockwise"));
            resMode = Ui.Combo(new Choice("source", "Same as the source (rotated)"), new Choice("custom", "Custom size"));
            rateMode = Ui.Combo(new Choice("match_source", "Match the source bitrate"),
                                new Choice("percent", "A percentage of the source bitrate"),
                                new Choice("fixed", "A fixed bitrate"),
                                new Choice("quality", "Constant quality"));
            preset = Ui.Combo(new Choice("balanced", "Balanced (encoder default)"), new Choice("fast", "Faster, a little larger"), new Choice("quality", "Slower, better quality"));
            audioMode = Ui.Combo(new Choice("copy", "Copy unchanged"), new Choice("aac", "Re-encode to AAC"), new Choice("none", "Remove audio"));
            session = Ui.Combo(new Choice("user_fallback", "In the service; if the GPU fails there, in the signed-in user's session"),
                               new Choice("service", "Only in the service"),
                               new Choice("user", "Only in the signed-in user's session"));
            priority = Ui.Combo(new Choice("below_normal", "Below normal (keeps the PC responsive)"), new Choice("normal", "Normal"), new Choice("idle", "Lowest"));

            detectBtn = Ui.Btn("Detect again", delegate { Detect(); });
            testBtn = Ui.Btn("Test these settings", delegate { RunTest(); });
            startBtn = Ui.Btn("Start watching", delegate { SetWatching(true, false); });
            stopBtn = Ui.Btn("Stop after current file", delegate { SetWatching(false, false); });
            stopNowBtn = Ui.Btn("Stop now", delegate { SetWatching(false, true); });
            retryBtn = Ui.Btn("Retry failed files", delegate { M.Async(this, delegate { return M.Call("vertical.retry"); }, delegate(JObject r, Exception ex) { }); });
            detailsBtn = Ui.Btn("Show details", delegate { ShowHistoryDetails(); });
            detailsBtn.Enabled = false;
            testDetailBtn = Ui.Btn("Show full details", delegate { ShowTestDetails(); });
            testDetailBtn.Visible = false;

            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.Add(BuildQueueTab());
            tabs.TabPages.Add(BuildFilesTab());
            tabs.TabPages.Add(BuildSourcesTab());
            tabs.TabPages.Add(BuildVideoTab());
            tabs.TabPages.Add(BuildGpuTab());

            Label pl = new Label();
            pl.Text = "Command that will run (shown for a sample 1920x1080 / 8 Mbps source)";
            pl.AutoSize = true;
            pl.ForeColor = SystemColors.GrayText;
            pl.Margin = new Padding(0, 8, 0, 2);
            preview.Multiline = true;
            preview.ReadOnly = true;
            preview.Height = 62;
            preview.Dock = DockStyle.Fill;
            preview.ScrollBars = ScrollBars.Vertical;
            preview.Font = Ui.MonoFont(Ui.BaseFont());
            preview.BackColor = SystemColors.Window;
            preview.Tag = "nodirty";

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            root.Controls.Add(tabs, 0, 0);
            root.Controls.Add(pl, 0, 1);
            root.Controls.Add(preview, 0, 2);
            Body.AutoScroll = false;
            Body.Controls.Add(root);

            adapters = M.Adapters;
            Init();
            RefreshAdapterUi();
        }

        static TabPage Tab(string title, Control content)
        {
            TabPage p = new TabPage(title);
            p.Padding = new Padding(12, 8, 8, 8);
            p.UseVisualStyleBackColor = true;
            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.Controls.Add(content);
            p.Controls.Add(scroll);
            return p;
        }

        // ======================================================================= queue
        TabPage BuildQueueTab()
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 1;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowCount = 6;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

            watcherState.AutoSize = true;
            watcherState.Margin = new Padding(0, 0, 16, 0);
            watcherState.Anchor = AnchorStyles.Left;
            FlowLayoutPanel top = Ui.Flow(watcherDot, watcherState, startBtn, stopBtn, stopNowBtn, retryBtn);
            top.Anchor = AnchorStyles.Left;
            top.Margin = new Padding(0, 4, 0, 6);
            watcherDot.Margin = new Padding(0, 9, 8, 0);
            t.Controls.Add(top, 0, 0);

            currentLine.AutoSize = true;
            currentLine.Margin = new Padding(0, 0, 0, 4);
            t.Controls.Add(currentLine, 0, 1);
            currentBar.Dock = DockStyle.Top;
            currentBar.Height = 14;
            currentBar.Minimum = 0;
            currentBar.Maximum = 1000;
            currentBar.Margin = new Padding(0, 0, 0, 10);
            t.Controls.Add(currentBar, 0, 2);

            SetupList(ingestList, new[] { "File", "Size", "State", "Will be saved as", "Note" }, new[] { 300, 70, 120, 280, 260 });
            t.Controls.Add(ingestList, 0, 3);

            Label hl = new Label();
            hl.Text = "Finished and failed in this session";
            hl.AutoSize = true;
            hl.Font = Ui.Derive(Ui.BaseFont(), 1.0f, FontStyle.Bold);
            hl.Margin = new Padding(0, 8, 16, 0);
            hl.Anchor = AnchorStyles.Left;
            detailsBtn.Margin = new Padding(0, 0, 0, 0);
            FlowLayoutPanel hrow = Ui.Flow(hl, detailsBtn);
            hrow.Margin = new Padding(0, 8, 0, 4);
            t.Controls.Add(hrow, 0, 4);
            SetupList(historyList, new[] { "Time", "File", "Result", "Took", "Encoded on", "Details" }, new[] { 70, 300, 80, 70, 190, 340 });
            t.Controls.Add(historyList, 0, 5);
            historyList.SelectedIndexChanged += delegate { detailsBtn.Enabled = historyList.SelectedItems.Count > 0; };
            historyList.DoubleClick += delegate { ShowHistoryDetails(); };

            TabPage p = new TabPage("Queue");
            p.Padding = new Padding(12, 8, 8, 8);
            p.UseVisualStyleBackColor = true;
            p.Controls.Add(t);
            return p;
        }

        static void SetupList(ListView lv, string[] cols, int[] widths)
        {
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.GridLines = false;
            lv.HideSelection = false;
            lv.MultiSelect = false;
            lv.Tag = "nodirty";
            for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], widths[i]);
        }

        static string StateText(string action)
        {
            switch (action)
            {
                case "convert": return "Waiting";
                case "converting": return "Converting";
                case "exists": return "Already converted";
                case "ignored": return "Not selected";
                case "backoff": return "Will retry";
                case "failed": return "Failed (gave up)";
                case "dryrun": return "Dry-run";
            }
            return action;
        }

        void SetWatching(bool on, bool abortNow)
        {
            if (Dirty)
            {
                Ui.Info(FindForm(), "Save or discard your changes first.");
                return;
            }
            AppConfig c = M.Config.Get();
            c.Vertical.Enabled = on;
            List<string> errs = M.Config.SaveSection("vertical", c.Vertical);
            if (errs.Count > 0)
            {
                Ui.Error(FindForm(), "Cannot change the watcher:\n• " + string.Join("\n• ", errs.ToArray()));
                return;
            }
            if (abortNow) M.Async(this, delegate { return M.Call("vertical.abort"); }, delegate(JObject r, Exception ex) { });
        }

        public override void Tick()
        {
            EngineStatus es = M.Status;
            ConverterStatus s = es != null ? es.Vertical : new ConverterStatus();
            AppConfig cfg = M.Config.Get();
            if (s.Running) { watcherDot.Dot = s.Current != null ? Ui.Warn : Ui.Ok; watcherState.Text = s.Current != null ? "Converting" : "Watching " + cfg.Vertical.WatchDir; }
            else if (s.Stopping) { watcherDot.Dot = Ui.Warn; watcherState.Text = "Stopping after the current file..."; }
            else { watcherDot.Dot = Ui.Off; watcherState.Text = "Not watching"; }
            startBtn.Enabled = !s.Running;
            stopBtn.Enabled = s.Running;
            stopNowBtn.Enabled = s.Running || s.Stopping;
            retryBtn.Enabled = s.Failures.Count > 0;
            retryBtn.Text = s.Failures.Count > 0 ? "Retry failed files (" + s.Failures.Count + ")" : "Retry failed files";

            if (s.Current != null)
            {
                CurrentJob c = s.Current;
                StringBuilder sb = new StringBuilder(c.Name);
                sb.Append("  -  ").Append(c.Stage);
                if (c.Stage == "encoding")
                {
                    sb.Append(' ').Append(c.Percent.ToString("0.0", Util.Inv)).Append('%');
                    if (c.Speed.Length > 0) sb.Append("  at ").Append(c.Speed);
                    if (c.EtaSeconds >= 0) sb.Append("  -  about ").Append(Util.FmtDur(c.EtaSeconds)).Append(" left");
                    if (c.Backend.Length > 0) sb.Append("  -  ").Append(c.Backend);
                }
                currentLine.Text = sb.ToString();
                currentBar.Value = (int)Math.Max(0, Math.Min(1000, c.Percent * 10));
                currentBar.Visible = true;
            }
            else
            {
                currentLine.Text = s.Running ? "Idle. Waiting for new recordings." : "Nothing is being converted.";
                currentBar.Value = 0;
            }

            StringBuilder isig = new StringBuilder();
            foreach (IngestRow r in s.Ingest) isig.Append(r.Name).Append('|').Append(r.Action).Append('|').Append(r.Note).Append('|').Append(r.Size).Append('\n');
            if (isig.ToString() != ingestSig)
            {
                ingestSig = isig.ToString();
                ingestList.BeginUpdate();
                ingestList.Items.Clear();
                foreach (IngestRow r in s.Ingest)
                {
                    ListViewItem it = new ListViewItem(r.Name);
                    it.SubItems.Add(Util.FmtSize(r.Size));
                    it.SubItems.Add(StateText(r.Action));
                    it.SubItems.Add(r.OutName);
                    it.SubItems.Add(r.Note);
                    if (r.Action == "failed") it.ForeColor = Ui.Bad;
                    else if (r.Action == "converting") it.ForeColor = Ui.Accent;
                    else if (r.Action == "ignored" || r.Action == "exists") it.ForeColor = SystemColors.GrayText;
                    ingestList.Items.Add(it);
                }
                ingestList.EndUpdate();
            }

            StringBuilder hsig = new StringBuilder();
            foreach (HistoryItem h in s.History) hsig.Append(h.Name).Append('|').Append(h.Status).Append('|').Append(h.Ended).Append('\n');
            if (hsig.ToString() != historySig)
            {
                historySig = hsig.ToString();
                historyList.BeginUpdate();
                historyList.Items.Clear();
                foreach (HistoryItem h in s.History)
                {
                    ListViewItem it = new ListViewItem(Util.FromEpoch(h.Ended).ToString("HH:mm:ss"));
                    it.SubItems.Add(h.Name);
                    it.SubItems.Add(h.Status == "done" ? "Done" : h.Status == "aborted" ? "Stopped" : "Failed");
                    it.SubItems.Add(Util.FmtDur(h.Seconds));
                    it.SubItems.Add(h.Backend);
                    it.SubItems.Add(h.Status == "done" ? h.OutName + " (" + Util.FmtSize(h.OutSize) + ")" + (!h.SourceDeleted ? ", source kept" : h.Detail.StartsWith("Original moved") ? ", original moved" : "") : h.Error);
                    if (h.Status == "failed") it.ForeColor = Ui.Bad;
                    it.Tag = h;
                    historyList.Items.Add(it);
                }
                historyList.EndUpdate();
            }
        }

        // ================================================================ files & rules
        TabPage BuildFilesTab()
        {
            FormGrid g = new FormGrid();
            g.Header("Folders");
            g.Row(null, enabled);
            g.Row("Source", watch, "Where new recordings appear, usually on the cache drive.");
            g.Row("Destination", outDir, "Finished portrait files are saved here, usually on the data drive.");
            g.Row("File types", exts, "Comma separated, for example .mp4");

            g.Header("After a file is converted");
            g.Row("Original recording", Ui.Flow(afterDelete, afterKeep, afterMove), "The original is only touched after the new file has been checked (size and length). If anything goes wrong it is kept.");
            g.Row("Move originals to", moveTo, "Usually on the data drive, so the cache is emptied and retention applies. Must not be the source folder.");
            afterMove.CheckedChanged += delegate { moveTo.Enabled = afterMove.Checked; };
            Label every = Ui.Lbl("seconds");
            g.Row("Look for new files every", Ui.Flow(poll, every));
            Label times = Ui.Lbl("times (waits longer each time)");
            g.Row("Retry a failed file up to", Ui.Flow(retries, times));
            return Tab("Files", g);
        }

        // ================================================================ sources
        TabPage BuildSourcesTab()
        {
            Label note = new Label();
            note.Text = "Each new file is checked against this list from the top; the first entry whose text appears in the file name decides. " +
                        "Entries that are switched off leave their files alone.";
            note.Dock = DockStyle.Top;
            note.AutoSize = false;
            note.Height = Ui.BaseFont().Height * 2 + 12;
            note.ForeColor = SystemColors.GrayText;
            note.Padding = new Padding(0, 0, 0, 8);

            sources.EmptyText = "No sources yet.\r\n\r\nClick \"Add source\" to choose which recordings are converted.";
            sources.RemoveNote = "Recordings that matched it are then handled by the entries below it, or by \"every other file\".";
            sources.Create = delegate
            {
                SourceRule r = new SourceRule();
                r.Name = "New source";
                r.Prefix = "Vertical";
                r.Enabled = true;
                return r;
            };
            sources.Copy = delegate(SourceRule r)
            {
                SourceRule c = new SourceRule();
                c.Name = r.Name + " (copy)";
                c.Match = r.Match;
                c.Prefix = r.Prefix;
                c.Enabled = r.Enabled;
                return c;
            };
            sources.Title = delegate(SourceRule r) { return r.Name; };
            sources.Subtitle = delegate(SourceRule r)
            {
                string m = (r.Match ?? "").Trim().Length == 0 ? "(no text yet)" : "\"" + r.Match.Trim() + "\"";
                return m + "  >  " + ((r.Prefix ?? "").Trim().Length == 0 ? "(no prefix)" : r.Prefix.Trim());
            };
            sources.IsOn = delegate(SourceRule r) { return r.Enabled; };

            FormGrid d = new FormGrid();
            d.Row(null, srcOn);
            d.Row("Name", srcName);
            d.Row("Name contains", srcMatch, "Text in the recording's file name, e.g. MultiCorder7. Capitals do not matter.");
            d.Row("Save as", srcPrefix, "Start of the finished file's name.");
            srcExample.AutoSize = true;
            srcExample.MaximumSize = new Size(380, 0);
            srcExample.ForeColor = SystemColors.GrayText;
            srcExample.Tag = "nodirty";
            d.Row("Result", srcExample, "Example of a finished file name.");
            sources.Detail.Controls.Add(d);

            srcOn.CheckedChanged += delegate { ApplySource(); };
            srcName.TextChanged += delegate { ApplySource(); };
            srcMatch.TextChanged += delegate { ApplySource(); };
            srcPrefix.TextChanged += delegate { ApplySource(); };
            sources.SelectedChanged += delegate { FillSource(); };
            sources.Changed += delegate { MarkDirty(); };

            Label otherLabel = Ui.Lbl("saved as");
            otherLabel.Margin = new Padding(8, 6, 6, 6);
            FlowLayoutPanel other = Ui.Flow(otherOn, otherLabel, otherPrefix);
            other.Dock = DockStyle.Bottom;
            other.Padding = new Padding(0, 8, 0, 0);

            TabPage p = new TabPage("Recordings");
            p.Padding = new Padding(12, 8, 8, 8);
            p.UseVisualStyleBackColor = true;
            // Docking order: the control added first is laid out last.
            p.Controls.Add(sources);
            p.Controls.Add(other);
            p.Controls.Add(note);
            return p;
        }

        void ApplySource()
        {
            if (srcFill > 0) return;
            SourceRule r = sources.Selected;
            if (r == null) return;
            r.Enabled = srcOn.Checked;
            r.Name = srcName.Text;
            r.Match = srcMatch.Text;
            r.Prefix = srcPrefix.Text;
            sources.RefreshSelected();
            UpdateSourceExample();
        }

        void FillSource()
        {
            SourceRule r = sources.Selected;
            srcFill++;
            try
            {
                Quiet(delegate
                {
                    srcOn.Checked = r != null && r.Enabled;
                    srcName.Text = r != null ? r.Name : "";
                    srcMatch.Text = r != null ? r.Match : "";
                    srcPrefix.Text = r != null ? r.Prefix : "";
                });
            }
            finally { srcFill--; }
            UpdateSourceExample();
        }

        void UpdateSourceExample()
        {
            string prefix = Util.CleanFilenamePart(srcPrefix.Text);
            string match = srcMatch.Text.Trim();
            if (prefix.Length == 0 || match.Length == 0)
            {
                srcExample.Text = "Fill in both texts above to see an example.";
                return;
            }
            string original = match + " - Cam1 - 23 July 2026 - 08-29-03 AM";
            srcExample.Text = Converter.OutputStem(original, prefix) + ".mp4";
        }

        // ============================================================ failure details
        static string HistoryText(HistoryItem h)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("File:        ").Append(h.Name).Append('\n');
            sb.Append("Result:      ").Append(h.Status == "done" ? "Done" : h.Status == "aborted" ? "Stopped" : "Failed").Append('\n');
            sb.Append("Time:        ").Append(Util.FromEpoch(h.Ended).ToString("yyyy-MM-dd HH:mm:ss", Util.Inv)).Append('\n');
            sb.Append("Took:        ").Append(Util.FmtDur(h.Seconds)).Append('\n');
            if (h.Backend.Length > 0) sb.Append("Encoded on:  ").Append(h.Backend).Append('\n');
            if (h.Status == "done")
                sb.Append("Saved as:    ").Append(h.OutName).Append(" (").Append(Util.FmtSize(h.OutSize)).Append(")")
                  .Append(h.SourceDeleted ? "" : ", original kept").Append('\n');
            if (h.Error.Length > 0) sb.Append("\nError:\n").Append(h.Error).Append('\n');
            if (h.Detail.Length > 0) sb.Append("\n").Append(h.Detail).Append('\n');
            return sb.ToString();
        }

        void ShowHistoryDetails()
        {
            if (historyList.SelectedItems.Count == 0) return;
            HistoryItem h = historyList.SelectedItems[0].Tag as HistoryItem;
            if (h == null) return;
            string head = h.Status == "done" ? "Converted " + h.Name : h.Status == "aborted" ? "Stopped: " + h.Name : "Could not convert " + h.Name;
            ResultDialog.Display(FindForm(), "Conversion details", head, HistoryText(h));
        }

        void ShowTestDetails()
        {
            if (testDetail.Length == 0) return;
            ResultDialog.Display(FindForm(), "GPU test details", "Details of the last test", testDetail);
        }

        // ===================================================================== video
        TabPage BuildVideoTab()
        {
            FormGrid g = new FormGrid();
            g.Header("Picture");
            Label pl = Ui.Lbl("profile");
            pl.Margin = new Padding(8, 6, 6, 6);
            g.Row("Video format", Ui.Flow(codec, pl, profile));
            g.Row("Rotation", rotation);
            sizeX.Margin = new Padding(2, 6, 2, 6);
            g.Row("Size of the finished video", Ui.Flow(resMode, width, sizeX, height),
                  "Width x height of the portrait result, in pixels (for example 1080 x 1920). Set only one to keep the proportions; 0 means automatic. Use even numbers.");
            Label fl = Ui.Lbl("frames per second (0 = same as the source)");
            g.Row("Frame rate", Ui.Flow(fps, fl));

            g.Header("Quality and size");
            percentSign.Margin = new Padding(0, 6, 8, 6);
            fixedUnit.Margin = new Padding(0, 6, 8, 6);
            qualityNote.Margin = new Padding(0, 6, 0, 6);
            g.Row("Bitrate", Ui.Flow(rateMode, percent, percentSign, fixedMbps, fixedUnit, quality, qualityNote));
            Label fb = Ui.Lbl("Mbps");
            g.Row("If the source bitrate is unknown", Ui.Flow(fallbackMbps, fb), "Used by the first two choices when ffprobe cannot read the original bitrate.");
            g.Row("Encoder speed", preset);

            g.Header("Audio");
            Label kb = Ui.Lbl("kbps");
            g.Row("Audio", Ui.Flow(audioMode, audioKbps, kb));

            g.Header("Advanced");
            g.Row("Extra ffmpeg input options", extraIn, "Inserted before -i. Leave empty unless you know you need it.");
            g.Row("Extra ffmpeg output options", extraOut, "Inserted before the output file name, e.g. -movflags +faststart. Quote values with spaces.");
            extraIn.Font = Ui.MonoFont(Ui.BaseFont());
            extraOut.Font = Ui.MonoFont(Ui.BaseFont());

            rateMode.SelectedIndexChanged += delegate { UpdateRateVisibility(); };
            resMode.SelectedIndexChanged += delegate { UpdateResEnabled(); };
            codec.SelectedIndexChanged += delegate { profile.Enabled = Ui.Get(codec) == "h264"; };
            audioMode.SelectedIndexChanged += delegate { audioKbps.Enabled = Ui.Get(audioMode) == "aac"; };
            return Tab("Video", g);
        }

        void UpdateRateVisibility()
        {
            string m = Ui.Get(rateMode);
            percent.Visible = percentSign.Visible = m == "percent";
            fixedMbps.Visible = fixedUnit.Visible = m == "fixed";
            quality.Visible = qualityNote.Visible = m == "quality";
            fallbackMbps.Enabled = m == "match_source" || m == "percent";
        }

        void UpdateResEnabled()
        {
            bool custom = Ui.Get(resMode) == "custom";
            width.Enabled = height.Enabled = custom;
        }

        // ======================================================================== gpu
        TabPage BuildGpuTab()
        {
            FormGrid g = new FormGrid();
            g.Header("Which hardware does the encoding");
            FlowLayoutPanel radios = new FlowLayoutPanel();
            radios.FlowDirection = FlowDirection.TopDown;
            radios.WrapContents = false;
            radios.AutoSize = true;
            radios.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            radios.Margin = new Padding(0);
            radios.Controls.AddRange(new Control[] { rbQsv, rbNv, rbAmf, rbCpu, rbAuto });
            g.Row("Encode with", radios);
            adapter.DropDownStyle = ComboBoxStyle.DropDownList;
            adapter.Width = 380;
            adapter.Anchor = AnchorStyles.Left;
            adapter.Margin = new Padding(0, 3, 6, 3);
            g.Row("Specific adapter", adapter, "Only matters when the computer has more than one GPU of the same kind. Otherwise leave it on \"let the driver choose\".");
            detected.AutoSize = true;
            detected.MaximumSize = new Size(640, 0);
            detected.Tag = "nodirty";
            detectBtn.Tag = "nodirty";
            g.Row("Graphics found in this PC", detected);
            g.Row(null, Ui.Flow(detectBtn));

            g.Header("Options");
            g.Row(null, hwDecode);
            g.Row(null, fallback);
            g.Row("Where ffmpeg runs", session, "Intel Quick Sync often only works in a signed-in session, not in the background service. With auto-logon the user session is always there.");
            Label thr = Ui.Lbl("threads (0 = automatic; CPU encoding only)");
            g.Row("CPU threads", Ui.Flow(cpuThreads, thr));
            g.Row("Process priority", priority);

            g.Header("Advanced");
            Label dl = Ui.Lbl("(-1 = automatic)");
            g.Row("Force GPU number", Ui.Flow(devIndex, dl),
                  "Last resort when ffmpeg uses the wrong GPU: sets QSV child_device / NVENC -gpu to this number. Detection order follows Windows, which may differ from ffmpeg's numbering.");

            g.Header("Check that it works");
            testBtn.Tag = "nodirty";
            g.Row(null, Ui.Flow(testBtn));
            testResult.AutoSize = true;
            testResult.MaximumSize = new Size(700, 0);
            testResult.Tag = "nodirty";
            testDetailBtn.Tag = "nodirty";
            testDetailBtn.Margin = new Padding(0, 4, 0, 0);
            g.Wide(testResult);
            g.Wide(testDetailBtn);
            g.Note("Encodes a 2 second test clip with the settings on the Video and GPU tabs (including unsaved changes). This is the quickest way to see whether the chosen GPU works.");

            rbQsv.CheckedChanged += delegate { if (rbQsv.Checked) RefreshAdapterUi(); };
            rbNv.CheckedChanged += delegate { if (rbNv.Checked) RefreshAdapterUi(); };
            rbAmf.CheckedChanged += delegate { if (rbAmf.Checked) RefreshAdapterUi(); };
            rbCpu.CheckedChanged += delegate { if (rbCpu.Checked) RefreshAdapterUi(); };
            rbAuto.CheckedChanged += delegate { if (rbAuto.Checked) RefreshAdapterUi(); };
            adapter.SelectedIndexChanged += delegate
            {
                Choice ch = adapter.SelectedItem as Choice;
                if (!Loading && ch != null) adapterWanted = ch.Value;
            };
            return Tab("GPU", g);
        }

        string SelectedBackend()
        {
            if (rbNv.Checked) return "nvidia_nvenc";
            if (rbAmf.Checked) return "amd_amf";
            if (rbCpu.Checked) return "cpu";
            if (rbAuto.Checked) return "auto";
            return "intel_qsv";
        }

        void SetBackend(string b)
        {
            rbQsv.Checked = b == "intel_qsv";
            rbNv.Checked = b == "nvidia_nvenc";
            rbAmf.Checked = b == "amd_amf";
            rbCpu.Checked = b == "cpu";
            rbAuto.Checked = b == "auto";
        }

        static string Names(List<Adapter> list, string backend)
        {
            List<string> n = new List<string>();
            foreach (Adapter a in list) if (a.Backend == backend) n.Add(a.Name);
            return string.Join(", ", n.ToArray());
        }

        void RefreshAdapterUi()
        {
            Quiet(RefreshAdapterUiCore);
        }

        void RefreshAdapterUiCore()
        {
            string[] backends = { "intel_qsv", "nvidia_nvenc", "amd_amf" };
            RadioButton[] rbs = { rbQsv, rbNv, rbAmf };
            string[] titles = { "Intel Quick Sync (QSV)", "NVIDIA NVENC", "AMD AMF" };
            for (int i = 0; i < 3; i++)
            {
                string found = Names(adapters, backends[i]);
                rbs[i].Text = titles[i] + (found.Length > 0 ? "   -   " + found : (adapters.Count > 0 ? "   -   not found in this PC" : ""));
            }
            if (adapters.Count == 0) detected.Text = "Nothing detected yet (or detection is not available).";
            else
            {
                StringBuilder sb = new StringBuilder();
                foreach (Adapter a in adapters) sb.AppendLine(a.Describe() + (a.Backend.Length > 0 ? "  ->  " + Gpu.BackendLabel(a.Backend) : "  ->  CPU only"));
                detected.Text = sb.ToString().TrimEnd();
            }

            string backend = SelectedBackend();
            string keep = adapterWanted;
            adapter.Items.Clear();
            adapter.Items.Add(new Choice("", "Let the driver choose"));
            foreach (Adapter a in adapters) if (a.Backend == backend) adapter.Items.Add(new Choice(a.Name, a.Describe()));
            int sel = 0;
            for (int i = 0; i < adapter.Items.Count; i++)
                if (((Choice)adapter.Items[i]).Value == keep) sel = i;
            if (keep.Length > 0 && sel == 0 && backend != "cpu" && backend != "auto")
            {
                adapter.Items.Add(new Choice(keep, keep + " (not found now)"));
                sel = adapter.Items.Count - 1;
            }
            adapter.SelectedIndex = sel;
            adapter.Enabled = backend != "cpu" && backend != "auto";
            cpuThreads.Enabled = backend == "cpu" || backend == "auto";
            hwDecode.Enabled = backend != "cpu";
            fallback.Enabled = backend != "cpu";
            session.Enabled = backend != "cpu";
            devIndex.Enabled = backend != "cpu";
        }

        void Detect()
        {
            detectBtn.Enabled = false;
            detected.Text = "Looking for graphics adapters...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Adapter> found;
                try { found = M.DetectAdapters(true); }
                catch (Exception) { found = adapters; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        adapters = found;
                        detectBtn.Enabled = true;
                        RefreshAdapterUi();
                        OnEdited();
                    });
                }
                catch (Exception) { }
            });
        }

        static void Reveal(Control c)
        {
            for (Control p = c.Parent; p != null; p = p.Parent)
            {
                ScrollableControl sc = p as ScrollableControl;
                if (sc != null && sc.AutoScroll) { sc.ScrollControlIntoView(c); return; }
            }
        }

        void RunTest()
        {
            AppConfig c = M.Config.Get();
            try { Collect(c); }
            catch (UiInputError ex) { testResult.ForeColor = Ui.Bad; testResult.Text = ex.Message; return; }
            if (!M.Online)
            {
                testResult.ForeColor = Ui.Bad;
                testResult.Text = "The service is not running. The test runs inside the service, where conversions run.";
                return;
            }
            testBtn.Enabled = false;
            testResult.ForeColor = SystemColors.GrayText;
            testResult.Text = "Encoding a test clip in the service...";
            Reveal(testResult);
            Reveal(testBtn);
            M.Async(this, delegate
            {
                JObject req = new JObject();
                req.Set("cmd", "gpu.test");
                req.Set("general", Json.ToJson(c.General));
                req.Set("vertical", Json.ToJson(c.Vertical));
                return M.Client.Call(req, 3000);
            }, delegate(JObject resp, Exception ex)
            {
                testBtn.Enabled = true;
                if (ex != null)
                {
                    testResult.ForeColor = Ui.Bad;
                    testResult.Text = "The test could not run: " + ex.Message;
                    return;
                }
                EncoderTestResult r = (EncoderTestResult)Json.ConvertTo(typeof(EncoderTestResult), resp.Get("result"));
                if (r.Ok)
                {
                    testResult.ForeColor = Ui.Ok;
                    testResult.Text = "Works. " + r.Label + (r.Adapter.Length > 0 ? " on " + r.Adapter : "") + " encoded the test clip in " +
                                      r.Seconds.ToString("0.0", Util.Inv) + " s (result " + r.Output + ")." +
                                      (r.Note.Length > 0 ? "\n" + r.Note : "");
                    testDetail = r.Log.Length > 0 ? r.Log : "";
                    testDetailBtn.Visible = testDetail.Length > 0;
                }
                else
                {
                    testResult.ForeColor = Ui.Bad;
                    testResult.Text = "Failed on " + r.Label + ": " + r.Error;
                    testDetail = (r.Log.Length > 0 ? r.Log : r.Error) +
                                 (r.Command.Length > 0 && r.Log.IndexOf("Command:", StringComparison.Ordinal) < 0 ? "\n\nCommand: " + r.Command : "");
                    testDetailBtn.Visible = true;
                    ResultDialog.Display(FindForm(), "GPU test failed", "The test conversion failed on " + r.Label, testDetail);
                }
                Reveal(testResult);
                Reveal(testBtn);
            });
        }

        // ============================================================== load / save
        protected override void LoadFrom(AppConfig c)
        {
            VerticalCfg v = c.Vertical;
            enabled.Checked = v.Enabled;
            watch.Text = v.WatchDir;
            outDir.Text = v.OutDir;
            exts.Text = Ui.JoinExts(v.Extensions);
            Ui.SetNum(poll, v.PollSeconds);
            Ui.SetNum(retries, v.MaxRetries);
            afterDelete.Checked = v.AfterSuccess == "delete";
            afterKeep.Checked = v.AfterSuccess == "keep";
            afterMove.Checked = v.AfterSuccess == "move";
            moveTo.Text = v.MoveOriginalTo;
            moveTo.Enabled = afterMove.Checked;
            List<SourceRule> srcCopy = new List<SourceRule>();
            foreach (SourceRule r in v.Sources) srcCopy.Add(Json.Clone(r));
            sources.SetItems(srcCopy);
            otherOn.Checked = v.Other.Enabled;
            otherPrefix.Text = v.Other.Prefix;

            VideoCfg vid = v.Video;
            Ui.Set(codec, vid.Codec);
            Ui.Set(profile, vid.Profile);
            Ui.Set(rotation, vid.Rotation);
            Ui.Set(resMode, vid.ResolutionMode);
            Ui.SetNum(width, vid.Width);
            Ui.SetNum(height, vid.Height);
            Ui.SetNum(fps, vid.Fps);
            Ui.Set(rateMode, vid.RateMode);
            Ui.SetNum(percent, vid.Percent);
            Ui.SetNum(fixedMbps, vid.FixedMbps);
            Ui.SetNum(quality, vid.Quality);
            Ui.SetNum(fallbackMbps, vid.FallbackMbps);
            Ui.Set(preset, vid.Preset);
            Ui.Set(audioMode, vid.AudioMode);
            Ui.SetNum(audioKbps, vid.AudioKbps);
            extraIn.Text = vid.ExtraInputArgs;
            extraOut.Text = vid.ExtraOutputArgs;
            UpdateRateVisibility();
            UpdateResEnabled();
            profile.Enabled = vid.Codec == "h264";
            audioKbps.Enabled = vid.AudioMode == "aac";

            GpuCfg g = v.Gpu;
            adapterWanted = g.AdapterName;
            SetBackend(g.Backend);
            Ui.SetNum(devIndex, g.AdapterIndex);
            hwDecode.Checked = g.HwDecode;
            fallback.Checked = g.FallbackToCpu;
            Ui.Set(session, g.Session);
            Ui.SetNum(cpuThreads, g.CpuThreads);
            Ui.Set(priority, g.Priority);
            RefreshAdapterUi();
        }

        protected override void Collect(AppConfig c)
        {
            VerticalCfg v = c.Vertical;
            v.Enabled = enabled.Checked;
            v.WatchDir = watch.Text;
            v.OutDir = outDir.Text;
            v.Extensions = Ui.SplitList(exts.Text);
            v.PollSeconds = (int)poll.Value;
            v.MaxRetries = (int)retries.Value;
            v.AfterSuccess = afterKeep.Checked ? "keep" : afterMove.Checked ? "move" : "delete";
            v.MoveOriginalTo = moveTo.Text;
            v.Sources = new List<SourceRule>();
            foreach (SourceRule r in sources.Items) v.Sources.Add(Json.Clone(r));
            v.Other.Enabled = otherOn.Checked;
            v.Other.Prefix = otherPrefix.Text;

            VideoCfg vid = v.Video;
            vid.Codec = Ui.Get(codec);
            vid.Profile = Ui.Get(profile);
            vid.Rotation = Ui.Get(rotation);
            vid.ResolutionMode = Ui.Get(resMode);
            vid.Width = (int)width.Value;
            vid.Height = (int)height.Value;
            vid.Fps = (double)fps.Value;
            vid.RateMode = Ui.Get(rateMode);
            vid.Percent = (double)percent.Value;
            vid.FixedMbps = (double)fixedMbps.Value;
            vid.Quality = (int)quality.Value;
            vid.FallbackMbps = (double)fallbackMbps.Value;
            vid.Preset = Ui.Get(preset);
            vid.AudioMode = Ui.Get(audioMode);
            vid.AudioKbps = (int)audioKbps.Value;
            vid.ExtraInputArgs = extraIn.Text;
            vid.ExtraOutputArgs = extraOut.Text;

            GpuCfg g = v.Gpu;
            g.Backend = SelectedBackend();
            Choice ch = adapter.SelectedItem as Choice;
            g.AdapterName = ch != null ? ch.Value : "";
            g.AdapterIndex = (int)devIndex.Value;
            g.HwDecode = hwDecode.Checked;
            g.FallbackToCpu = fallback.Checked;
            g.Session = Ui.Get(session);
            g.CpuThreads = (int)cpuThreads.Value;
            g.Priority = Ui.Get(priority);
        }

        protected override void OnEdited()
        {
            if (preview == null) return;
            try
            {
                AppConfig c = M.Config.Get();
                Collect(c);
                CommandPreview p = Converter.Preview(c.General, c.Vertical, adapters);
                if (!p.Ok) { preview.Text = "Cannot build the command: " + p.Error; return; }
                StringBuilder sb = new StringBuilder(p.Command);
                foreach (string n in p.Notes) sb.Append("\r\n# ").Append(n);
                preview.Text = sb.ToString();
            }
            catch (UiInputError ex) { preview.Text = ex.Message; }
            catch (Exception ex) { preview.Text = "Preview unavailable: " + ex.Message; }
        }

        public override void OnShown()
        {
            if (adapters.Count == 0) Detect();
        }
    }
}
