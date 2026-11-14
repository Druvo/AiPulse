using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AiPulse.Services;
using AiPulse.Tests.E2E;
using Xunit;

namespace AiPulse.Tests;

public class AppDownloadsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "downloads-" + Guid.NewGuid().ToString("N"));

    public AppDownloadsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private void Install(byte[] apk, string? sha = null, string file = "AiPulse-1.2.0.apk", string version = "1.2.0")
    {
        File.WriteAllBytes(Path.Combine(_dir, file), apk);
        File.WriteAllText(Path.Combine(_dir, "latest.json"), JsonSerializer.Serialize(new { version, versionCode = 7, file, sha256 = sha ?? Sha(apk), size = apk.Length }));
    }

    [Fact]
    public void Nothing_is_offered_until_a_build_is_installed()
    {
        Assert.Null(new AppDownloads(_dir).Android());
        Assert.Null(new AppDownloads(Path.Combine(_dir, "does-not-exist")).Android());
    }

    [Fact]
    public void A_build_whose_checksum_matches_is_offered_with_its_details()
    {
        var apk = new byte[2_500_000];
        new Random(1).NextBytes(apk);
        Install(apk);

        var info = new AppDownloads(_dir).Android();

        Assert.NotNull(info);
        Assert.Equal("1.2.0", info!.Version);
        Assert.Equal(7, info.VersionCode);
        Assert.Equal(apk.Length, info.Size);
        Assert.Equal(Sha(apk), info.Sha256);
    }

    [Fact]
    public void A_file_that_does_not_match_its_checksum_is_never_offered()
    {
        Install(new byte[] { 1, 2, 3, 4 }, sha: new string('0', 64));
        Assert.Null(new AppDownloads(_dir).Android());
    }

    [Fact]
    public void Swapping_the_file_after_it_was_verified_is_noticed()
    {
        Install(new byte[] { 1, 2, 3, 4 });
        var downloads = new AppDownloads(_dir);
        Assert.NotNull(downloads.Android());

        File.WriteAllBytes(Path.Combine(_dir, "AiPulse-1.2.0.apk"), new byte[] { 9, 9, 9, 9, 9 });
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "AiPulse-1.2.0.apk"), DateTime.UtcNow.AddMinutes(1));

        Assert.Null(downloads.Android());
    }

    [Theory]
    [InlineData("../secret.apk")]
    [InlineData("sub/dir.apk")]
    [InlineData("..\\secret.apk")]
    [InlineData("notes.txt")]
    [InlineData("")]
    public void The_metadata_cannot_point_outside_the_folder_or_at_non_apk_files(string file)
    {
        File.WriteAllBytes(Path.Combine(_dir, "x.apk"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(_dir, "latest.json"), JsonSerializer.Serialize(new { version = "1", file, sha256 = Sha(new byte[] { 1 }) }));
        Assert.Null(new AppDownloads(_dir).Android());
    }

    [Fact]
    public void Broken_metadata_means_no_download_not_an_error()
    {
        File.WriteAllText(Path.Combine(_dir, "latest.json"), "{ not json");
        Assert.Null(new AppDownloads(_dir).Android());
        File.WriteAllText(Path.Combine(_dir, "latest.json"), "{}");
        Assert.Null(new AppDownloads(_dir).Android());
    }

    [Theory]
    [InlineData(500, "1 KB")]
    [InlineData(2048, "2 KB")]
    [InlineData(40_265_318, "38.4 MB")]
    public void Sizes_read_naturally(long bytes, string expected) => Assert.Equal(expected, AppDownloads.FormatSize(bytes));
}

[Collection(E2ECollection.Name)]
public class AppDownloadEndpointTests(AiPulseAppFactory app)
{
    private string DownloadsDir => Path.Combine(app.ContentRoot, "App_Data", AppDownloads.Folder);

    [Fact]
    public async Task The_endpoints_say_not_found_when_no_build_is_installed_and_the_page_hides_the_card()
    {
        if (Directory.Exists(DownloadsDir)) Directory.Delete(DownloadsDir, true);
        var client = app.Anonymous();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/download/android")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/download/android.json")).StatusCode);
        Assert.DoesNotContain("Download the Android app", await client.GetStringAsync("/app"));
    }

    [Fact]
    public async Task An_installed_build_is_downloadable_by_anyone_resumable_and_shown_on_the_page()
    {
        var apk = new byte[300_000];
        new Random(7).NextBytes(apk);
        Directory.CreateDirectory(DownloadsDir);
        File.WriteAllBytes(Path.Combine(DownloadsDir, "AiPulse-9.9.9.apk"), apk);
        var sha = Convert.ToHexString(SHA256.HashData(apk)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(DownloadsDir, "latest.json"), JsonSerializer.Serialize(new { version = "9.9.9", versionCode = 99, file = "AiPulse-9.9.9.apk", sha256 = sha, size = apk.Length }));
        try
        {
            var client = app.Anonymous();

            var full = await client.GetAsync("/download/android");
            Assert.Equal(HttpStatusCode.OK, full.StatusCode);
            Assert.Equal("application/vnd.android.package-archive", full.Content.Headers.ContentType?.MediaType);
            Assert.Contains("AiPulse-9.9.9.apk", full.Content.Headers.ContentDisposition?.ToString());
            Assert.Equal(apk, await full.Content.ReadAsByteArrayAsync());

            var request = new HttpRequestMessage(HttpMethod.Get, "/download/android");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, 199);
            var partial = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
            Assert.Equal(apk[100..200], await partial.Content.ReadAsByteArrayAsync());

            using var meta = JsonDocument.Parse(await client.GetStringAsync("/download/android.json"));
            Assert.Equal("9.9.9", meta.RootElement.GetProperty("version").GetString());
            Assert.Equal(sha, meta.RootElement.GetProperty("sha256").GetString());

            var page = await client.GetStringAsync("/app");
            Assert.Contains("Download the Android app", page);
            Assert.Contains("9.9.9", page);
            Assert.Contains(sha, page);
        }
        finally { Directory.Delete(DownloadsDir, true); }
    }
}
