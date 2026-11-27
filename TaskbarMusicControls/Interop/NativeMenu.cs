using System.Runtime.InteropServices;

namespace TaskbarMusicControls.Interop;

/// <summary>
/// A Win32 popup menu. The WPF ContextMenu costs seconds on its first open in
/// this process - it builds a window, a visual tree and a menu message loop -
/// while a native menu is drawn by the shell and returns immediately.
/// </summary>
internal sealed class NativeMenu : IDisposable
{
    private const uint MfString = 0x0000;
    private const uint MfPopup = 0x0010;
    private const uint MfSeparator = 0x0800;
    private const uint MfChecked = 0x0008;
    private const uint MfGrayed = 0x0001;

    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmTopAlign = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;

    private const uint WmNull = 0x0000;

    private readonly List<NativeMenu> _children = [];
    private bool _disposed;

    public NativeMenu()
    {
        Handle = CreatePopupMenu();
    }

    public nint Handle { get; }

    /// <summary>Adds a clickable entry and returns its command id handling to the caller.</summary>
    public void AddItem(string text, int command, bool isChecked = false, bool isEnabled = true)
    {
        var flags = MfString;
        if (isChecked) flags |= MfChecked;
        if (!isEnabled) flags |= MfGrayed;
        AppendMenuW(Handle, flags, (nuint)command, text);
    }

    public void AddSeparator() =>
        AppendMenuW(Handle, MfSeparator, 0, null);

    public NativeMenu AddSubmenu(string text)
    {
        var submenu = new NativeMenu();
        _children.Add(submenu);
        AppendMenuW(Handle, MfPopup, (nuint)submenu.Handle, text);
        return submenu;
    }

    /// <summary>Shows the menu at a screen position and returns the chosen command, or 0.</summary>
    public static int Show(nint menu, nint owner, int screenX, int screenY)
    {
        // The menu has to be dismissed when the user clicks elsewhere, and that
        // only happens reliably while the owner is the foreground window.
        SetForegroundWindow(owner);
        var command = TrackPopupMenuEx(
            menu,
            TpmLeftAlign | TpmTopAlign | TpmRightButton | TpmReturnCmd,
            screenX,
            screenY,
            owner,
            nint.Zero);
        // Lets the owner's message queue drain after the menu's modal loop, which
        // is what the docs recommend for TrackPopupMenu.
        PostMessage(owner, WmNull, nint.Zero, nint.Zero);
        return command;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        // Destroying the parent destroys the submenus attached to it.
        if (Handle != 0)
            DestroyMenu(Handle);
        _children.Clear();
    }

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(
        nint menu, uint flags, nuint itemId, string? item);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(
        nint menu, uint flags, int x, int y, nint owner, nint parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint hwnd, uint message, nint wParam, nint lParam);
}
