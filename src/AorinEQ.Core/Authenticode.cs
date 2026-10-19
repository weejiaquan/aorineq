using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AorinEQ.Core;

/// <summary>Authenticode check for a file AorinEQ is about to run elevated.
///
/// One question with one answer: WHO signed this file, provided the signature is intact and chained
/// to a trusted root. The name comes from the very signature WinVerifyTrust validated, never from a
/// second read of the file.
///
/// Contract for callers: open the file with <c>new FileStream(path, FileMode.Open, FileAccess.Read,
/// FileShare.Read)</c> (no write or delete sharing) and keep it open until the process is launched, so
/// the bytes verified are the bytes run.</summary>
public static class Authenticode
{
    private const string CommonNameOid = "2.5.4.3";

    /// <summary>WinVerifyTrust with the generic Authenticode policy, no UI, revocation checked for the
    /// whole chain except the root, against the open <paramref name="file"/>. Returns the CN of the
    /// validated signer's certificate, or null when the signature is not valid (tampered, unsigned,
    /// untrusted, revocation unreachable — the HRESULT is traced) or the subject has no CN RDN.
    /// Never throws for a bad or unsigned file.</summary>
    public static string? VerifiedSignerCommonName(FileStream file)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = file.Name,
            hFile = file.SafeFileHandle.DangerousGetHandle(),
        };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        bool marshalled = false;
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, fDeleteOld: false);
            marshalled = true;
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeWholeChain,
                dwUnionChoice = WtdChoiceFile,
                pFile = pFile,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdRevocationCheckChainExcludeRoot,
            };
            var action = GenericVerifyV2;
            try
            {
                int verdict = WinVerifyTrust(NoUiWindow, ref action, ref data);
                if (verdict != 0)
                {
                    Trace.TraceWarning($"Authenticode: WinVerifyTrust rejected '{file.Name}' with 0x{verdict:X8}");
                    return null;
                }
                return LeafCommonName(data.hWVTStateData);
            }
            finally
            {
                // ALWAYS closed, whatever the verdict: VERIFY allocates provider state only CLOSE frees.
                data.dwStateAction = WtdStateActionClose;
                WinVerifyTrust(NoUiWindow, ref action, ref data);
            }
        }
        finally
        {
            if (marshalled)
                Marshal.DestroyStructure<WinTrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
            GC.KeepAlive(file);
        }
    }

    /// <summary>CN RDN of the leaf certificate WinVerifyTrust validated; the state must still be open.</summary>
    private static string? LeafCommonName(IntPtr state)
    {
        IntPtr provider = WTHelperProvDataFromStateData(state);
        if (provider == IntPtr.Zero)
            return null;
        IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == IntPtr.Zero)
            return null;
        var signerData = Marshal.PtrToStructure<CryptProviderSigner>(signer);
        if (signerData.pasCertChain == IntPtr.Zero || signerData.csCertChain == 0)
            return null;
        var leaf = Marshal.PtrToStructure<CryptProviderCert>(signerData.pasCertChain);
        if (leaf.pCert == IntPtr.Zero)
            return null;
        try
        {
            using var certificate = new X509Certificate2(leaf.pCert); // duplicates the context
            foreach (var rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
                if (!rdn.HasMultipleElements && rdn.GetSingleElementType().Value == CommonNameOid)
                    return rdn.GetSingleElementValue();
            return null;
        }
        catch (CryptographicException ex)
        {
            Trace.TraceWarning($"Authenticode: validated certificate unreadable: {ex.Message}");
            return null;
        }
    }

    private static readonly IntPtr NoUiWindow = new(-1); // INVALID_HANDLE_VALUE: no interactive user
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckChainExcludeRoot = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    // Leading fields only; the rest of each native struct is never touched. FILETIME is two DWORDs.
    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        public uint cbStruct;
        public uint verifyAsOfLow;
        public uint verifyAsOfHigh;
        public uint csCertChain;
        public IntPtr pasCertChain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCert
    {
        public uint cbStruct;
        public IntPtr pCert;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);
}
