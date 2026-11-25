using System;
using System.IO;
using System.Net;
using System.Text;

internal static class UpdateSmokeTests
{
    private const string Digest = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    internal static void Run()
    {
        var installed = new Version(0, 4, 0);
        var newer = Parse("v0.10.0", false, Digest, installed);
        SmokeTests.Check(newer.Version == new Version(0, 10, 0), "Release versions must compare numerically");
        SmokeTests.Check(Parse("v0.4.0", false, Digest, installed) == null &&
            Parse("v0.3.9", false, Digest, installed) == null, "Updates must be newer than the installed version");
        Reject(() => Parse("v0.4.1", true, Digest, installed), "Prereleases must be rejected");
        Reject(() => Parse("v00.4.1", false, Digest, installed), "Noncanonical versions must be rejected");
        Reject(() => Parse("v0.4.1", false, "invalid", installed), "An update must have a SHA-256 digest");

        using (var payload = new MemoryStream(Encoding.UTF8.GetBytes("abc")))
            SmokeTests.Check(Encoding.UTF8.GetString(newer.ReadExecutable(payload)) == "abc", "The verified payload should be returned");
        foreach (string text in new[] { "abd", "ab", "abcd" })
        {
            using (var payload = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                Reject(() => newer.ReadExecutable(payload), "Altered, truncated or oversized downloads must be rejected");
        }

        var now = DateTimeOffset.FromUnixTimeSeconds(1000000);
        var headers = new WebHeaderCollection { { "Retry-After", "120" }, { "X-RateLimit-Remaining", "0" },
            { "X-RateLimit-Reset", "1000300" } };
        SmokeTests.Check(ReleaseUpdate.RetryAt(headers, now) == now.AddMinutes(5),
            "Rate-limited checks must wait for both Retry-After and the rate limit reset");
    }

    private static ReleaseUpdate Parse(string version, bool prerelease, string digest, Version installed)
    {
        string json = "{\"tag_name\":\"" + version + "\",\"prerelease\":" + (prerelease ? "true" : "false") +
            ",\"assets\":[{\"name\":\"KeyRewind.exe\",\"size\":3,\"digest\":\"sha256:" + digest + "\"}]}";
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            return ReleaseUpdate.ReadLatest(stream, installed);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception(message);
    }
}
