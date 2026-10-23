using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BambuBar.Protocol;

/// <summary>
/// Decides whether a printer's TLS certificate is genuine.
///
/// Bambu printers present a leaf named after their serial number, issued by a per-model "BBL Device CA"
/// that chains to Bambu's own roots ("BBL CA", "BBL CA2 RSA/ECC"). Those roots ship with the app; the
/// hostname is not checked because the certificate names the serial, not the IP address.
/// </summary>
public static class BambuTrust
{
    public static readonly X509Certificate2Collection Anchors = LoadAnchors();

    private static X509Certificate2Collection LoadAnchors()
    {
        var anchors = new X509Certificate2Collection();
        foreach (var name in EmbeddedResources.Names("Certificates."))
        {
            using var stream = EmbeddedResources.Open(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            foreach (var cert in CertificatesFromPem(reader.ReadToEnd())) anchors.Add(cert);
        }
        return anchors;
    }

    public static IEnumerable<X509Certificate2> CertificatesFromPem(string pem)
    {
        const string begin = "-----BEGIN CERTIFICATE-----", end = "-----END CERTIFICATE-----";
        foreach (var chunk in pem.Split(begin).Skip(1))
        {
            var body = chunk.Split(end)[0];
            var base64 = new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray());
            byte[] der;
            try { der = Convert.FromBase64String(base64); }
            catch (FormatException) { continue; }
            yield return X509CertificateLoader.LoadCertificate(der);
        }
    }

    public abstract record Verdict
    {
        /// <summary>Chains to a bundled Bambu root and is named after the expected printer.</summary>
        public sealed record Trusted : Verdict;
        public sealed record Rejected(string Reason) : Verdict;
    }

    /// <summary>The most recent verdict, for <c>--probe</c> diagnostics.</summary>
    public static Verdict? LastVerdict { get; private set; }

    /// <summary>Both conditions matter: the chain proves it's a Bambu device, the name proves it's <i>this</i> printer
    /// (another Bambu printer on the network must not be able to collect the access code).</summary>
    public static Verdict Evaluate(X509Certificate? presented, X509Chain? presentedChain, string serial)
    {
        var verdict = Check(presented, presentedChain, serial, Anchors);
        LastVerdict = verdict;
        return verdict;
    }

    internal static Verdict Check(X509Certificate? presented, X509Chain? presentedChain, string serial, X509Certificate2Collection anchors)
    {
        if (presented is null) return new Verdict.Rejected("The printer sent no certificate.");
        using var leaf = X509CertificateLoader.LoadCertificate(presented.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        // Bambu's self-signed roots anchor the chain; the bundled per-model device CAs (and the cross-signed CA2) help
        // build it when a printer doesn't send its issuer along.
        foreach (var anchor in anchors)
        {
            if (anchor.SubjectName.RawData.AsSpan().SequenceEqual(anchor.IssuerName.RawData)) chain.ChainPolicy.CustomTrustStore.Add(anchor);
            else chain.ChainPolicy.ExtraStore.Add(anchor);
        }
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // The intermediates the printer sent along (its "BBL Device CA").
        if (presentedChain is not null)
        {
            foreach (var element in presentedChain.ChainElements)
            {
                if (!element.Certificate.RawData.AsSpan().SequenceEqual(leaf.RawData)) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
            chain.ChainPolicy.ExtraStore.AddRange(presentedChain.ChainPolicy.ExtraStore);
        }
        if (!chain.Build(leaf))
        {
            var status = chain.ChainStatus.FirstOrDefault(s => s.Status != X509ChainStatusFlags.NoError);
            var reason = string.IsNullOrWhiteSpace(status.StatusInformation) ? "The certificate isn't from Bambu Lab." : status.StatusInformation.Trim();
            return new Verdict.Rejected(reason);
        }
        var name = leaf.GetNameInfo(X509NameType.SimpleName, false);
        if (string.IsNullOrEmpty(name) || !string.Equals(name, serial, StringComparison.OrdinalIgnoreCase))
            return new Verdict.Rejected($"The certificate belongs to another device ({(string.IsNullOrEmpty(name) ? "unnamed" : name)}).");
        return new Verdict.Trusted();
    }
}
