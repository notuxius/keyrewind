using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;

[DataContract]
internal sealed class ReleaseUpdate
{
    internal const string ReleasesPage = "https://github.com/notuxius/keyrewind/releases";
    private const string LatestApi = "https://api.github.com/repos/notuxius/keyrewind/releases/latest";
    private const string ExecutableName = "KeyRewind.exe";
    private const int MaxExecutableSize = 16 * 1024 * 1024;

    [DataMember(Name = "tag_name")] private string tag = null;
    [DataMember(Name = "draft")] private bool draft = false;
    [DataMember(Name = "prerelease")] private bool prerelease = false;
    [DataMember(Name = "assets")] private Asset[] assets = null;

    internal Version Version { get; private set; }
    internal string Digest { get { return executable.digest.Substring(7); } }
    private Asset executable;

    internal static ReleaseUpdate Check(Version installedVersion, CancellationToken cancellation)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var request = CreateRequest(LatestApi);
        request.AllowAutoRedirect = false;
        request.Accept = "application/vnd.github+json";
        request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
        using (cancellation.Register(request.Abort))
        using (var response = GetResponse(request))
        using (var stream = response.GetResponseStream())
            return ReadLatest(stream, installedVersion);
    }

    internal static ReleaseUpdate ReadLatest(Stream stream, Version installedVersion)
    {
        var release = (ReleaseUpdate)new DataContractJsonSerializer(typeof(ReleaseUpdate)).ReadObject(stream);
        Version version;
        if (release == null || release.draft || release.prerelease || string.IsNullOrEmpty(release.tag) ||
            release.tag[0] != 'v' || !Version.TryParse(release.tag.Substring(1), out version) ||
            version.Build < 0 || version.Revision >= 0 || release.tag != "v" + version)
            throw new InvalidDataException("GitHub did not return a stable KeyRewind version.");
        if (version <= installedVersion) return null;
        release.Version = version;
        if (release.assets != null)
        {
            foreach (var asset in release.assets)
            {
                if (asset == null || asset.name != ExecutableName) continue;
                if (release.executable != null || asset.size < 1 || asset.size > MaxExecutableSize ||
                    asset.digest == null || !asset.digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                    !IsDigest(asset.digest.Substring(7)))
                    throw new InvalidDataException("GitHub did not provide valid download details for this release.");
                release.executable = asset;
            }
        }
        if (release.executable == null) throw new InvalidDataException("The release is missing KeyRewind.exe.");
        return release;
    }

    internal byte[] Download(CancellationToken cancellation)
    {
        var request = CreateRequest(ReleasesPage + "/download/" + tag + "/" + ExecutableName);
        using (cancellation.Register(request.Abort))
        using (var response = GetResponse(request))
        using (var stream = response.GetResponseStream())
            return ReadExecutable(stream);
    }

    internal byte[] ReadExecutable(Stream stream)
    {
        var bytes = new byte[executable.size];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new InvalidDataException("The update download is incomplete.");
            offset += count;
        }
        using (var sha = SHA256.Create())
        {
            if (stream.ReadByte() != -1 || !MatchesDigest(sha.ComputeHash(bytes), Digest))
                throw new InvalidDataException("The update did not match GitHub’s checksum.");
        }
        return bytes;
    }

    internal static bool VerifyFile(string path, string digest)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create())
            return MatchesDigest(sha.ComputeHash(stream), digest);
    }

    internal static bool IsDigest(string digest)
    {
        if (digest.Length != 64) return false;
        foreach (char c in digest)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static bool MatchesDigest(byte[] hash, string digest)
    {
        return string.Equals(BitConverter.ToString(hash).Replace("-", ""), digest, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpWebRequest CreateRequest(string url)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.UserAgent = Program.Name + "-Updater";
        request.Timeout = 15000;
        request.ReadWriteTimeout = 15000;
        return request;
    }

    private static HttpWebResponse GetResponse(HttpWebRequest request)
    {
        try
        {
            var response = (HttpWebResponse)request.GetResponse();
            if (response.StatusCode == HttpStatusCode.OK && response.ResponseUri.Scheme == Uri.UriSchemeHttps)
                return response;
            response.Dispose();
            throw new IOException("GitHub returned an unexpected update response.");
        }
        catch (WebException error)
        {
            using (var response = error.Response as HttpWebResponse)
            {
                if (response != null && ((int)response.StatusCode == 429 ||
                    response.StatusCode == HttpStatusCode.Forbidden &&
                    (response.Headers["X-RateLimit-Remaining"] == "0" || response.Headers["Retry-After"] != null)))
                {
                    var retryAt = RetryAt(response.Headers, DateTimeOffset.UtcNow);
                    throw new UpdateRateLimitException(retryAt);
                }
            }
            throw;
        }
    }

    internal static DateTimeOffset RetryAt(WebHeaderCollection headers, DateTimeOffset now)
    {
        DateTimeOffset retryAt = now.AddMinutes(1);
        long seconds;
        DateTimeOffset retryHeader;
        if (long.TryParse(headers["Retry-After"], NumberStyles.None, CultureInfo.InvariantCulture, out seconds) &&
            seconds <= (DateTimeOffset.MaxValue - now).TotalSeconds - 1)
            retryAt = now.AddSeconds(Math.Max(seconds, 60));
        else if (DateTimeOffset.TryParse(headers["Retry-After"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out retryHeader) &&
            retryHeader > retryAt)
            retryAt = retryHeader;
        if (headers["X-RateLimit-Remaining"] == "0" &&
            long.TryParse(headers["X-RateLimit-Reset"], NumberStyles.None, CultureInfo.InvariantCulture, out seconds) &&
            seconds <= 253402300799L)
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (reset > retryAt) retryAt = reset;
        }
        return retryAt;
    }

    [DataContract]
    private sealed class Asset
    {
        [DataMember] internal string name = null;
        [DataMember] internal int size = 0;
        [DataMember] internal string digest = null;
    }
}

internal sealed class UpdateRateLimitException : IOException
{
    internal readonly DateTimeOffset RetryAt;

    internal UpdateRateLimitException(DateTimeOffset retryAt) : base("GitHub is temporarily limiting update requests.")
    {
        RetryAt = retryAt;
    }
}
