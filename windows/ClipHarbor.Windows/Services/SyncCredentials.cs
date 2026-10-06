using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ClipHarbor.Windows.Services;

internal static class SyncCredentials
{
    [StructLayout(LayoutKind.Sequential)] private struct Credential
    {
        public uint Flags, Type; public IntPtr TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist, AttributeCount;
        public IntPtr Attributes, TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WriteNative(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ReadNative(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool DeleteNative(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
    private static string Target(string id) => "ClipHarbor.Sync." + id;
    public static string? Read(string id)
    {
        if (!ReadNative(Target(id), 1, 0, out var pointer)) { if (Marshal.GetLastWin32Error() == 1168) return null; throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "读取同步凭据失败。"); }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer); var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }
    public static void Write(string id, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); var data = Marshal.AllocHGlobal(bytes.Length); var target = Marshal.StringToCoTaskMemUni(Target(id));
        try
        {
            Marshal.Copy(bytes, 0, data, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = target, CredentialBlob = data, CredentialBlobSize = (uint)bytes.Length, Persist = 2 };
            if (!WriteNative(ref credential, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "保存同步凭据失败。");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); Marshal.Copy(bytes, 0, data, bytes.Length); Marshal.FreeHGlobal(data); Marshal.FreeCoTaskMem(target); }
    }
    public static void Delete(string id) { if (!DeleteNative(Target(id), 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "清除同步凭据失败。"); }
}
