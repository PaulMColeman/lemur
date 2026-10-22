using System.Runtime.InteropServices;
using System.Text;

namespace Lemur.Config;

/// <summary>Where API keys live. Settings only ever hold a secret's name.</summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string value);
    bool Delete(string name);

    /// <summary>The names of every secret held (not their values).</summary>
    IEnumerable<string> Names();
}

/// <summary>
/// Windows Credential Manager (generic credentials, per Windows user, DPAPI-protected).
/// Visible under Control Panel → Credential Manager → Windows Credentials as "Lemur:&lt;name&gt;".
/// </summary>
public sealed class WindowsCredentialStore(string? prefix = null) : ISecretStore
{
    readonly string prefix = prefix ?? AppPaths.CredentialPrefix;

    const uint CredTypeGeneric = 1;
    const uint CredPersistLocalMachine = 2;

    string Target(string name) => $"{prefix}:{name}";

    public string? Get(string name)
    {
        if (!CredReadW(Target(name), CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return null;
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Set(string name, string value)
    {
        var blob = Encoding.Unicode.GetBytes(value);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = Target(name),
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWriteW(ref cred, 0))
                throw new InvalidOperationException($"CredWrite failed for '{name}' (Win32 error {Marshal.GetLastWin32Error()})");
        }
        finally
        {
            // Don't leave the key lying around in freed memory.
            Marshal.Copy(new byte[blob.Length], 0, blobPtr, blob.Length);
            Array.Clear(blob);
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public bool Delete(string name) => CredDeleteW(Target(name), CredTypeGeneric, 0);

    public IEnumerable<string> Names()
    {
        if (!CredEnumerateW(prefix + ":*", 0, out uint count, out var list)) return []; // none, or no access
        try
        {
            var names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                if (cred.Type == CredTypeGeneric && cred.TargetName.StartsWith(prefix + ":", StringComparison.Ordinal))
                    names.Add(cred.TargetName[(prefix.Length + 1)..]);
            }
            return names;
        }
        finally
        {
            CredFree(list);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredEnumerateW(string filter, uint flags, out uint count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}

/// <summary>For tests.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    readonly Dictionary<string, string> _values = new();
    public string? Get(string name) => _values.GetValueOrDefault(name);
    public void Set(string name, string value) => _values[name] = value;
    public bool Delete(string name) => _values.Remove(name);
    public IEnumerable<string> Names() => [.. _values.Keys];
}
