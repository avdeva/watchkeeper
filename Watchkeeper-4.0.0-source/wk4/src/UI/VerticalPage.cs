// Vertical conversion page.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class VerticalPage : Page
    {
        readonly DataGridView sources = new DataGridView();
        readonly ListView files = new ListView();
        readonly Label state = new Label();
        bool filling;
        public override string Title { get { return "Vertical conversion"; } }
        VerticalSettings V { get { return M.Edit.Vertical; } }

        static readonly string[][] After = { new[] { "move", "Move the original to a folder" }, new[] { "delete", "Delete the original" }, new[] { "keep", "Leave the original where it is" } };
        static readonly string[][] Codecs = { new[] { "h264", "H.264" }, new[] { "hevc", "H.265 (HEVC)" } };
        static readonly string[][] Rotations = { new[] { "counterclockwise", "90 degrees counter-clockwise" }, new[] { "clockwise", "90 degrees clockwise" } };
        static readonly string[][] Rates = { new[] { "source", "Same bitrate as the source" }, new[] { "fixed", "Fixed bitrate" }, new[] { "quality", "Constant quality" } };
        static readonly string[][] Presets = { new[] { "fast", "Fast" }, new[] { "balanced", "Balanced" }, new[] { "quality", "Best quality" } };
        static readonly string[][] Audios = { new[] { "copy", "Copy unchanged" }, new[] { "aac", "AAC" }, new[] { "none", "No audio" } };
        static readonly string[][] Encoders = {
            new[] { "auto", "Automatic (the GPU this computer has, then the CPU)" }, new[] { "intel_qsv", "Intel Quick Sync" },
            new[] { "nvidia_nvenc", "NVIDIA NVENC" }, new[] { "amd_amf", "AMD AMF" }, new[] { "cpu", "CPU only" } };
        static readonly string[][] Priorities = { new[] { "below_normal", "Below normal" }, new[] { "idle", "Lowest" }, new[] { "normal", "Normal" } };

        public VerticalPage(Manager m) : base(m)
        {
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterWidth = 6;
            Controls.Add(split);
            Heading("Vertical conversion", "Recordings that appear in a folder are turned on their side (portrait, for phones) with ffmpeg. A file is converted once it has stopped " +
                                           "changing; the result is checked before the original is moved or deleted. If the GPU cannot be used, the next way is tried, ending with the CPU.");

            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            split.Panel1.Controls.Add(scroll);
            FormGrid g = new FormGrid();
            int w = Ui.BaseFont().Height * 30;
            g.Row("", B.Check("Convert recordings", delegate { return V.Enabled; }, delegate(bool v) { V.Enabled = v; }));
            TextBox watch = B.Text(w, delegate { return V.WatchFolder; }, delegate(string v) { V.WatchFolder = v; });
            g.Row("Folder to watch", Ui.Flow(watch, Browse(watch)));
            TextBox output = B.Text(w, delegate { return V.OutputFolder; }, delegate(string v) { V.OutputFolder = v; });
            g.Row("Output folder", Ui.Flow(output, Browse(output)));
            g.Row("File types", B.Text(160, delegate { return string.Join("; ", V.Extensions.ToArray()); }, delegate(string v) { V.Extensions = Util.Patterns(v); }), "e.g. .mp4; .mov");
            g.Row("Wait", Ui.Flow(B.Number(2, 3600, 0, delegate { return V.SettleSeconds; }, delegate(double v) { V.SettleSeconds = (int)v; }), Ui.Label("seconds after a file stops changing")));
            g.Row("Afterwards", B.Choice(After, delegate { return V.AfterSuccess; }, delegate(string v) { V.AfterSuccess = v; }));
            TextBox orig = B.Text(w, delegate { return V.MoveOriginalsTo; }, delegate(string v) { V.MoveOriginalsTo = v; });
            g.Row("Originals to", Ui.Flow(orig, Browse(orig)));
            g.Row("Tries", Ui.Flow(B.Number(0, 20, 0, delegate { return V.Retries; }, delegate(double v) { V.Retries = (int)v; }), Ui.Label("more times after a failure (after 1, 5, 15, 60 minutes)")));

            g.Section("Output names");
            sources.AllowUserToAddRows = false;
            sources.AllowUserToResizeRows = false;
            sources.RowHeadersVisible = false;
            sources.Width = w + 120;
            sources.Height = Ui.BaseFont().Height * 8;
            sources.BackgroundColor = Ui.Back;
            sources.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            sources.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", FillWeight = 12 });
            sources.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Source", FillWeight = 30 });
            sources.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "File name contains", FillWeight = 40 });
            sources.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Output name starts with", FillWeight = 45 });
            sources.CellValueChanged += delegate { if (!filling) { ReadSources(); M.MarkChanged(); } };
            sources.CurrentCellDirtyStateChanged += delegate { if (sources.IsCurrentCellDirty) sources.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            g.Row("Sources", sources, "The first source whose text is in the file name decides the output name: 'Recorder 7 - Cam1 - 1 May 2026 - 10-00-00 AM' with " +
                                      "'Vertical PGM' becomes 'Vertical PGM - 1 May 2026 - 10-00-00 AM.mp4'. A source that is switched off leaves its files alone.");
            g.Row("", Ui.Flow(Ui.Button("Add source", delegate { V.Sources.Add(new VerticalSource()); FillSources(); M.MarkChanged(); }),
                              Ui.Button("Remove source", delegate { RemoveSource(); })));
            g.Row("Other files", Ui.Flow(B.Check("Convert them too, named", delegate { return V.ConvertOthers; }, delegate(bool v) { V.ConvertOthers = v; }),
                                         B.Text(160, delegate { return V.OtherPrefix; }, delegate(string v) { V.OtherPrefix = v; }), Ui.Label("- ...")));

            g.Section("Picture and sound");
            g.Row("Video", Ui.Flow(B.Choice(Codecs, delegate { return V.Codec; }, delegate(string v) { V.Codec = v; }), Ui.Label("turned"),
                                   B.Choice(Rotations, delegate { return V.Rotate; }, delegate(string v) { V.Rotate = v; })));
            g.Row("Size", Ui.Flow(B.Number(0, 8192, 0, delegate { return V.Width; }, delegate(double v) { V.Width = (int)v; }), Ui.Label("x"),
                                  B.Number(0, 8192, 0, delegate { return V.Height; }, delegate(double v) { V.Height = (int)v; }), Ui.Label("pixels (0 x 0 = the source size, turned)")),
                  "Set only one of them to keep the proportions. Even numbers only.");
            g.Row("Frame rate", Ui.Flow(B.Number(0, 240, 3, delegate { return V.Fps; }, delegate(double v) { V.Fps = v; }), Ui.Label("per second (0 = as the source)")));
            g.Row("Bitrate", Ui.Flow(B.Choice(Rates, delegate { return V.Bitrate; }, delegate(string v) { V.Bitrate = v; }),
                                     B.Number(0.1m, 500, 1, delegate { return V.FixedMbps; }, delegate(double v) { V.FixedMbps = v; }), Ui.Label("Mbps fixed; quality"),
                                     B.Number(1, 51, 0, delegate { return V.Quality; }, delegate(double v) { V.Quality = (int)v; })),
                  "Same as the source: when the source's bitrate cannot be read, " + "the fallback below is used. Quality: lower number = better (23 is typical).");
            g.Row("Fallback", Ui.Flow(B.Number(0.1m, 500, 1, delegate { return V.FallbackMbps; }, delegate(double v) { V.FallbackMbps = v; }), Ui.Label("Mbps")));
            g.Row("Speed", B.Choice(Presets, delegate { return V.Preset; }, delegate(string v) { V.Preset = v; }));
            g.Row("Audio", Ui.Flow(B.Choice(Audios, delegate { return V.Audio; }, delegate(string v) { V.Audio = v; }),
                                   B.Number(32, 512, 0, delegate { return V.AudioKbps; }, delegate(double v) { V.AudioKbps = (int)v; }), Ui.Label("kbps (AAC)")));
            g.Row("Extra options", Ui.Flow(B.Text(w / 2, delegate { return V.ExtraInputArgs; }, delegate(string v) { V.ExtraInputArgs = v; }), Ui.Label("before the input,"),
                                           B.Text(w / 2, delegate { return V.ExtraOutputArgs; }, delegate(string v) { V.ExtraOutputArgs = v; }), Ui.Label("for the output")),
                  "Only if you know ffmpeg; usually empty.");

            g.Section("Encoder");
            g.Row("Use", B.Choice(Encoders, delegate { return V.Encoder; }, delegate(string v) { V.Encoder = v; }));
            g.Row("GPU number", Ui.Flow(B.Number(-1, 16, 0, delegate { return V.GpuIndex; }, delegate(double v) { V.GpuIndex = (int)v; }), Ui.Label("(-1 = the default; only with several GPUs of the same kind)")));
            g.Row("", B.Check("Decode on the GPU too (faster; software decoding is tried if it fails)", delegate { return V.HardwareDecode; }, delegate(bool v) { V.HardwareDecode = v; }));
            g.Row("", B.Check("Try in the signed-in user's session when the GPU cannot be used from the service", delegate { return V.UserSessionFallback; }, delegate(bool v) { V.UserSessionFallback = v; }),
                  "Intel Quick Sync often only works in the signed-in session on servers.");
            g.Row("", B.Check("Use the CPU as the last resort", delegate { return V.CpuFallback; }, delegate(bool v) { V.CpuFallback = v; }));
            g.Row("Priority", B.Choice(Priorities, delegate { return V.Priority; }, delegate(string v) { V.Priority = v; }));
            g.Row("", Ui.Button("Test the encoders...", delegate { TestEncoders(); }), "Converts a 2-second test clip with each way in order and says which work (on this computer, from this window).");
            B.Add(FillSources);
            scroll.Controls.Add(g);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Fill;
            state.Dock = DockStyle.Top;
            state.AutoSize = false;
            state.Height = Ui.BaseFont().Height * 2 + 6;
            state.Font = new Font(Ui.BaseFont(), FontStyle.Bold);
            files.View = View.Details;
            files.Dock = DockStyle.Fill;
            files.FullRowSelect = true;
            int em = Ui.BaseFont().Height;
            files.Columns.Add("File", em * 24);
            files.Columns.Add("State", em * 7);
            files.Columns.Add("Details", em * 40);
            Button retry = Ui.Button("Try failed files again", delegate { Retry(); });
            retry.Dock = DockStyle.Bottom;
            bottom.Controls.Add(files);
            bottom.Controls.Add(state);
            bottom.Controls.Add(retry);
            split.Panel2.Controls.Add(bottom);
            bool sized = false;
            split.Resize += delegate { if (sized || split.Height < 200) return; sized = true; try { split.SplitterDistance = split.Height * 62 / 100; } catch (Exception) { } };
        }

        static Button Browse(TextBox target)
        {
            return Ui.Button("Browse...", delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.SelectedPath = Util.NormPath(target.Text);
                    if (d.ShowDialog(target.FindForm()) == DialogResult.OK) target.Text = d.SelectedPath;
                }
            });
        }

        void FillSources()
        {
            filling = true;
            sources.Rows.Clear();
            foreach (VerticalSource s in V.Sources) sources.Rows.Add(s.Enabled, s.Name, s.Match, s.Prefix);
            filling = false;
        }

        void ReadSources()
        {
            for (int i = 0; i < sources.Rows.Count && i < V.Sources.Count; i++)
            {
                DataGridViewRow r = sources.Rows[i];
                V.Sources[i].Enabled = r.Cells[0].Value is bool && (bool)r.Cells[0].Value;
                V.Sources[i].Name = Convert.ToString(r.Cells[1].Value) ?? "";
                V.Sources[i].Match = Convert.ToString(r.Cells[2].Value) ?? "";
                V.Sources[i].Prefix = Convert.ToString(r.Cells[3].Value) ?? "";
            }
        }

        void RemoveSource()
        {
            if (sources.CurrentRow == null || sources.CurrentRow.Index >= V.Sources.Count) return;
            V.Sources.RemoveAt(sources.CurrentRow.Index);
            FillSources();
            M.MarkChanged();
        }

        public DataGridView SourcesGrid { get { return sources; } }

        public override void Live(EngineStatus s)
        {
            VerticalStatus v = s.Vertical;
            state.Text = !M.ServiceAlive ? "The service is not running." : v.State == "converting" ? v.Text + " - " + v.Percent.ToString("0", Util.Inv) + "% with " + v.Encoder : v.Text;
            state.ForeColor = M.ServiceAlive ? Ui.StateColor(v.State) : Ui.Help;
            files.BeginUpdate();
            files.Items.Clear();
            foreach (VerticalFile f in v.Files) Row(f, "in the folder");
            foreach (VerticalFile f in v.Recent) Row(f, "earlier");
            files.EndUpdate();
        }

        void Row(VerticalFile f, string group)
        {
            ListViewItem it = new ListViewItem(f.Name);
            it.SubItems.Add(f.State);
            it.SubItems.Add(f.Note + (f.Output.Length > 0 && f.State == "done" ? " -> " + f.Output : ""));
            it.ForeColor = Ui.StateColor(f.State);
            files.Items.Add(it);
        }

        public override Color Dot(EngineStatus s)
        {
            if (!s.Vertical.Enabled) return Ui.Off;
            if (s.Vertical.State == "error" || s.Vertical.Files.Exists(delegate(VerticalFile f) { return f.State == "failed"; })) return Ui.Bad;
            return s.Vertical.State == "converting" ? Ui.Warn : Ui.Good;
        }

        void TestEncoders()
        {
            Cursor = Cursors.WaitCursor;
            List<string> lines;
            try { lines = Vertical.TestEncoders(M.Edit, new SystemProcesses()); }
            finally { Cursor = Cursors.Default; }
            lines.Insert(0, "Tried in this order (the service uses the same order and remembers the first that works):\n");
            Ui.ShowText(FindForm(), "Encoder test", string.Join("\n", lines.ToArray()));
        }

        void Retry()
        {
            if (!M.ServiceAlive) { Ui.Info(FindForm(), "The service is not running."); return; }
            CommandResult r = M.Command("retry_vertical", "", 10000);
            Ui.Info(FindForm(), r == null ? "The service did not answer." : Cap(r.Text) + ".");
        }

        static string Cap(string t) { return t.Length == 0 ? t : t.Substring(0, 1).ToUpperInvariant() + t.Substring(1); }
    }
}
