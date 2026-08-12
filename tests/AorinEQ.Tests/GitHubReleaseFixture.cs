namespace AorinEQ.Tests;

/// <summary>A REAL response from
/// GET https://api.github.com/repos/weejiaquan/aorineq/releases/latest, captured 2026-08-12
/// (the v1.8.0 release). Fixture of a real response — not a hand-invented feed. v1.8.0 predates
/// the sha256-asset pipeline requirement, so this fixture doubles as the missing-sha case;
/// tests that need the sha asset splice one in via <see cref="WithSha256Asset"/> using the same
/// asset shape.</summary>
public static class GitHubReleaseFixture
{
    public const string LatestV180Json = """
        {
          "url": "https://api.github.com/repos/weejiaquan/aorineq/releases/368921684",
          "assets_url": "https://api.github.com/repos/weejiaquan/aorineq/releases/368921684/assets",
          "upload_url": "https://uploads.github.com/repos/weejiaquan/aorineq/releases/368921684/assets{?name,label}",
          "html_url": "https://github.com/weejiaquan/aorineq/releases/tag/v1.8.0",
          "id": 368921684,
          "author": {
            "login": "weejiaquan",
            "id": 15049008,
            "node_id": "MDQ6VXNlcjE1MDQ5MDA4",
            "avatar_url": "https://avatars.githubusercontent.com/u/15049008?v=4",
            "gravatar_id": "",
            "url": "https://api.github.com/users/weejiaquan",
            "html_url": "https://github.com/weejiaquan",
            "type": "User",
            "user_view_type": "public",
            "site_admin": false
          },
          "node_id": "RE_kwDOTe67O84V_UxU",
          "tag_name": "v1.8.0",
          "target_commitish": "master",
          "name": "v1.8.0",
          "draft": false,
          "immutable": false,
          "prerelease": false,
          "created_at": "2026-08-12T00:10:26Z",
          "updated_at": "2026-08-12T00:12:15Z",
          "published_at": "2026-08-12T00:11:02Z",
          "assets": [
            {
              "url": "https://api.github.com/repos/weejiaquan/aorineq/releases/assets/510818473",
              "id": 510818473,
              "node_id": "RA_kwDOTe67O84ecnip",
              "name": "AorinEQ.exe",
              "label": "",
              "content_type": "application/x-msdownload",
              "state": "uploaded",
              "size": 71650928,
              "digest": "sha256:f82b23b87de02c5b5d58d57915030ca434b760a06be2c9611e735fad58851587",
              "download_count": 0,
              "created_at": "2026-08-12T00:12:10Z",
              "updated_at": "2026-08-12T00:12:15Z",
              "browser_download_url": "https://github.com/weejiaquan/aorineq/releases/download/v1.8.0/AorinEQ.exe"
            }
          ],
          "tarball_url": "https://api.github.com/repos/weejiaquan/aorineq/tarball/v1.8.0",
          "zipball_url": "https://api.github.com/repos/weejiaquan/aorineq/zipball/v1.8.0",
          "body": "Volume mode, onboarding mode choice, percent-text alignment, custom muted artwork + mute-dim slider, two-column Settings."
        }
        """;

    /// <summary>The same release with an AorinEQ.exe.sha256 asset spliced in (identical asset
    /// shape) — what every release from v1.9.0 on looks like per the ship pipeline.</summary>
    public static string WithSha256Asset(string tag = "v1.8.0") => LatestV180Json
        .Replace("\"tag_name\": \"v1.8.0\"", $"\"tag_name\": \"{tag}\"")
        .Replace("\"assets\": [", """
            "assets": [
                {
                  "url": "https://api.github.com/repos/weejiaquan/aorineq/releases/assets/510818474",
                  "id": 510818474,
                  "node_id": "RA_kwDOTe67O84ecniq",
                  "name": "AorinEQ.exe.sha256",
                  "label": "",
                  "content_type": "text/plain",
                  "state": "uploaded",
                  "size": 78,
                  "download_count": 0,
                  "created_at": "2026-08-12T00:12:10Z",
                  "updated_at": "2026-08-12T00:12:15Z",
                  "browser_download_url": "https://github.com/weejiaquan/aorineq/releases/download/v1.8.0/AorinEQ.exe.sha256"
                },
            """);
}
