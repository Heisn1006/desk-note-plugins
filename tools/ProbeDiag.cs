using System;
using System.Runtime.InteropServices;

// 诊断：IDesktopWallpaper 在 y 这台机器上 GetMonitorDevicePathCount 返回 0x800706F4，
// 但 GetWallpaper/GetPosition 正常。这里只验证「能不能绕开 count，直接按索引枚举」。
class ProbeDiag
{
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        [PreserveSig] int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out IntPtr wallpaper);
        [PreserveSig] int GetMonitorDevicePathCount(out uint count);
        [PreserveSig] int GetMonitorDevicePathAt(uint monitorIndex, out IntPtr monitorID);
        [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out RECT rect);
        [PreserveSig] int SetBackgroundColor(uint color);
        [PreserveSig] int GetBackgroundColor(out uint color);
        [PreserveSig] int SetPosition(int position);
        [PreserveSig] int GetPosition(out int position);
    }

    static string Hex(int hr) { return "0x" + hr.ToString("X8"); }

    static void Main()
    {
        var type = Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"));
        var desk = (IDesktopWallpaper)Activator.CreateInstance(type);

        uint count = 0;
        Console.WriteLine("GetMonitorDevicePathCount -> " + Hex(desk.GetMonitorDevicePathCount(out count)) + " count=" + count);

        for (uint i = 0; i < 4; i++)
        {
            IntPtr idPtr = IntPtr.Zero;
            int hr = desk.GetMonitorDevicePathAt(i, out idPtr);
            Console.WriteLine("GetMonitorDevicePathAt(" + i + ") -> " + Hex(hr));
            if (hr != 0) break;
            string id = idPtr == IntPtr.Zero ? "(null)" : Marshal.PtrToStringUni(idPtr);
            Console.WriteLine("   id=" + id);

            RECT r;
            int hr2 = desk.GetMonitorRECT(id, out r);
            Console.WriteLine("   GetMonitorRECT -> " + Hex(hr2) + " " + r.Left + "," + r.Top + "," + (r.Right - r.Left) + "x" + (r.Bottom - r.Top));

            IntPtr wpPtr = IntPtr.Zero;
            int hr3 = desk.GetWallpaper(id, out wpPtr);
            string wp = wpPtr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(wpPtr);
            Console.WriteLine("   GetWallpaper(id) -> " + Hex(hr3) + " " + wp);
        }
    }
}
