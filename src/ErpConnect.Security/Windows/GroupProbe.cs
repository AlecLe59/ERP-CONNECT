using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace ErpConnect.Security.Windows;

/// <summary>Détermine le type d'un SID via LookupAccountSidW (advapi32).</summary>
[SupportedOSPlatform("windows")]
internal static partial class GroupProbe
{
    private const int SidTypeGroup = 2;
    private const int SidTypeWellKnownGroup = 5;
    private const int SidTypeAlias = 4;

    public static bool IsGroup(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var nameLen = 256;
        var domainLen = 256;
        var name = new char[nameLen];
        var domain = new char[domainLen];
        if (!LookupAccountSid(null, bytes, name, ref nameLen, domain, ref domainLen, out var use))
        {
            return false;
        }

        return use is SidTypeGroup or SidTypeWellKnownGroup or SidTypeAlias;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "LookupAccountSidW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupAccountSid(
        string? systemName,
        byte[] sid,
        [Out] char[] name,
        ref int nameLength,
        [Out] char[] domain,
        ref int domainLength,
        out int use);
}
