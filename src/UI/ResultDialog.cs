// Window for long, copyable text: ffmpeg errors, test results, full details of a failed conversion.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Watchkeeper.UI
{
    public class ResultDialog : Form
    {
        readonly TextBox box = new TextBox();

        public ResultDialog(string title, string headline, string text)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            Font = Ui.BaseFont();
            ClientSize = new Size(Math.Max(760, Font.Height * 48), Math.Max(480, Font.Height * 28));
            MinimumSize = new Size(480, 300);

            Label head = new Label();
            head.Text = headline;
            head.Dock = DockStyle.Top;
            head.AutoSize = false;
            head.Height = Font.Height * 2 + 14;
            head.Padding = new Padding(14, 12, 14, 0);
            head.Font = Ui.Derive(Font, 1.1f, FontStyle.Bold);

            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 52;
            bar.Padding = new Padding(14, 10, 14, 10);

            Button close = Ui.Btn("Close", delegate { Close(); });
            close.Dock = DockStyle.Right;
            close.Margin = new Padding(0);
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = 8;
            Button copy = Ui.Btn("Copy all", delegate { CopyAll(); });
            copy.Dock = DockStyle.Right;
            copy.Margin = new Padding(0);
            bar.Controls.Add(close);
            bar.Controls.Add(gap);
            bar.Controls.Add(copy);

            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Both;
            box.WordWrap = false;
            box.Font = Ui.MonoFont(Font);
            box.BackColor = SystemColors.Window;
            box.Dock = DockStyle.Fill;
            box.Text = (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            box.SelectionStart = 0;
            box.SelectionLength = 0;

            Panel pad = new Panel();
            pad.Dock = DockStyle.Fill;
            pad.Padding = new Padding(14, 6, 14, 6);
            pad.Controls.Add(box);

            Controls.Add(pad);       // Fill first
            Controls.Add(bar);
            Controls.Add(head);      // docks first (top)
            AcceptButton = close;
            CancelButton = close;
        }

        void CopyAll()
        {
            try { Clipboard.SetText(box.Text); }
            catch (Exception ex) { Ui.Error(this, "Cannot use the clipboard: " + ex.Message); }
        }

        public static void Display(IWin32Window owner, string title, string headline, string text)
        {
            using (ResultDialog d = new ResultDialog(title, headline, text))
            {
                if (owner != null) d.ShowDialog(owner); else d.ShowDialog();
            }
        }
    }
}
