using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CryptoSSH;
// Integration test: posts the same character/key messages received by the native
// WPF window, through the WPF message loop. Never calls the SSH transport directly.
internal static class NativeKeyboardTest
{
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScan(char character);
    internal static async Task TypeAsync(TerminalSession session, string text)
    {
        // Wait for a reselected tab to rejoin the visual tree before routing WM_CHAR.
        await session.Dispatcher.InvokeAsync(() =>
        {
            Window.GetWindow(session)?.Activate();
            session.UpdateLayout(); session.FocusTerminal();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (!session.Surface.IsKeyboardFocused) throw new Exception("Terminal did not receive keyboard focus after tab selection");
        var hwnd = (PresentationSource.FromVisual(session) as HwndSource)?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) throw new Exception("WPF keyboard target not found");
        foreach (var ch in text)
        {
            uint vk = ch == '\r' ? 13u : (uint)(VkKeyScan(ch) & 0xff);
            var scan = MapVirtualKey(vk, 0);
            var flags = (IntPtr)(1L | ((long)scan << 16));
            if (ch == '\r')
            {
                PostMessage(hwnd, 0x100, (IntPtr)vk, flags);
                PostMessage(hwnd, 0x101, (IntPtr)vk, (IntPtr)(flags.ToInt64() | 0xc0000000L));
            }
            else
            {
                // WM_CHAR is already the translated printable character. Posting
                // a keydown as well would let WPF translate it a second time.
                PostMessage(hwnd, 0x102, (IntPtr)ch, flags);
            }
            await Task.Delay(12);
        }
    }
}
