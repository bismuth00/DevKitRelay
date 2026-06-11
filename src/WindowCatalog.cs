using System.Runtime.InteropServices;
using System.Text;

namespace DevKitRelay;

internal sealed record WindowInfo(IntPtr Handle, string Title);

internal static class WindowCatalog
{
    public static IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
            {
                return true;
            }

            var length = GetWindowTextLength(handle);
            if (length == 0)
            {
                return true;
            }

            var builder = new StringBuilder(length + 1);
            GetWindowText(handle, builder, builder.Capacity);
            var title = builder.ToString();

            if (!string.IsNullOrWhiteSpace(title))
            {
                windows.Add(new WindowInfo(handle, title));
            }

            return true;
        }, IntPtr.Zero);

        return windows.OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static WindowInfo FindByTitle(string query)
    {
        var match = GetVisibleWindows()
            .FirstOrDefault(window => window.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        return match ?? throw new InvalidOperationException($"No visible window contains title text: {query}");
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);
}
