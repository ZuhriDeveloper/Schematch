using System.Runtime.InteropServices;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using MaterialSkin;
using MaterialSkin.Controls;

namespace Schematch.App.UI;

/// <summary>Side-by-side text diff: two synced monospace panes with per-line change coloring.</summary>
public sealed class DiffViewer : UserControl
{
    private static readonly Color InsertedColor = Color.FromArgb(215, 245, 215);
    private static readonly Color DeletedColor = Color.FromArgb(250, 220, 220);
    private static readonly Color ModifiedColor = Color.FromArgb(252, 245, 205);
    private static readonly Color ImaginaryColor = Color.FromArgb(240, 240, 240);

    private readonly RichTextBox _left = CreatePane();
    private readonly RichTextBox _right = CreatePane();
    private readonly MaterialLabel _leftHeader = CreateHeader("Source");
    private readonly MaterialLabel _rightHeader = CreateHeader("Target");
    private bool _syncing;

    public DiffViewer()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_leftHeader, 0, 0);
        layout.Controls.Add(_rightHeader, 1, 0);
        layout.Controls.Add(_left, 0, 1);
        layout.Controls.Add(_right, 1, 1);
        Controls.Add(layout);

        _left.VScroll += (_, _) => SyncScroll(_left, _right);
        _right.VScroll += (_, _) => SyncScroll(_right, _left);
    }

    public void SetHeaders(string left, string right)
    {
        _leftHeader.Text = left;
        _rightHeader.Text = right;
    }

    public void ShowDiff(string? leftText, string? rightText)
    {
        var model = new SideBySideDiffBuilder().BuildDiffModel(leftText ?? "", rightText ?? "", ignoreWhitespace: false);
        Render(_left, model.OldText);
        Render(_right, model.NewText);
    }

    public void Clear()
    {
        _left.Clear();
        _right.Clear();
    }

    private static void Render(RichTextBox box, DiffPaneModel pane)
    {
        box.SuspendLayout();
        box.Clear();
        foreach (var line in pane.Lines)
        {
            int start = box.TextLength;
            box.AppendText((line.Text ?? "") + "\n");
            box.Select(start, box.TextLength - start);
            box.SelectionBackColor = line.Type switch
            {
                ChangeType.Inserted => InsertedColor,
                ChangeType.Deleted => DeletedColor,
                ChangeType.Modified => ModifiedColor,
                ChangeType.Imaginary => ImaginaryColor,
                _ => box.BackColor,
            };
        }
        box.Select(0, 0);
        box.ResumeLayout();
    }

    private void SyncScroll(RichTextBox from, RichTextBox to)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var pos = GetScrollPos(from);
            SetScrollPos(to, pos);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static RichTextBox CreatePane() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        WordWrap = false,
        Font = new Font("Consolas", 9.5f),
        BorderStyle = BorderStyle.FixedSingle,
        HideSelection = false,
        BackColor = Color.White,
        ScrollBars = RichTextBoxScrollBars.Both,
    };

    private static MaterialLabel CreateHeader(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        FontType = MaterialSkinManager.fontType.Subtitle2,
        Padding = new Padding(2),
        AutoSize = true,
    };

    private const int WM_USER = 0x400;
    private const int EM_GETSCROLLPOS = WM_USER + 221;
    private const int EM_SETSCROLLPOS = WM_USER + 222;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, ref POINT lParam);

    private static POINT GetScrollPos(RichTextBox box)
    {
        var pt = new POINT();
        SendMessage(box.Handle, EM_GETSCROLLPOS, 0, ref pt);
        return pt;
    }

    private static void SetScrollPos(RichTextBox box, POINT pt) =>
        SendMessage(box.Handle, EM_SETSCROLLPOS, 0, ref pt);
}
