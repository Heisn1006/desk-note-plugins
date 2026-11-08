using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

// 试验：用 PrintWindow(PW_RENDERFULLCONTENT) 抓 WE 壁纸子窗（WPEVideoWallpaper），
// 看能否拿到「不含桌面图标」的纯净壁纸帧。
class PrintTest
{
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    static string Class(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static IntPtr FindWallpaperChild()
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(FindRootProgman(), (h, l) =>
        {
            string c = Class(h);
            if (c.IndexOf("Wallpaper", StringComparison.OrdinalIgnoreCase) >= 0 && IsWindowVisible(h))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static IntPtr FindRootProgman()
    {
        IntPtr progman = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
        return progman;
    }

    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "print-test.png";
        uint flags = args.Length > 1 && args[1] == "0" ? 0u : 2u; // 2 = PW_RENDERFULLCONTENT

        IntPtr h = FindWallpaperChild();
        Console.WriteLine("wallpaper child hwnd=" + h + " class=" + (h == IntPtr.Zero ? "-" : Class(h)));
        if (h == IntPtr.Zero) { Console.WriteLine("not found"); return; }

        RECT r;
        GetWindowRect(h, out r);
        int w = r.Right - r.Left, hgt = r.Bottom - r.Top;
        Console.WriteLine("rect=" + w + "x" + hgt + " flags=" + flags);
        if (w <= 0 || hgt <= 0) return;

        using (var bmp = new Bitmap(w, hgt, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(h, hdc, flags);
                g.ReleaseHdc(hdc);
                Console.WriteLine("PrintWindow=" + ok + " err=" + Marshal.GetLastWin32Error());
            }
            bmp.Save(outPath, ImageFormat.Png);
            Console.WriteLine("saved " + outPath);
        }
    }
}
