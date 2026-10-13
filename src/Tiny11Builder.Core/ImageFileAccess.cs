using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Tiny11Builder.Core;

[SupportedOSPlatform("windows")]
internal static class ImageFileAccess
{
    private const uint Wof = 0x80000017;
    private const uint Wim = 0x80000008;

    public static bool Exists(string path)
    {
        using var handle = CreateFile(ExtendedPath(path), 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return true;
        var error = Marshal.GetLastWin32Error();
        if (error is 2 or 3) return false;
        throw new Win32Exception(error, $"无法检查脱机条目：{path}");
    }

    public static void ValidateFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return;
        using var handle = CreateFile(ExtendedPath(path), 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法检查脱机映像文件：{path}");
        var buffer = new byte[16384];
        if (!DeviceIoControl(handle, 0x000900A8, IntPtr.Zero, 0, buffer, buffer.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法读取脱机映像的重解析标记：{path}");
        var tag = BitConverter.ToUInt32(buffer, 0);
        if (tag is not (Wof or Wim))
            throw new InvalidDataException($"拒绝跟随非映像的重解析点：{path}（标记 {tag:X8}）。");
    }

    public static void DeleteEntry(string path)
    {
        ValidateFile(path);
        using var handle = CreateFile(ExtendedPath(path), 0x00010080, 7,
            IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法打开脱机条目以进行删除：{path}");
        if (!GetFileInformationByHandleEx(handle, 9, out var attributes, Marshal.SizeOf<AttributeTagInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法检查脱机删除句柄：{path}");
        if ((attributes.Attributes & (uint)FileAttributes.ReparsePoint) != 0 && attributes.Tag is not (Wof or Wim))
            throw new InvalidDataException($"拒绝删除非映像的重解析点：{path}（标记 {attributes.Tag:X8}）。");
        // Delete only this directory entry; do not change attributes or ACLs shared by hard links.
        var flags = 0x00000015u;
        if (!SetFileInformationByHandle(handle, 21, ref flags, sizeof(uint)))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法删除脱机条目：{path}");
    }

    private static string ExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full : @"\\?\" + full;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint Attributes; public uint Tag; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out AttributeTagInfo information, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint control, IntPtr input, int inputSize,
        byte[] output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref uint information, int size);
}
