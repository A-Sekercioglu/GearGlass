using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace GearGlass.Services;

/// <summary>The GearGlass icon in the notification area, with its right-click menu.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _clickThroughItem;
    private bool _hiddenTipShown;

    public TrayIconService(Stream iconStream)
    {
        _icon = new Icon(iconStream, SystemInformation.SmallIconSize);

        _showItem = new ToolStripMenuItem("HUD'u gizle", null, (_, _) => ToggleVisibilityRequested?.Invoke());
        _clickThroughItem = new ToolStripMenuItem(
            "Fare geçişi",
            null,
            (_, _) => ClickThroughToggleRequested?.Invoke())
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+F12"
        };
        var resetItem = new ToolStripMenuItem("Konumu sıfırla", null, (_, _) => ResetPositionRequested?.Invoke());
        var exitItem = new ToolStripMenuItem("Çıkış", null, (_, _) => ExitRequested?.Invoke());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([_showItem, _clickThroughItem, resetItem, new ToolStripSeparator(), exitItem]);
        menu.Opening += (_, _) => MenuOpening?.Invoke();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "GearGlass",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowRequested?.Invoke();
            }
        };
    }

    public event Action? ShowRequested;

    public event Action? ToggleVisibilityRequested;

    public event Action? ClickThroughToggleRequested;

    public event Action? ResetPositionRequested;

    public event Action? ExitRequested;

    /// <summary>Raised just before the menu opens, so the owner can refresh its state.</summary>
    public event Action? MenuOpening;

    public void UpdateMenuState(bool hudVisible, bool clickThrough)
    {
        _showItem.Text = hudVisible ? "HUD'u gizle" : "HUD'u göster";
        _clickThroughItem.Checked = clickThrough;
    }

    /// <summary>Tells the user once per session where the HUD went after it was hidden.</summary>
    public void NotifyHiddenToTray()
    {
        if (_hiddenTipShown)
        {
            return;
        }

        _hiddenTipShown = true;
        _notifyIcon.ShowBalloonTip(
            3000,
            "GearGlass",
            "GearGlass tepsi çubuğunda çalışmaya devam ediyor. Göstermek için simgeye tıklayın.",
            ToolTipIcon.None);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
