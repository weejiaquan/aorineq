using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Real WinVerifyTrust against real files on this machine. The runtime's own
/// System.Private.CoreLib.dll is Authenticode-signed by Microsoft (CN=.NET), which makes it a valid
/// signature with the WRONG signer for VB-Cable's purposes; AorinEQ's own locally built assembly is
/// unsigned. No VB-Audio-signed file ships with the repo, so the right-signer case is the literal
/// compared against: the wrong-signer test proves it is not matched by accident.</summary>
public class AuthenticodeTests
{
    private const string VbAudioSigner = "BUREL VINCENT Entrepreneur individuel";
    private readonly ITestOutputHelper _out;
    public AuthenticodeTests(ITestOutputHelper output) => _out = output;

    private static string CoreLib => typeof(object).Assembly.Location;

    private static FileStream OpenLocked(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    [Fact]
    public void A_validly_signed_file_by_another_publisher_yields_that_publishers_name_not_vb_audios()
    {
        using var file = OpenLocked(CoreLib);
        var cn = Authenticode.VerifiedSignerCommonName(file);
        _out.WriteLine($"{CoreLib}: verified signer CN='{cn}'");
        Assert.Equal(".NET", cn);
        Assert.NotEqual(VbAudioSigner, cn);
    }

    [Fact]
    public void An_unsigned_assembly_has_no_verified_signer()
    {
        var own = typeof(Authenticode).Assembly.Location;
        using var file = OpenLocked(own);
        var cn = Authenticode.VerifiedSignerCommonName(file);
        _out.WriteLine($"{own}: verified signer='{cn ?? "(none)"}'");
        Assert.Null(cn);
    }

    [Fact]
    public void One_flipped_byte_voids_the_signature_so_the_embedded_name_is_not_returned()
    {
        var copy = Path.Combine(Path.GetTempPath(), "aorineq-authenticode-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.Copy(CoreLib, copy);
            var bytes = File.ReadAllBytes(copy);
            int at = bytes.Length / 2;
            bytes[at] ^= 0xFF;
            File.WriteAllBytes(copy, bytes);

            using var file = OpenLocked(copy);
            var cn = Authenticode.VerifiedSignerCommonName(file);
            _out.WriteLine($"flipped byte {at} of {bytes.Length}: verified signer='{cn ?? "(none)"}'");
            Assert.Null(cn); // the certificate is still embedded, but it vouches for nothing
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void A_file_held_open_for_verification_cannot_be_swapped_underneath()
    {
        var copy = Path.Combine(Path.GetTempPath(), "aorineq-authenticode-lock-" + Guid.NewGuid().ToString("N") + ".dll");
        var moved = copy + ".moved";
        try
        {
            File.Copy(CoreLib, copy);
            using (var file = OpenLocked(copy))
            {
                Assert.Equal(".NET", Authenticode.VerifiedSignerCommonName(file));
                var write = Assert.Throws<IOException>(() => File.WriteAllBytes(copy, new byte[] { 1 }));
                var delete = Assert.Throws<IOException>(() => File.Delete(copy));
                var move = Assert.Throws<IOException>(() => File.Move(copy, moved));
                _out.WriteLine($"while open: write -> {write.GetType().Name}, delete -> {delete.GetType().Name}, move -> {move.GetType().Name}");
            }
            File.Delete(copy); // the lock is released with the stream
            Assert.False(File.Exists(copy));
            _out.WriteLine("after close: delete succeeds");
        }
        finally
        {
            File.Delete(copy);
            File.Delete(moved);
        }
    }
}
