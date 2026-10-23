using System.Runtime.InteropServices;
using System.Text;
using BambuBar.Services;

namespace BambuBar.Platform;

/// <summary>Access codes in Windows Credential Manager (Control Panel → Credential Manager → Windows Credentials),
/// one generic credential per printer, readable only by this Windows account.</summary>
public sealed class CredentialStore : ICredentialStore
{
    private static string Target(string serial) => $"Bambu Bar/{serial}";

    public string? AccessCode(string serial)
    {
        if (!Native.CredRead(Target(serial), Native.CRED_TYPE_GENERIC, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Native.CREDENTIAL>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            Native.CredFree(pointer);
        }
    }

    public void SetAccessCode(string code, string serial)
    {
        var bytes = Encoding.Unicode.GetBytes(code);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Native.CREDENTIAL
            {
                Type = Native.CRED_TYPE_GENERIC,
                TargetName = Target(serial),
                Comment = "Bambu Lab printer access code",
                CredentialBlob = blob,
                CredentialBlobSize = bytes.Length,
                Persist = Native.CRED_PERSIST_LOCAL_MACHINE,
                UserName = serial,
            };
            Native.CredWrite(ref credential, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void RemoveAccessCode(string serial) => Native.CredDelete(Target(serial), Native.CRED_TYPE_GENERIC, 0);
}
