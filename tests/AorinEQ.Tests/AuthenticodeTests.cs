using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Real WinVerifyTrust against real files on this machine. The runtime's own
/// System.Private.CoreLib.dll is Authenticode-signed by Microsoft (CN=.NET, verified 2026-10-18),
/// which makes it a valid signature with the WRONG signer for VB-Cable's purposes; AorinEQ's own
/// locally built assembly is unsigned. (The spec suggested AorinEQ's signed binary — dev builds
/// are not signed, so the runtime stands in.)</summary>
public class AuthenticodeTests
{
    private readonly ITestOutputHelper _out;
    public AuthenticodeTests(ITestOutputHelper output) => _out = output;

    private static string CoreLib => typeof(object).Assembly.Location;

    [Fact]
    public void A_microsoft_signed_runtime_file_verifies_and_names_its_signer()
    {
        bool valid = Authenticode.IsSignatureValid(CoreLib);
        var cn = Authenticode.SignerCommonName(CoreLib);
        _out.WriteLine($"{CoreLib}: valid={valid} signer='{cn}'");
        Assert.True(valid);
        Assert.False(string.IsNullOrWhiteSpace(cn));
        Assert.NotEqual("BUREL VINCENT Entrepreneur individuel", cn);
    }

    [Fact]
    public void An_unsigned_assembly_neither_verifies_nor_names_a_signer()
    {
        var own = typeof(Authenticode).Assembly.Location;
        bool valid = Authenticode.IsSignatureValid(own);
        var cn = Authenticode.SignerCommonName(own);
        _out.WriteLine($"{own}: valid={valid} signer='{cn ?? "(none)"}'");
        Assert.False(valid);
        Assert.Null(cn);
    }

    [Fact]
    public void One_flipped_byte_breaks_the_signature_but_not_the_name_which_is_why_both_are_checked()
    {
        var copy = Path.Combine(Path.GetTempPath(), "aorineq-authenticode-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.Copy(CoreLib, copy);
            var bytes = File.ReadAllBytes(copy);
            int at = bytes.Length / 2;
            bytes[at] ^= 0xFF;
            File.WriteAllBytes(copy, bytes);

            bool valid = Authenticode.IsSignatureValid(copy);
            var cn = Authenticode.SignerCommonName(copy);
            _out.WriteLine($"flipped byte {at} of {bytes.Length}: valid={valid} signer still reads '{cn}'");
            Assert.False(valid);
            Assert.NotNull(cn); // the certificate is still embedded — a name alone proves nothing
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void A_missing_file_is_simply_not_valid()
    {
        var missing = Path.Combine(Path.GetTempPath(), "aorineq-no-such-" + Guid.NewGuid().ToString("N") + ".exe");
        Assert.False(Authenticode.IsSignatureValid(missing));
        Assert.Null(Authenticode.SignerCommonName(missing));
        _out.WriteLine("missing file -> not valid, no signer");
    }
}
