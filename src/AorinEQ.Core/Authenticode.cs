using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AorinEQ.Core;

/// <summary>Authenticode checks for a file AorinEQ is about to run elevated.
///
/// Two questions, both required: is the signature intact and chained to a trusted root
/// (<see cref="IsSignatureValid"/>), and WHO signed it (<see cref="SignerCommonName"/>). Either
/// alone is worthless — any signed program verifies, and a tampered file still carries its
/// original certificate.</summary>
public static class Authenticode
{
    /// <summary>WinVerifyTrust with the generic Authenticode policy, no UI, revocation checked for
    /// the whole chain except the root. Never throws.</summary>
    public static bool IsSignatureValid(string path)
    {
        if (!File.Exists(path))
            return false;
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = path,
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
                return WinVerifyTrust(NoUiWindow, ref action, ref data) == 0;
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
        }
    }

    /// <summary>The CN of the certificate the file's embedded signature names, or null when the
    /// file has none. Does NOT verify anything — pair it with <see cref="IsSignatureValid"/>.</summary>
    public static string? SignerCommonName(string path)
    {
        try
        {
            using var signer = X509Certificate.CreateFromSignedFile(path);
            using var certificate = new X509Certificate2(signer);
            return certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
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

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);
}
