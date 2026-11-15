using UCS.Common;

namespace UCS.Client;

public enum UpdateChoice { Later, Skip, UpdateNow, CloseDcsAndUpdate, WhenDcsCloses }

/// <summary>Pop-up shown when an automatic check finds a new version.</summary>
public sealed class UpdatePromptForm : Form
{
    public UpdateChoice Choice { get; private set; } = UpdateChoice.Later;

    public UpdatePromptForm(ReleaseInfo release)
    {
        Text = "DCS-UCS update available";
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(520, 400);

        var dcsRunning = UpdateService.IsDcsRunning();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(490, 0),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            Text = $"DCS-UCS {release.Version} is available (you have {UpdateService.CurrentVersion})." +
                   (release.Prerelease ? "  [pre-release]" : ""),
        }, 0, 0);
        layout.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window,
            Text = (string.IsNullOrWhiteSpace(release.Body) ? "(no release notes)" : release.Body).Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
        }, 0, 1);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(490, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 8, 3, 8),
            Text = dcsRunning
                ? "DCS is running. DCS only loads the new UCS scripts after it restarts, so updating with DCS closed is recommended. " +
                  "\"Close DCS and update\" brings DCS to the front: confirm the exit there."
                : "The installer runs silently (Windows may ask for permission) and reopens UCS-Client.",
        }, 0, 2);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Dock = DockStyle.Fill };
        if (dcsRunning)
        {
            buttons.Controls.Add(Btn("Close DCS and update", UpdateChoice.CloseDcsAndUpdate, true));
            buttons.Controls.Add(Btn("Update when DCS closes", UpdateChoice.WhenDcsCloses, false));
            buttons.Controls.Add(Btn("Update now", UpdateChoice.UpdateNow, false));
        }
        else
        {
            buttons.Controls.Add(Btn("Update now", UpdateChoice.UpdateNow, true));
        }
        buttons.Controls.Add(Btn("Remind me later", UpdateChoice.Later, false));
        buttons.Controls.Add(Btn("Skip this version", UpdateChoice.Skip, false));
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
    }

    private Button Btn(string text, UpdateChoice choice, bool isDefault)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 30, Padding = new Padding(6, 0, 6, 0) };
        b.Click += (_, _) =>
        {
            Choice = choice;
            DialogResult = DialogResult.OK;
            Close();
        };
        if (isDefault) AcceptButton = b;
        if (choice == UpdateChoice.Later) CancelButton = b;
        return b;
    }
}
