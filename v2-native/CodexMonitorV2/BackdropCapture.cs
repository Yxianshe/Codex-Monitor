using System.Runtime.InteropServices;

namespace CodexMonitorV2;

internal static class BackdropCapture
{
    internal sealed record Frame(byte[] Pixels, int Width, int Height);

    public static Frame? CaptureWindowBehind(nint host)
    {
        if (!GetWindowRect(host, out Rect hostRect)) return null;
        int width = Math.Max(1, hostRect.Right - hostRect.Left);
        int height = Math.Max(1, hostRect.Bottom - hostRect.Top);
        nint source = FindWindowBehind(host, hostRect);
        if (source == 0 || !GetVisualRect(source, out Rect sourceRect)) return null;

        int sourceWidth = Math.Max(1, sourceRect.Right - sourceRect.Left);
        int sourceHeight = Math.Max(1, sourceRect.Bottom - sourceRect.Top);
        nint screenDc = GetDC(0);
        nint sourceDc = CreateCompatibleDC(screenDc);
        nint cropDc = CreateCompatibleDC(screenDc);
        nint sourceBitmap = CreateCompatibleBitmap(screenDc, sourceWidth, sourceHeight);
        nint oldSource = SelectObject(sourceDc, sourceBitmap);

        BitmapInfo info = BitmapInfo.Create(width, -height);
        nint cropBitmap = CreateDIBSection(cropDc, ref info, 0, out nint bits, 0, 0);
        nint oldCrop = SelectObject(cropDc, cropBitmap);

        try
        {
            if (!PrintWindow(source, sourceDc, 2)) return null;
            PatBlt(cropDc, 0, 0, width, height, 0x00FF0062); // WHITENESS

            int srcX = hostRect.Left - sourceRect.Left;
            int srcY = hostRect.Top - sourceRect.Top;
            int dstX = Math.Max(0, -srcX);
            int dstY = Math.Max(0, -srcY);
            srcX = Math.Max(0, srcX);
            srcY = Math.Max(0, srcY);
            int copyWidth = Math.Min(width - dstX, sourceWidth - srcX);
            int copyHeight = Math.Min(height - dstY, sourceHeight - srcY);
            if (copyWidth > 0 && copyHeight > 0)
                BitBlt(cropDc, dstX, dstY, copyWidth, copyHeight, sourceDc, srcX, srcY, 0x00CC0020);

            byte[] pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new Frame(pixels, width, height);
        }
        finally
        {
            SelectObject(sourceDc, oldSource);
            SelectObject(cropDc, oldCrop);
            DeleteObject(sourceBitmap);
            DeleteObject(cropBitmap);
            DeleteDC(sourceDc);
            DeleteDC(cropDc);
            ReleaseDC(0, screenDc);
        }
    }

    private static nint FindWindowBehind(nint host, Rect hostRect)
    {
        GetWindowThreadProcessId(host, out uint ownProcess);
        int centerX = (hostRect.Left + hostRect.Right) / 2;
        int centerY = (hostRect.Top + hostRect.Bottom) / 2;

        for (nint candidate = GetWindow(host, 2); candidate != 0; candidate = GetWindow(candidate, 2))
        {
            if (!IsWindowVisible(candidate) || IsIconic(candidate)) continue;
            GetWindowThreadProcessId(candidate, out uint process);
            if (process == ownProcess) continue;
            if (DwmGetWindowAttribute(candidate, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (!GetVisualRect(candidate, out Rect rect)) continue;
            if (centerX >= rect.Left && centerX < rect.Right && centerY >= rect.Top && centerY < rect.Bottom)
                return candidate;
        }

        return GetDesktopWindow();
    }

    private static bool GetVisualRect(nint hwnd, out Rect rect)
    {
        if (DwmGetWindowAttribute(hwnd, 9, out rect, Marshal.SizeOf<Rect>()) == 0)
            return rect.Right > rect.Left && rect.Bottom > rect.Top;
        return GetWindowRect(hwnd, out rect);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;

        public static BitmapInfo Create(int width, int height) => new()
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = (uint)(width * Math.Abs(height) * 4)
            }
        };
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);
    [DllImport("gdi32.dll")] private static extern bool PatBlt(nint dc, int x, int y, int width, int height, uint rop);
}
