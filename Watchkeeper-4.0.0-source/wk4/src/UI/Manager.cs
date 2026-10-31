// What the manager window works with: the settings being edited (saved to config.json as a whole), and the service's
// status file (read every two seconds).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class Manager
    {
        public readonly string DataDir;
        public readonly SettingsFile File;
        public Settings Edit = new Settings();
        string savedText = "";
        DateTime loadedStamp;
        /// <summary>Why the file could not be loaded (the newest backup or empty settings are shown instead).</summary>
        public string LoadError;
        public List<string> LoadNotes = new List<string>();
        public string LoadSource = "";
        public EngineStatus Status;
        public event Action Reloaded;
        public event Action Changed;

        public Manager(string dataDir)
        {
            DataDir = dataDir;
            File = new SettingsFile(dataDir, null);
            File.ImportVersion3 = false;
        }

        public void Load()
        {
            LoadError = null;
            LoadResult r;
            try { r = File.Load(); }
            catch (Exception ex) { r = new LoadResult(); r.Error = ex.Message; }
            LoadSource = r.Source;
            LoadNotes = r.Notes;
            if (r.Settings != null) Edit = r.Settings;
            else
            {
                LoadError = r.Error;
                Backup b = File.NewestReadable();
                Edit = b != null ? SettingsFile.Parse(Util.ReadAllTextRetry(b.File), null) : new Settings();
            }
            savedText = r.Settings != null ? SettingsFile.ToText(Edit) : "";
            loadedStamp = Stamp();
            if (Reloaded != null) Reloaded();
            if (Changed != null) Changed();
        }

        DateTime Stamp() { try { return System.IO.File.GetLastWriteTimeUtc(File.Path); } catch (Exception) { return DateTime.MinValue; } }

        public bool Dirty { get { return SettingsFile.ToText(Edit) != savedText; } }

        public void MarkChanged() { if (Changed != null) Changed(); }

        /// <summary>Save everything. Returns false when the person cancelled.</summary>
        public bool Save(IWin32Window owner)
        {
            if (LoadError != null && LoadError.Contains("version 3"))
            {
                Ui.Error(owner, "Not saved: " + LoadError + ".");
                return false;
            }
            List<Problem> problems = Validate.All(Edit);
            List<Problem> errors = problems.FindAll(delegate(Problem p) { return p.Level == "error"; });
            if (errors.Count > 0)
            {
                string list = string.Join("\n", errors.ConvertAll(delegate(Problem p) { return "- " + p; }).ToArray());
                if (!Ui.Confirm(owner, "These entries cannot work as they are set up:\n\n" + list + "\n\nThe service skips them and shows why. Save anyway?")) return false;
            }
            if (Stamp() != loadedStamp && System.IO.File.Exists(File.Path) && savedText.Length > 0)
            {
                string disk = "";
                try { disk = Util.ReadAllTextRetry(File.Path); } catch (Exception) { }
                if (disk != savedText &&
                    !Ui.Confirm(owner, "config.json was changed by someone else since it was opened here. Save anyway (their change is kept in the backups folder)?")) return false;
            }
            File.Save(Edit, "manager");
            savedText = SettingsFile.ToText(Edit);
            loadedStamp = Stamp();
            LoadError = null;
            if (Changed != null) Changed();
            return true;
        }

        public void PollStatus() { Status = Engine.ReadStatus(DataDir); }

        /// <summary>The service wrote its status within the last 15 seconds.</summary>
        public bool ServiceAlive { get { return Status != null && Status.Updated > 0 && Util.Now() - Status.Updated < 15; } }

        /// <summary>Ask the service to do something and wait for its answer (null when it does not answer in time).</summary>
        /// <summary>A request to the service is waiting for its answer (the window stays usable meanwhile, but a second
        /// request and closing the window wait until it is answered).</summary>
        public bool Busy { get; private set; }

        public CommandResult Command(string action, string target, int waitMs)
        {
            if (Busy)
            {
                CommandResult wait = new CommandResult();
                wait.Text = "another request to the service is still waiting for its answer; try again in a moment";
                return wait;
            }
            Busy = true;
            Cursor was = Cursor.Current;
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                string id = Engine.SendCommand(DataDir, action, target);
                DateTime end = DateTime.Now.AddMilliseconds(waitMs);
                while (DateTime.Now < end)
                {
                    Application.DoEvents();
                    Thread.Sleep(150);
                    EngineStatus s = Engine.ReadStatus(DataDir);
                    if (s == null) continue;
                    CommandResult r = s.Commands.Find(delegate(CommandResult c) { return c.Id == id; });
                    if (r != null) { Status = s; return r; }
                }
                try { System.IO.File.Delete(Path.Combine(Path.Combine(DataDir, "commands"), id + ".json")); } catch (Exception) { }
                return null;
            }
            finally { Busy = false; Cursor.Current = was; }
        }
    }

    public abstract class Page : UserControl
    {
        protected readonly Manager M;
        public readonly Binder B = new Binder();
        public abstract string Title { get; }
        public virtual string Key { get { return Title.ToLowerInvariant(); } }

        protected Page(Manager m)
        {
            M = m;
            Dock = DockStyle.Fill;
            BackColor = Ui.Back;
            Padding = new Padding(16, 12, 12, 8);
            Font = Ui.BaseFont();
            B.Changed += delegate { OnFieldChanged(); M.MarkChanged(); };
        }

        protected virtual void OnFieldChanged() { }

        /// <summary>The settings were loaded again (start, Discard changes, restore).</summary>
        public virtual void Reload() { B.Load(); }

        /// <summary>A new status from the service.</summary>
        public virtual void Live(EngineStatus s) { }

        /// <summary>Colour of the dot in the navigation.</summary>
        public virtual Color Dot(EngineStatus s) { return Ui.Off; }

        protected Label Heading(string text, string help)
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Top;
            Label h = new Label();
            h.Text = text;
            h.Font = Ui.Bold(13f);
            h.AutoSize = true;
            h.Location = new Point(0, 0);
            Label d = new Label();
            d.Text = help;
            d.ForeColor = Ui.Help;
            d.AutoSize = false;
            d.Location = new Point(0, h.Font.Height + 6);
            p.Controls.Add(h);
            p.Controls.Add(d);
            EventHandler size = delegate
            {
                int w = Math.Max(200, Math.Min(p.Width, 1100));
                Size need = TextRenderer.MeasureText(help, Ui.BaseFont(), new Size(w, 0), TextFormatFlags.WordBreak);
                d.Size = new Size(w, need.Height + 2);
                p.Height = d.Bottom + 10;
            };
            p.Resize += size;
            size(null, EventArgs.Empty);
            Controls.Add(p);
            return h;
        }

        protected static string Ago(double epoch)
        {
            if (epoch <= 0) return "never";
            double s = Util.Now() - epoch;
            if (s < 0) return "in " + Util.FmtDuration(-s);
            return Util.FmtDuration(s) + " ago";
        }

        protected static string At(double epoch)
        {
            if (epoch <= 0) return "";
            DateTime t = Util.FromEpoch(epoch);
            return t.Date == DateTime.Today ? t.ToString("HH:mm", Util.Inv) : t.ToString("ddd d MMM HH:mm", Util.Inv);
        }
    }
}
