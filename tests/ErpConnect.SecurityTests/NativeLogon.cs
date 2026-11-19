using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ErpConnect.SecurityTests;

[SupportedOSPlatform("windows")]
internal static partial class NativeLogon
{
    private const int Logon32LogonInteractive = 2;
    private const int Logon32ProviderDefault = 0;

    public static SafeAccessTokenHandle Logon(string user, string password)
    {
        var parts = user.Split('\\', 2);
        var (domain, name) = parts.Length == 2 ? (parts[0], parts[1]) : (".", parts[0]);
        if (!LogonUser(name, domain, password, Logon32LogonInteractive, Logon32ProviderDefault, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return token;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUser(string user, string domain, string password, int logonType, int provider, out SafeAccessTokenHandle token);
}
