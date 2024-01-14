using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using osu.Framework.IO.Network;

namespace osu.Desktop.Deploy
{
    public class GithubReleaseHelper
    {
        private readonly string _apiEndpoint;
        private readonly string _accessToken;

        public GithubReleaseHelper(string apiEndpoint, string accessToken)
        {
            _apiEndpoint = apiEndpoint;
            _accessToken = accessToken;
            if (String.IsNullOrEmpty(_apiEndpoint))
                throw new ArgumentException("apiEndpoint cannot be null or empty", nameof(apiEndpoint));
            if (String.IsNullOrEmpty(_accessToken))
                throw new ArgumentException("accessToken cannot be null or empty", nameof(accessToken));
        }

        public string GetNextGitHubReleaseVersion()
        {
            GitHubRelease? lastRelease = getLastGithubRelease();

            //increment build number until we have a unique one.
            string verBase = DateTime.Now.ToString("yyyy.Mdd.");
            int increment = 0;

            if (lastRelease?.TagName.StartsWith(verBase, StringComparison.InvariantCulture) ?? false)
                increment = int.Parse(lastRelease.TagName.Split('.')[2]) + 1;

            string version = $"{verBase}{increment}";
            return version;
        }

        public void UploadBuild(string version, string releases_folder)
        {
            Log.write("Publishing to GitHub...");

            var req = new JsonWebRequest<GitHubRelease>(_apiEndpoint)
            {
                Method = HttpMethod.Post,
            };

            GitHubRelease? targetRelease = getLastGithubRelease(true);

            if (targetRelease == null || targetRelease.TagName != version)
            {
                Log.write($"- Creating release {version}...", ConsoleColor.Yellow);
                req.AddRaw(JsonConvert.SerializeObject(new GitHubRelease
                {
                    Name = version,
                    Draft = true,
                }));
                AuthenticatedBlockingPerform(req);

                targetRelease = req.ResponseObject;
            }
            else
            {
                Log.write($"- Adding to existing release {version}...", ConsoleColor.Yellow);
            }

            Debug.Assert(targetRelease.UploadUrl != null);

            var assetUploadUrl = targetRelease.UploadUrl.Replace("{?name,label}", "?name={0}");
            foreach (var a in Directory.GetFiles(releases_folder).Reverse()) //reverse to upload RELEASES first.
            {
                if (Path.GetFileName(a).StartsWith('.'))
                    continue;

                Log.write($"- Adding asset {a}...", ConsoleColor.Yellow);
                var upload = new WebRequest(assetUploadUrl, Path.GetFileName(a))
                {
                    Method = HttpMethod.Post,
                    Timeout = 240000,
                    ContentType = "application/octet-stream",
                };

                upload.AddRaw(File.ReadAllBytes(a));
                AuthenticatedBlockingPerform(upload);
            }
        }

        private GitHubRelease? getLastGithubRelease(bool includeDrafts = false)
        {
            var req = new JsonWebRequest<List<GitHubRelease>>($"{_apiEndpoint}");
            AuthenticatedBlockingPerform(req);
            return req.ResponseObject.FirstOrDefault(r => includeDrafts || !r.Draft);
        }

        private void AuthenticatedBlockingPerform(WebRequest r)
        {
            r.AddHeader("Authorization", $"token {_accessToken}");
            r.Perform();
        }

        private class GitHubObject
        {
            [JsonProperty(@"id")]
            public int Id;

            [JsonProperty(@"name")]
            public string Name = string.Empty;
        }

        private class GitHubRelease
        {
            [JsonProperty(@"id")]
            public int Id;

            [JsonProperty(@"tag_name")]
            public string TagName => $"{Name}";

            [JsonProperty(@"name")]
            public string Name = string.Empty;

            [JsonProperty(@"draft")]
            public bool Draft;

            [JsonProperty(@"prerelease")]
            public bool PreRelease;

            [JsonProperty(@"upload_url")]
            public string UploadUrl = string.Empty;
        }
    }
}
