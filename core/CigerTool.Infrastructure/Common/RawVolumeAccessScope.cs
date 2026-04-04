using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace CigerTool.Infrastructure.Common;

[SupportedOSPlatform("windows")]
internal sealed class RawVolumeAccessScope : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlUnlockVolume = 0x0009001C;
    private const uint FsctlDismountVolume = 0x00090020;
    private const int BufferSize = 1024 * 1024;

    private readonly bool _unlockOnDispose;
    private bool _disposed;

    private RawVolumeAccessScope(SafeFileHandle handle, FileAccess access, bool unlockOnDispose)
    {
        Handle = handle;
        Stream = new FileStream(handle, access, BufferSize, isAsync: true);
        _unlockOnDispose = unlockOnDispose;
    }

    public SafeFileHandle Handle { get; }

    public FileStream Stream { get; }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static RawVolumeAccessScope OpenRead(string driveLetter)
    {
        var handle = OpenVolumeHandle(driveLetter, write: false);
        return new RawVolumeAccessScope(handle, FileAccess.Read, unlockOnDispose: false);
    }

    public static RawVolumeAccessScope OpenWrite(string driveLetter)
    {
        var handle = OpenVolumeHandle(driveLetter, write: true);
        TryControlVolume(handle, FsctlLockVolume);
        TryControlVolume(handle, FsctlDismountVolume);
        return new RawVolumeAccessScope(handle, FileAccess.ReadWrite, unlockOnDispose: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_unlockOnDispose)
            {
                TryControlVolume(Handle, FsctlUnlockVolume);
            }
        }
        finally
        {
            Stream.Dispose();
            Handle.Dispose();
        }
    }

    private static SafeFileHandle OpenVolumeHandle(string driveLetter, bool write)
    {
        var normalized = NormalizeDriveLetter(driveLetter);
        var path = $@"\\.\{normalized}";
        var desiredAccess = write ? GenericRead | GenericWrite : GenericRead;
        var handle = CreateFile(path, desiredAccess, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Hacme erişilemedi '{normalized}': {new Win32Exception(error).Message}",
                new Win32Exception(error));
        }

        return handle;
    }

    private static string NormalizeDriveLetter(string driveLetter)
    {
        var trimmed = (driveLetter ?? string.Empty).Trim().TrimEnd('\\');
        if (trimmed.Length == 1)
        {
            return $"{trimmed}:";
        }

        return trimmed;
    }

    private static void TryControlVolume(SafeFileHandle handle, uint controlCode)
    {
        DeviceIoControl(handle, controlCode, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
