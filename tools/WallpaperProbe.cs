using System;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// 通过 Windows COM IDesktopWallpaper 读取每台显示器的壁纸、区域、位置模式和 DPI
// 输出一行 JSON，每 2 秒刷新；供 DeskNote 主进程解析
//   --watch  持续输出（每 2 秒一行）
//   --debug  把每一步的函数与 HRESULT 打到 stderr，用于定位 COM 失败点
class WallpaperProbe
{
    static bool Debug;
    static string LastComError = "";

    static void Dbg(string s)
    {
        if (!Debug) return;
        try { Console.Error.WriteLine("[probe] " + s); Console.Error.Flush(); } catch { }
    }

    static string Hr(int hr)
    {
        return hr == 0 ? "S_OK" : string.Format("0x{0:X8}", hr);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

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

    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX mi);

    static string Esc(string s)
    {
        if (s == null) return "";
        var b = new StringBuilder();
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': b.Append("\\\\"); break;
                case '"': b.Append("\\\""); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                default: b.Append(c); break;
            }
        }
        return b.ToString();
    }

    static string PtrToStringAndFree(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
    }

    static string BuildJson()
    {
        Dbg("--- BuildJson ---");
        var clsid = new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");
        var type = Type.GetTypeFromCLSID(clsid);
        if (type == null) { Dbg("Type.GetTypeFromCLSID -> null"); return null; }

        object obj;
        try
        {
            obj = Activator.CreateInstance(type);
        }
        catch (Exception e)
        {
            int chr = Marshal.GetHRForException(e);
            Dbg("CoCreateInstance(DesktopWallpaper) failed: " + e.GetType().Name + " hr=" + Hr(chr) + " msg=" + e.Message);
            return null;
        }
        var desk = obj as IDesktopWallpaper;
        if (desk == null) { Dbg("COM object is not IDesktopWallpaper"); return null; }

        uint count = 0;
        int hr = desk.GetMonitorDevicePathCount(out count);
        Dbg("GetMonitorDevicePathCount hr=" + Hr(hr) + " count=" + count);
        if (hr != 0 || count == 0)
        {
            // 本机（Windows 11 26200）实测：GetWallpaper/GetPosition 正常，但枚举接口
            // 恒定返回 0x800706F4 (RPC_X_NULL_REF_POINTER)，计划任务里跑也一样 —— 是系统侧的问题，
            // 不是封送写错。这里记下原因，交给 fallback 输出，别让调用方以为是「没有显示器」。
            LastComError = hr != 0 ? Hr(hr) : "count=0";
            return null;
        }

        int position = 0;
        hr = desk.GetPosition(out position);
        Dbg("GetPosition hr=" + Hr(hr) + " position=" + position);
        if (hr != 0) position = PositionFromRegistry();

        var sb = new StringBuilder();
        sb.Append("{\"position\":").Append(position).Append(",\"source\":\"com\",\"monitors\":[");

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        bool first = true;
        for (uint i = 0; i < count; i++)
        {
            IntPtr idPtr;
            hr = desk.GetMonitorDevicePathAt(i, out idPtr);
            Dbg("GetMonitorDevicePathAt(" + i + ") hr=" + Hr(hr));
            if (hr != 0 || idPtr == IntPtr.Zero) continue;
            string id = PtrToStringAndFree(idPtr);

            IntPtr wpPtr;
            string wp = "";
            hr = desk.GetWallpaper(id, out wpPtr);
            Dbg("GetWallpaper(" + id + ") hr=" + Hr(hr));
            if (hr == 0) wp = PtrToStringAndFree(wpPtr);

            RECT r;
            hr = desk.GetMonitorRECT(id, out r);
            Dbg("GetMonitorRECT(" + id + ") hr=" + Hr(hr) + " rect=" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom);
            if (hr != 0) continue;

            int dpi = DpiForRect(r);

            if (!first) sb.Append(',');
            first = false;
            sb.Append('{')
              .Append("\"id\":\"").Append(Esc(id)).Append("\",")
              .Append("\"rect\":{")
              .Append("\"x\":").Append(r.Left).Append(',')
              .Append("\"y\":").Append(r.Top).Append(',')
              .Append("\"w\":").Append(r.Right - r.Left).Append(',')
              .Append("\"h\":").Append(r.Bottom - r.Top).Append("},")
              .Append("\"dpi\":").Append(dpi).Append(',')
              .Append("\"wallpaper\":\"").Append(Esc(wp)).Append("\"")
              .Append('}');

            if (r.Left < minX) minX = r.Left;
            if (r.Top < minY) minY = r.Top;
            if (r.Right > maxX) maxX = r.Right;
            if (r.Bottom > maxY) maxY = r.Bottom;
        }

        if (first)
        {
            Dbg("COM reported " + count + " monitor(s) but none produced a rect");
            return null;
        }

        sb.Append("],\"virtual\":{")
          .Append("\"x\":").Append(minX).Append(',')
          .Append("\"y\":").Append(minY).Append(',')
          .Append("\"w\":").Append(maxX - minX).Append(',')
          .Append("\"h\":").Append(maxY - minY)
          .Append("}}");
        return sb.ToString();
    }

    static int DpiForRect(RECT r)
    {
        int dpi = 96;
        POINT pt;
        pt.X = (r.Left + r.Right) / 2;
        pt.Y = (r.Top + r.Bottom) / 2;
        IntPtr mon = MonitorFromPoint(pt, 2);
        if (mon == IntPtr.Zero) return dpi;
        uint dx, dy;
        int hr = GetDpiForMonitor(mon, 0, out dx, out dy);
        Dbg("GetDpiForMonitor hr=" + Hr(hr) + " dpi=" + dx);
        if (hr == 0 && dx > 0) dpi = (int)dx;
        return dpi;
    }

    static int PositionFromRegistry()
    {
        try
        {
            string style = Convert.ToString(Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallpaperStyle", "10"));
            string tile = Convert.ToString(Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "TileWallpaper", "0"));
            if (tile == "1") return 1;
            switch (style)
            {
                case "0": return 0;
                case "2": return 2;
                case "6": return 3;
                case "22": return 5;
                default: return 4;
            }
        }
        catch { return 4; }
    }

    static string RegistryWallpaper()
    {
        try { return Convert.ToString(Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", "")) ?? ""; }
        catch { return ""; }
    }

    // COM 被拒时的兜底：EnumDisplayMonitors + 注册表壁纸（每屏同一张）
    static string BuildFallback(int position)
    {
        string wp = RegistryWallpaper();
        Dbg("fallback: registry wallpaper = " + wp);
        var items = new List<string>();
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        MonitorEnumProc cb = (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            string dev = "";
            if (GetMonitorInfo(hMon, ref mi)) dev = mi.szDevice ?? "";
            int dpi = DpiForRect(r);
            items.Add("{\"id\":\"" + Esc(dev) + "\",\"rect\":{\"x\":" + r.Left + ",\"y\":" + r.Top +
                      ",\"w\":" + (r.Right - r.Left) + ",\"h\":" + (r.Bottom - r.Top) + "},\"dpi\":" + dpi +
                      ",\"wallpaper\":\"" + Esc(wp) + "\"}");
            if (r.Left < minX) minX = r.Left;
            if (r.Top < minY) minY = r.Top;
            if (r.Right > maxX) maxX = r.Right;
            if (r.Bottom > maxY) maxY = r.Bottom;
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        if (items.Count == 0) { minX = minY = maxX = maxY = 0; }
        string err = LastComError == "" ? "" : ",\"comError\":" + Quote(LastComError);
        return "{\"position\":" + position + ",\"source\":\"fallback\",\"fallback\":true" + err + ",\"monitors\":[" + string.Join(",", items.ToArray()) +
               "],\"virtual\":{\"x\":" + minX + ",\"y\":" + minY + ",\"w\":" + (maxX - minX) + ",\"h\":" + (maxY - minY) + "}}";
    }

    static string Quote(string s)
    {
        return "\"" + Esc(s) + "\"";
    }

    [STAThread]
    static void Main(string[] args)
    {
        Debug = Array.IndexOf(args, "--debug") >= 0;
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

        bool watch = Array.IndexOf(args, "--watch") >= 0;
        do
        {
            string line = null;
            try
            {
                line = BuildJson();
            }
            catch (Exception e)
            {
                Dbg("BuildJson threw " + e.GetType().Name + ": " + e.Message);
            }
            if (line == null)
            {
                try { line = BuildFallback(PositionFromRegistry()); }
                catch (Exception e2)
                {
                    Dbg("BuildFallback threw " + e2.GetType().Name + ": " + e2.Message);
                    line = "{\"error\":\"" + Esc(e2.Message) + "\",\"source\":\"error\"}";
                }
            }
            Console.WriteLine(line);
            Console.Out.Flush();
            if (watch) Thread.Sleep(2000);
        } while (watch);
    }
}
