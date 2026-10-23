using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BambuBar.Protocol;

namespace BambuBar.Tests;

public class TrustTests
{
    private static X509Certificate2 Authority(string name, X509Certificate2? issuer = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        if (issuer is null) return request.CreateSelfSigned(from, from.AddYears(10));
        var serial = RandomNumberGenerator.GetBytes(8);
        using var signed = request.Create(issuer, from, from.AddYears(5), serial);
        return signed.CopyWithPrivateKey(key);
    }

    private static X509Certificate2 Leaf(string serialNumber, X509Certificate2 issuer)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={serialNumber}", key, HashAlgorithmName.SHA256);
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        return request.Create(issuer, from, from.AddYears(1), RandomNumberGenerator.GetBytes(8));
    }

    [Fact]
    public void BundledAnchorsLoad()
    {
        Assert.True(BambuTrust.Anchors.Count >= 4);
        Assert.Contains(BambuTrust.Anchors.Cast<X509Certificate2>(), c => c.Subject.Contains("BBL CA"));
    }

    [Fact]
    public void ADeviceCaBetweenRootAndLeafIsFollowed()
    {
        // Like a printer: root ("BBL CA2") → bundled device CA (intermediate) → leaf named after the serial.
        using var root = Authority("Test Root");
        using var device = Authority("Test Device CA", root);
        using var leaf = Leaf("01P00A000000001", device);
        var anchors = new X509Certificate2Collection { X509CertificateLoader.LoadCertificate(root.RawData), X509CertificateLoader.LoadCertificate(device.RawData) };
        Assert.IsType<BambuTrust.Verdict.Trusted>(BambuTrust.Check(leaf, null, "01P00A000000001", anchors));
        Assert.IsType<BambuTrust.Verdict.Rejected>(BambuTrust.Check(leaf, null, "01P00A000000002", anchors));

        // Without the intermediate the chain can't be built.
        var rootOnly = new X509Certificate2Collection { X509CertificateLoader.LoadCertificate(root.RawData) };
        Assert.IsType<BambuTrust.Verdict.Rejected>(BambuTrust.Check(leaf, null, "01P00A000000001", rootOnly));

        // An intermediate alone is not a root of trust.
        var deviceOnly = new X509Certificate2Collection { X509CertificateLoader.LoadCertificate(device.RawData) };
        Assert.IsType<BambuTrust.Verdict.Rejected>(BambuTrust.Check(leaf, null, "01P00A000000001", deviceOnly));
    }

    [Fact]
    public void AnotherRootIsRejected()
    {
        using var root = Authority("Someone Else");
        using var leaf = Leaf("01P00A000000001", root);
        Assert.IsType<BambuTrust.Verdict.Rejected>(BambuTrust.Evaluate(leaf, null, "01P00A000000001"));
    }
}
