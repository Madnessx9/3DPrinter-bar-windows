using System.Text;
using BambuBar.Protocol;

namespace BambuBar.Tests;

public class SlicerImportTests
{
    [Fact]
    public void MersenneTwisterMatchesStdMt19937()
    {
        // The C++ standard requires the 10000th output of a default-seeded mt19937 to be 4123659995.
        var rng = new MersenneTwister(5489);
        Assert.Equal(3499211612u, rng.Next());
        for (var i = 2; i < 10000; i++) rng.Next();
        Assert.Equal(4123659995u, rng.Next());
    }

    private static string Encode(string ip, string key)
    {
        uint seed = 2_166_136_261;
        foreach (var b in Encoding.UTF8.GetBytes(key)) seed = unchecked((seed ^ b) * 16_777_619);
        var rng = new MersenneTwister(seed);
        return string.Concat(Encoding.UTF8.GetBytes(ip).Select(b => ((byte)(b ^ (byte)rng.Next())).ToString("x2")));
    }

    [Fact]
    public void DecodesScrambledAddresses()
    {
        const string key = "8f0b6c43-5c1e-4b38-b1f6-0d8e8f7f3a21";
        Assert.Equal("192.168.1.42", SlicerImport.DecodeIp(Encode("192.168.1.42", key), key));
        Assert.Equal("10.0.0.7", SlicerImport.DecodeIp("10.0.0.7", key));
        Assert.Null(SlicerImport.DecodeIp(Encode("192.168.1.42", key), "another-key"));
    }

    [Fact]
    public void ReadsBambuStudioSettings()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var conf = Path.Combine(dir.FullName, "BambuStudio", "BambuStudio.conf");
            Directory.CreateDirectory(Path.GetDirectoryName(conf)!);
            const string uuid = "abc-123";
            File.WriteAllText(conf, $$"""
                {
                  "app": { "slicer_uuid": "{{uuid}}" },
                  "access_code": { "01P00A000000001": "11111111", "01S00C000000002": "22222222" },
                  "user_access_code": { "01P00A000000001": "33333333" },
                  "ip_address": { "01P00A000000001": "192.168.1.10" },
                  "user_access_dev_ip": { "01S00C000000002": "{{Encode("192.168.1.20", uuid)}}" }
                }
                # MD5 checksum 0123456789abcdef
                """);
            var printers = SlicerImport.ReadPicked(dir.FullName);
            Assert.Equal(2, printers.Count);
            Assert.Equal(["11111111", "33333333"], printers[0].AccessCodes);
            Assert.Equal("192.168.1.10", printers[0].Host);
            Assert.Equal("192.168.1.20", printers[1].Host);
            Assert.Equal("Bambu Studio", printers[1].Source);
            Assert.Equal(printers, SlicerImport.ReadPicked(conf).Select(p => p with { Source = "Bambu Studio" }).ToList());
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void ParsesSsdpAnnouncements()
    {
        const string text = "NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1990\r\nLocation: 192.168.1.30\r\nNT: urn:bambulab-com:device:3dprinter:1\r\n" +
            "USN: 01P00A000000001\r\nDevModel.bambu.com: C11\r\nDevName.bambu.com: Workshop P1P\r\nDevConnect.bambu.com: lan\r\nDevBind.bambu.com: free\r\n\r\n";
        var found = PrinterDiscovery.Parse(text, "192.168.1.30");
        Assert.NotNull(found);
        Assert.Equal("01P00A000000001", found.Serial);
        Assert.Equal("Workshop P1P", found.Name);
        Assert.Equal("C11", found.ModelCode);
    }
}
