// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using EssvbSub.Core.Models;

namespace EssvbSub.Capture.TvInstall;

public enum TizenSignatureDigest
{
    Sha256,
    Sha512
}

/// <summary>
/// Packages tv-tizen/ directly into a .wgt archive without external SDK CLI tools,
/// adapts config.xml for 65" CU8500 (Tizen 7.0), MU9000 (Tizen 3.0), and Tizen 2.4+ models,
/// and applies W3C Widget XML-DSig signatures.
/// </summary>
public static partial class TizenPackageAdapter
{
    private const string XmlDsigNs = "http://www.w3.org/2000/09/xmldsig#";
    private const string DspNs = "http://www.w3.org/2009/xmldsig-properties";
    private const string ExcC14n = "http://www.w3.org/2001/10/xml-exc-c14n#";
    private const string C14n11 = "http://www.w3.org/2006/12/xml-c14n11";

    public static bool IsVersionNewer(string candidate, string baseline)
    {
        var a = ParseParts(candidate);
        var b = ParseParts(baseline);
        int len = Math.Max(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            int va = i < a.Length ? a[i] : 0;
            int vb = i < b.Length ? b[i] : 0;
            if (va != vb) return va > vb;
        }
        return false;
    }

    private static int[] ParseParts(string version) =>
        version.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p.Trim(), out int n) ? n : 0)
            .ToArray();

    public static bool RequiresLegacyAdaptation(string tizenPlatform) =>
        !string.IsNullOrWhiteSpace(tizenPlatform) && IsVersionNewer("3.0", tizenPlatform);

    public static bool RequiresDeviceProfile(string tizenPlatform) =>
        !string.IsNullOrWhiteSpace(tizenPlatform) &&
        !IsVersionNewer("2.4", tizenPlatform) &&
        !IsVersionNewer(tizenPlatform, "4.0");

    public static string ResolvePlatformVersion(
        string? detectedPlatform,
        SamsungTvModel selectedModel,
        string? detectedModelName = null)
    {
        if (!string.IsNullOrWhiteSpace(detectedPlatform))
            return detectedPlatform.Trim();

        if (!string.IsNullOrWhiteSpace(detectedModelName))
        {
            if (detectedModelName.Contains("CU8500", StringComparison.OrdinalIgnoreCase))
                return "7.0";
            if (detectedModelName.Contains("MU9000", StringComparison.OrdinalIgnoreCase))
                return "3.0";
        }

        return selectedModel switch
        {
            SamsungTvModel.Cu8500_65 => "7.0",
            SamsungTvModel.Mu9000 => "3.0",
            SamsungTvModel.LegacyTizen24 => "2.4",
            _ => "7.0"
        };
    }

    public static TizenSignatureDigest GetRecommendedDigest(string tizenPlatform) =>
        RequiresLegacyAdaptation(tizenPlatform)
            ? TizenSignatureDigest.Sha256
            : TizenSignatureDigest.Sha512;

    public static string PrepareUnsignedWgtForDevice(
        string tvTizenDirectory,
        string outputWgtPath,
        string tizenPlatform,
        bool lean = false)
    {
        BuildUnsignedWgtFromFolder(tvTizenDirectory, outputWgtPath);
        if (lean || RequiresLegacyAdaptation(tizenPlatform))
            AdaptWgtInPlace(outputWgtPath, lean);
        else
            UpdateConfigXmlInWgt(outputWgtPath, xml => ScreenResolutionRegex().Replace(NetworkPublicRegex().Replace(xml, ""), ""));
        return outputWgtPath;
    }

    public static string PrepareAndSignWgtForDevice(
        string tvTizenDirectory,
        string outputWgtPath,
        string tizenPlatform,
        string authorP12Path,
        string distributorP12Path,
        string password,
        bool lean = false)
    {
        PrepareUnsignedWgtForDevice(tvTizenDirectory, outputWgtPath, tizenPlatform, lean);
        var digest = lean ? TizenSignatureDigest.Sha256 : GetRecommendedDigest(tizenPlatform);
        SignWgtInPlace(outputWgtPath, authorP12Path, distributorP12Path, password, digest);
        return outputWgtPath;
    }

    public static string BuildUnsignedWgtFromFolder(string tvTizenDirectory, string outputWgtPath)
    {
        if (!Directory.Exists(tvTizenDirectory))
            throw new DirectoryNotFoundException($"Tizen TV uygulama klasörü bulunamadı: {tvTizenDirectory}");

        Directory.CreateDirectory(Path.GetDirectoryName(outputWgtPath)!);
        if (File.Exists(outputWgtPath))
            File.Delete(outputWgtPath);

        using var archive = ZipFile.Open(outputWgtPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(tvTizenDirectory, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(tvTizenDirectory, file).Replace('\\', '/');
            if (rel.Equals("author-signature.xml", StringComparison.OrdinalIgnoreCase) ||
                rel.Equals("signature1.xml", StringComparison.OrdinalIgnoreCase))
                continue;

            archive.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
        }

        return outputWgtPath;
    }

    public static string AdaptConfigXml(string configXml, bool lean = false)
    {
        string cleaned = NetworkPublicRegex().Replace(configXml, "");
        cleaned = ScreenResolutionRegex().Replace(cleaned, "");
        if (lean)
        {
            cleaned = ProfileRegex().Replace(cleaned, "");
            cleaned = LeanPrivilegesRegex().Replace(cleaned, "");
        }
        return cleaned;
    }

    public static bool AdaptWgtInPlace(string wgtPath, bool lean = false) =>
        UpdateConfigXmlInWgt(wgtPath, xml => AdaptConfigXml(xml, lean));

    private static bool UpdateConfigXmlInWgt(string wgtPath, Func<string, string> transform)
    {
        string original;
        using (var readZip = ZipFile.OpenRead(wgtPath))
        {
            var entry = readZip.GetEntry("config.xml");
            if (entry is null) return false;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            original = reader.ReadToEnd();
        }

        string updated = transform(original);
        if (updated == original)
            return false;

        using var updateZip = ZipFile.Open(wgtPath, ZipArchiveMode.Update);
        var target = updateZip.GetEntry("config.xml")!;
        using var stream = target.Open();
        byte[] bytes = new UTF8Encoding(false).GetBytes(updated);
        stream.SetLength(0);
        stream.Write(bytes);
        return true;
    }

    public static void SignWgtInPlace(string wgtPath, string authorP12Path, string distributorP12Path, string password, TizenSignatureDigest digest)
    {
        using var author = LoadPkcs12(authorP12Path, password);
        using var distributor = LoadPkcs12(distributorP12Path, password);
        byte[] signed = SignWgtBytes(File.ReadAllBytes(wgtPath), author.Key, author.Chain, distributor.Key, distributor.Chain, digest);
        File.WriteAllBytes(wgtPath, signed);
    }

    internal static byte[] SignWgtBytes(
        byte[] wgtBytes,
        RSA authorKey,
        IReadOnlyList<byte[]> authorChain,
        RSA distributorKey,
        IReadOnlyList<byte[]> distributorChain,
        TizenSignatureDigest digest)
    {
        var entries = ReadContentEntries(wgtBytes);
        var authorSig = ("author-signature.xml", CreateSignatureXml("AuthorSignature", entries, authorKey, authorChain, digest));
        var distInputs = new List<(string Name, byte[] Data)>(entries.Count + 1) { authorSig };
        distInputs.AddRange(entries);
        var distSig = ("signature1.xml", CreateSignatureXml("DistributorSignature", distInputs, distributorKey, distributorChain, digest));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in (List<(string Name, byte[] Data)>)[authorSig, distSig, .. entries])
            {
                using var s = zip.CreateEntry(item.Name, CompressionLevel.Optimal).Open();
                s.Write(item.Data);
            }
        }
        return ms.ToArray();
    }

    private static List<(string Name, byte[] Data)> ReadContentEntries(byte[] wgtBytes)
    {
        var list = new List<(string Name, byte[] Data)>();
        using var zip = new ZipArchive(new MemoryStream(wgtBytes, writable: false), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            string lower = entry.FullName.ToLowerInvariant();
            if (entry.FullName.EndsWith('/') || lower.EndsWith("author-signature.xml") || lower.EndsWith("signature1.xml"))
                continue;
            using var ms = new MemoryStream();
            using var s = entry.Open();
            s.CopyTo(ms);
            list.Add((entry.FullName, ms.ToArray()));
        }
        return list;
    }

    private static byte[] CreateSignatureXml(
        string id,
        IReadOnlyList<(string Name, byte[] Data)> files,
        RSA key,
        IReadOnlyList<byte[]> certChain,
        TizenSignatureDigest digest)
    {
        string sigAlg = digest == TizenSignatureDigest.Sha256
            ? "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256"
            : "http://www.w3.org/2001/04/xmldsig-more#rsa-sha512";
        string digAlg = digest == TizenSignatureDigest.Sha256
            ? "http://www.w3.org/2001/04/xmlenc#sha256"
            : "http://www.w3.org/2001/04/xmlenc#sha512";

        string propCanonical = BuildPropertiesObject(id, canonical: true);
        string propDigest = ComputeDigest(Encoding.UTF8.GetBytes(propCanonical), digest);

        var si = new StringBuilder("<SignedInfo>");
        si.Append($"<CanonicalizationMethod Algorithm=\"{ExcC14n}\"></CanonicalizationMethod>");
        si.Append($"<SignatureMethod Algorithm=\"{sigAlg}\"></SignatureMethod>");
        foreach (var (name, data) in files)
        {
            si.Append("<Reference URI=\"").Append(EscapeXmlAttr(name)).Append("\">");
            si.Append($"<DigestMethod Algorithm=\"{digAlg}\"></DigestMethod>");
            si.Append("<DigestValue>").Append(ComputeDigest(data, digest)).Append("</DigestValue></Reference>");
        }
        si.Append($"<Reference URI=\"#prop\"><Transforms><Transform Algorithm=\"{C14n11}\"></Transform></Transforms>");
        si.Append($"<DigestMethod Algorithm=\"{digAlg}\"></DigestMethod><DigestValue>").Append(propDigest).Append("</DigestValue></Reference>");
        si.Append("</SignedInfo>");

        string signedInfo = si.ToString();
        string canonicalSignedInfo = $"<SignedInfo xmlns=\"{XmlDsigNs}\">" + signedInfo["<SignedInfo>".Length..];
        byte[] sigValue = key.SignData(
            Encoding.UTF8.GetBytes(canonicalSignedInfo),
            digest == TizenSignatureDigest.Sha256 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA512,
            RSASignaturePadding.Pkcs1);

        var xml = new StringBuilder();
        xml.Append($"<Signature xmlns=\"{XmlDsigNs}\" Id=\"{EscapeXmlAttr(id)}\">");
        xml.Append(signedInfo);
        xml.Append("<SignatureValue>").Append(Convert.ToBase64String(sigValue)).Append("</SignatureValue>");
        xml.Append("<KeyInfo><X509Data>");
        foreach (var cert in certChain)
        {
            string b64 = string.Join('\n', Convert.ToBase64String(cert).Chunk(76).Select(c => new string(c)));
            xml.Append("<X509Certificate>\n").Append(b64).Append("\n</X509Certificate>");
        }
        xml.Append("</X509Data></KeyInfo>");
        xml.Append(BuildPropertiesObject(id, canonical: false));
        xml.Append("</Signature>");
        return Encoding.UTF8.GetBytes(xml.ToString());
    }

    private static string BuildPropertiesObject(string id, bool canonical)
    {
        string role = id == "AuthorSignature" ? "author" : "distributor";
        string target = EscapeXmlAttr($"#{id}");
        string prop(string name, string child, string? uri) =>
            $"<SignatureProperty Id=\"{name}\" Target=\"{target}\"><dsp:{child}{(uri is null ? "" : $" URI=\"{EscapeXmlAttr(uri)}\"")}></dsp:{child}></SignatureProperty>";

        return (canonical ? $"<Object xmlns=\"{XmlDsigNs}\" Id=\"prop\">" : "<Object Id=\"prop\">") +
               $"<SignatureProperties xmlns:dsp=\"{DspNs}\">" +
               prop("profile", "Profile", "http://www.w3.org/ns/widgets-digsig#profile") +
               prop("role", "Role", $"http://www.w3.org/ns/widgets-digsig#role-{role}") +
               prop("identifier", "Identifier", null) +
               "</SignatureProperties></Object>";
    }

    private static string ComputeDigest(byte[] data, TizenSignatureDigest digest) =>
        Convert.ToBase64String(digest == TizenSignatureDigest.Sha256 ? SHA256.HashData(data) : SHA512.HashData(data));

    private static string EscapeXmlAttr(string val) =>
        val.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;")
           .Replace("\t", "&#x9;").Replace("\n", "&#xA;").Replace("\r", "&#xD;");

    private static LoadedSigner LoadPkcs12(string p12Path, string password)
    {
        var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        var certs = X509CertificateLoader.LoadPkcs12CollectionFromFile(p12Path, password, flags);
        var leaf = certs.FirstOrDefault(c => c.HasPrivateKey) ?? throw new CryptographicException("PKCS#12 özel anahtar içermiyor.");
        var rsa = leaf.GetRSAPrivateKey() ?? throw new CryptographicException("RSA anahtarı bulunamadı.");
        var chain = new List<byte[]> { leaf.RawData };
        var intermediate = PickBestIntermediateCa(leaf, certs.Where(c => c.Thumbprint != leaf.Thumbprint));
        if (intermediate is not null)
            chain.Add(intermediate.RawData);
        return new LoadedSigner(rsa, chain, certs);
    }

    internal static X509Certificate2? PickBestIntermediateCa(
        X509Certificate2 leaf,
        IEnumerable<X509Certificate2> candidates)
    {
        var matches = candidates
            .Where(ca => ca.Subject == leaf.Issuer && ca.Subject != ca.Issuer)
            .OrderByDescending(ca => ca.NotBefore)
            .ToList();

        foreach (var ca in matches)
        {
            try
            {
                using var xchain = new X509Chain();
                xchain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                xchain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority
                                                       | X509VerificationFlags.IgnoreNotTimeValid;
                xchain.ChainPolicy.ExtraStore.Add(ca);
                if (xchain.Build(leaf) &&
                    !xchain.ChainStatus.Any(s => (s.Status & X509ChainStatusFlags.NotSignatureValid) != 0) &&
                    xchain.ChainElements.Any(e => e.Certificate.Thumbprint == ca.Thumbprint))
                {
                    return ca;
                }
            }
            catch { }
        }

        return matches.FirstOrDefault();
    }

    private sealed record LoadedSigner(RSA Key, IReadOnlyList<byte[]> Chain, X509Certificate2Collection Certs) : IDisposable
    {
        public void Dispose()
        {
            Key.Dispose();
            foreach (var c in Certs) c.Dispose();
        }
    }

    [GeneratedRegex("""[ \t]*<tizen:privilege name="http://developer\.samsung\.com/privilege/network\.public"\s*/>[ \t]*\r?\n?""")]
    private static partial Regex NetworkPublicRegex();

    [GeneratedRegex("""[ \t]*<tizen:metadata key="http://tizen\.org/metadata/app_ui_type/base_screen_resolution"[^>]*/>[ \t]*\r?\n?""")]
    private static partial Regex ScreenResolutionRegex();

    [GeneratedRegex("""[ \t]*<tizen:profile\s[^>]*/>[ \t]*\r?\n?""")]
    private static partial Regex ProfileRegex();

    [GeneratedRegex("""[ \t]*<tizen:privilege name="http://tizen\.org/privilege/(application\.launch|network\.get)"\s*/>[ \t]*\r?\n?""")]
    private static partial Regex LeanPrivilegesRegex();
}
