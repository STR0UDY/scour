using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Scour.Core.Interop;

public enum SignatureStatus
{
    /// <summary>Not checked yet, or the check couldn't run.</summary>
    Unknown,

    /// <summary>Valid signature, embedded or through a Windows catalog.</summary>
    Signed,

    Unsigned,

    /// <summary>Signed, but the signature doesn't verify (tampered, expired root, revoked…).</summary>
    Invalid,

    /// <summary>The file doesn't exist.</summary>
    Missing,
}

public sealed record SignatureInfo(SignatureStatus Status, string? Signer)
{
    /// <summary>Validly signed by Microsoft. Spoofing a company name in version info doesn't count.</summary>
    public bool IsMicrosoft => Status == SignatureStatus.Signed && Signer is not null
        && Signer.StartsWith("Microsoft ", StringComparison.Ordinal);
}

/// <summary>
/// Authenticode verification through WinVerifyTrust, covering both embedded signatures and
/// catalog signatures (most Windows binaries have no embedded signature). Offline only:
/// no revocation checks and no network retrieval, so it's fast and never phones home.
/// </summary>
public static partial class Authenticode
{
    private static readonly ConcurrentDictionary<string, SignatureInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static SignatureInfo Verify(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new SignatureInfo(SignatureStatus.Missing, null);
        return Cache.GetOrAdd(path, VerifyUncached);
    }

    private static SignatureInfo VerifyUncached(string path)
    {
        try
        {
            // Store app execution aliases are zero-byte reparse points; there's nothing to verify.
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return new SignatureInfo(SignatureStatus.Unknown, null);

            var embedded = VerifyFile(path);
            if (embedded.Status != SignatureStatus.Unsigned)
                return embedded;
            return VerifyCatalog(path) ?? embedded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            return new SignatureInfo(SignatureStatus.Unknown, null);
        }
    }

    private static SignatureInfo VerifyFile(string path)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = pathPtr };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            return Run(UnionFile, filePtr);
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    private static SignatureInfo? VerifyCatalog(string path)
    {
        // Newer catalogs index by SHA256, older ones by SHA1.
        foreach (var algorithm in (string?[])["SHA256", null])
        {
            var result = VerifyCatalog(path, algorithm);
            if (result is not null)
                return result;
        }
        return null;
    }

    private static SignatureInfo? VerifyCatalog(string path, string? algorithm)
    {
        if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0))
            return null;
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            uint hashSize = 0;
            CryptCATAdminCalcHashFromFileHandle2(admin, file, ref hashSize, null, 0);
            if (hashSize == 0)
                return null;
            var hash = new byte[hashSize];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, file, ref hashSize, hash, 0))
                return null;

            var catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, hashSize, 0, IntPtr.Zero);
            if (catalog == IntPtr.Zero)
                return null;
            try
            {
                var info = new CatalogInfo { cbStruct = (uint)Marshal.SizeOf<CatalogInfo>() };
                if (!CryptCATCatalogInfoFromContext(catalog, ref info, 0))
                    return null;

                var catalogPath = Marshal.StringToHGlobalUni(info.wszCatalogFile);
                var memberTag = Marshal.StringToHGlobalUni(Convert.ToHexString(hash));
                var memberPath = Marshal.StringToHGlobalUni(path);
                var catInfo = new WintrustCatalogInfo
                {
                    cbStruct = (uint)Marshal.SizeOf<WintrustCatalogInfo>(),
                    pcwszCatalogFilePath = catalogPath,
                    pcwszMemberTag = memberTag,
                    pcwszMemberFilePath = memberPath,
                    hCatAdmin = admin,
                };
                var catPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustCatalogInfo>());
                try
                {
                    Marshal.StructureToPtr(catInfo, catPtr, false);
                    return Run(UnionCatalog, catPtr);
                }
                finally
                {
                    Marshal.FreeHGlobal(catPtr);
                    Marshal.FreeHGlobal(catalogPath);
                    Marshal.FreeHGlobal(memberTag);
                    Marshal.FreeHGlobal(memberPath);
                }
            }
            finally
            {
                CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
            }
        }
        finally
        {
            CryptCATAdminReleaseContext(admin, 0);
        }
    }

    private static SignatureInfo Run(uint unionChoice, IntPtr union)
    {
        var data = new WintrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
            dwUIChoice = UiNone,
            fdwRevocationChecks = 0,
            dwUnionChoice = unionChoice,
            pUnion = union,
            dwStateAction = StateActionVerify,
            dwProvFlags = CacheOnlyUrlRetrieval | RevocationCheckNone,
        };
        var action = GenericVerifyV2;
        var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        try
        {
            var status = result switch
            {
                0 => SignatureStatus.Signed,
                TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => SignatureStatus.Unsigned,
                CryptEFileError => SignatureStatus.Unknown, // Couldn't read the file: no verdict.
                _ => SignatureStatus.Invalid,
            };
            return new SignatureInfo(status, status == SignatureStatus.Unsigned ? null : SignerName(data.hWVTStateData));
        }
        finally
        {
            data.dwStateAction = StateActionClose;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        }
    }

    private static string? SignerName(IntPtr state)
    {
        if (state == IntPtr.Zero)
            return null;
        var provider = WTHelperProvDataFromStateData(state);
        if (provider == IntPtr.Zero)
            return null;
        var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == IntPtr.Zero)
            return null;
        var providerCert = WTHelperGetProvCertFromChain(signer, 0);
        if (providerCert == IntPtr.Zero)
            return null;

        // CRYPT_PROVIDER_CERT: DWORD cbStruct, then PCCERT_CONTEXT pCert (pointer-aligned).
        var cert = Marshal.ReadIntPtr(providerCert, IntPtr.Size);
        if (cert == IntPtr.Zero)
            return null;

        var buffer = new char[512];
        var length = CertGetNameStringW(cert, CertNameSimpleDisplayType, 0, IntPtr.Zero, buffer, (uint)buffer.Length);
        return length > 1 ? new string(buffer, 0, (int)length - 1) : null;
    }

    private const uint UiNone = 2;
    private const uint UnionFile = 1;
    private const uint UnionCatalog = 2;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckNone = 0x10;
    private const uint CacheOnlyUrlRetrieval = 0x1000;
    private const uint CertNameSimpleDisplayType = 4;
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int CryptEFileError = unchecked((int)0x80092003);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustCatalogInfo
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnion;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }

#pragma warning disable SYSLIB1054 // Structs with strings and refs; classic marshalling is simpler here.
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr catAdmin, IntPtr subsystem, string? hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr catAdmin, SafeFileHandle file, ref uint hashSize, byte[]? hash, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr catAdmin, byte[] hash, uint hashSize, uint flags, IntPtr previous);

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CatalogInfo info, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catInfo, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr catAdmin, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);

    [DllImport("crypt32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CertGetNameStringW(IntPtr certContext, uint type, uint flags, IntPtr typePara, char[] name, uint nameSize);
#pragma warning restore SYSLIB1054
}
