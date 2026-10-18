// First start without a configuration file: what kind of server is this? Picks the starter
// configuration and which parts of Watchkeeper are shown and run. Changeable later on
// Service & settings.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class SetupDialog : Form
    {
        readonly RadioButton studio = Ui.Radio("Studio server: recordings, vMix, vertical conversion, Axia GPIO");
        readonly RadioButton watchdog = Ui.Radio("Watchdog and alerts: keep programs and Windows services running, alert on problems");
        readonly CheckBox mFolders = Ui.Chk("Folder policies (move from a cache drive, retention)");
        readonly CheckBox mPrograms = Ui.Chk("Watchdog (programs and Windows services, health checks)");
        readonly CheckBox mScripts = Ui.Chk("Scripts (on a schedule, at start-up, for new files)");
        readonly CheckBox mVertical = Ui.Chk("Vertical conversion (needs ffmpeg)");
        readonly CheckBox mGpio = Ui.Chk("GPIO: Axia to vMix / HTTP");

        public SetupDialog()
        {
            Text = "Set up Watchkeeper";
            Font = Ui.BaseFont();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Icon = MainForm.AppIcon();

            FormGrid g = new FormGrid();
            g.Padding = new Padding(20, 16, 20, 10);
            Label intro = new Label();
            intro.Text = "There is no configuration on this computer yet. What does this server do?";
            intro.AutoSize = true;
            intro.MaximumSize = new Size(560, 0);
            intro.Margin = new Padding(0, 0, 0, 10);
            g.Wide(intro);
            FlowLayoutPanel kinds = new FlowLayoutPanel();
            kinds.FlowDirection = FlowDirection.TopDown;
            kinds.AutoSize = true;
            kinds.WrapContents = false;
            kinds.Controls.Add(studio);
            kinds.Controls.Add(watchdog);
            g.Wide(kinds);
            g.Header("Parts shown and running (changeable later on Service & settings)");
            foreach (CheckBox c in new[] { mFolders, mPrograms, mScripts, mVertical, mGpio }) g.Wide(c);
            Label note = new Label();
            note.Text = "Everything starts switched off; nothing happens until you switch it on. Alerts and the log are always there.";
            note.AutoSize = true;
            note.MaximumSize = new Size(560, 0);
            note.ForeColor = SystemColors.GrayText;
            note.Margin = new Padding(0, 10, 0, 0);
            g.Wide(note);

            Button ok = new Button();
            ok.Text = "Continue";
            ok.DialogResult = DialogResult.OK;
            ok.AutoSize = true;
            ok.Padding = new Padding(12, 4, 12, 4);
            ok.Margin = new Padding(0, 14, 0, 0);
            g.Wide(ok);
            AcceptButton = ok;

            studio.CheckedChanged += delegate { if (studio.Checked) SetModules(true); };
            watchdog.CheckedChanged += delegate { if (watchdog.Checked) SetModules(false); };
            studio.Checked = true;

            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Controls.Add(g);
        }

        void SetModules(bool all)
        {
            mFolders.Checked = all;
            mPrograms.Checked = true;
            mScripts.Checked = true;
            mVertical.Checked = all;
            mGpio.Checked = all;
        }

        public AppConfig Result()
        {
            AppConfig c = Cfg.Starter(watchdog.Checked ? "watchdog" : "studio");
            c.General.Modules.Folders = mFolders.Checked;
            c.General.Modules.Programs = mPrograms.Checked;
            c.General.Modules.Scripts = mScripts.Checked;
            c.General.Modules.Vertical = mVertical.Checked;
            c.General.Modules.Gpio = mGpio.Checked;
            return c;
        }

        /// <summary>Ask; closing the window gives the studio starter.</summary>
        public static AppConfig Ask()
        {
            using (SetupDialog d = new SetupDialog())
            {
                return d.ShowDialog() == DialogResult.OK ? d.Result() : null;
            }
        }
    }
}
