﻿// Copyright (c) 2007-2018 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu/master/LICENCE

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using Newtonsoft.Json;
using osu.Framework.IO.Network;
using FileWebRequest = osu.Framework.IO.Network.FileWebRequest;
using WebRequest = osu.Framework.IO.Network.WebRequest;

namespace osu.Desktop.Deploy
{
    internal static class Program
    {
        private static string packages => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        private static string nugetPath => Path.Combine(packages, @"nuget.commandline\4.7.0\tools\NuGet.exe");
        private static string squirrelPath => Path.Combine(packages, @"ppy.squirrel.windows\1.8.0.3\tools\Squirrel.exe");
        private const string dotnet_path = @"C:\Program Files\dotnet\dotnet.exe";

        private const string staging_folder = "staging";
        private const string releases_folder = "releases";

        public static string GitHubAccessToken = ConfigurationManager.AppSettings["GitHubAccessToken"];
        public static string GitHubUsername = ConfigurationManager.AppSettings["GitHubUsername"];
        public static string GitHubRepoName = ConfigurationManager.AppSettings["GitHubRepoName"];
        public static string SolutionName = ConfigurationManager.AppSettings["SolutionName"];
        public static string ProjectName = ConfigurationManager.AppSettings["ProjectName"];
        public static string NuSpecName = ConfigurationManager.AppSettings["NuSpecName"];
        public static string TargetNames = ConfigurationManager.AppSettings["TargetName"];
        public static string PackageName = ConfigurationManager.AppSettings["PackageName"];
        public static string IconName = ConfigurationManager.AppSettings["IconName"];
        public static string CodeSigningCertificate = ConfigurationManager.AppSettings["CodeSigningCertificate"];

        public static string GitHubApiEndpoint => $"https://api.github.com/repos/{GitHubUsername}/{GitHubRepoName}/releases";
        public static string GitHubReleasePage => $"https://github.com/{GitHubUsername}/{GitHubRepoName}/releases";

        /// <summary>
        /// How many previous build deltas we want to keep when publishing.
        /// </summary>
        private const int keep_delta_count = 4;

        private static string codeSigningCmd =>
            string.IsNullOrEmpty(codeSigningPassword) ? "" : $"-n \"/a /f {codeSigningCertPath} /p {codeSigningPassword} /t http://timestamp.comodoca.com/authenticode\"";

        private static string homeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static string codeSigningCertPath => Path.Combine(homeDir, CodeSigningCertificate);
        private static string solutionPath;
        private static string stagingPath => Path.Combine(Environment.CurrentDirectory, staging_folder);
        private static string releasesPath => Path.Combine(Environment.CurrentDirectory, releases_folder);
        private static string iconPath => Path.Combine(solutionPath, ProjectName, IconName);

        private static string nupkgFilename(string ver) => $"{PackageName}.{ver}.nupkg";
        private static string nupkgDistroFilename(string ver) => $"{PackageName}-{ver}-full.nupkg";

        private static readonly Stopwatch sw = new Stopwatch();

        private static string codeSigningPassword;

        private static bool interactive;

        public static void Main(string[] args)
        {
            interactive = args.Length == 0;

            displayHeader();

            findSolutionPath();

            if (!Directory.Exists(releases_folder))
            {
                write("WARNING: No release directory found. Make sure you want this!", ConsoleColor.Yellow);
                Directory.CreateDirectory(releases_folder);
            }

            checkGitHubReleases();

            refreshDirectory(staging_folder);

            //increment build number until we have a unique one.
            string verBase = DateTime.Now.ToString("yyyy.Mdd.");
            int increment = 0;
            while (Directory.GetFiles(releases_folder, $"*{verBase}{increment}*").Any())
                increment++;

            string version = $"{verBase}{increment}";

            if (args.Length > 1 && !string.IsNullOrEmpty(args[1]))
                version = args[1];

            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"Ready to deploy {version}!");
            pauseIfInteractive();

            sw.Start();

            if (!string.IsNullOrEmpty(CodeSigningCertificate))
            {
                Console.Write("Enter code signing password: ");
                codeSigningPassword = args.Length > 0 ? args[0] : readLineMasked();
            }

            write("Updating AssemblyInfo...");
            updateAppveyorVersion(version);

            write("Running build process...");
            foreach (string targetName in TargetNames.Split(','))
            {
                runCommand(dotnet_path, $"publish -f netcoreapp2.1 -r win-x64 {ProjectName} -o {stagingPath} --configuration Release /p:Version={version}");

                // change subsystem of dotnet stub to WINDOWS (defaults to console; no way to change this yet https://github.com/dotnet/core-setup/issues/196)
                runCommand("tools/editbin.exe", $"/SUBSYSTEM:WINDOWS {stagingPath}\\osu!.exe");

                // add icon to dotnet stub
                runCommand("tools/rcedit-x64.exe", $"\"{stagingPath}\\osu!.exe\" --set-icon \"{iconPath}\"");
            }

            write("Creating NuGet deployment package...");
            runCommand(nugetPath, $"pack {NuSpecName} -Version {version} -Properties Configuration=Deploy -OutputDirectory {stagingPath} -BasePath {stagingPath}");

            //prune once before checking for files so we can avoid erroring on files which aren't even needed for this build.
            pruneReleases();

            checkReleaseFiles();

            write("Running squirrel build...");
            runCommand(squirrelPath, $"--releasify {stagingPath}\\{nupkgFilename(version)} -r {releasesPath} --setupIcon {iconPath} --icon {iconPath} {codeSigningCmd} --no-msi");

            //prune again to clean up before upload.
            pruneReleases();

            //rename setup to install.
            File.Copy(Path.Combine(releases_folder, "Setup.exe"), Path.Combine(releases_folder, "install.exe"), true);
            File.Delete(Path.Combine(releases_folder, "Setup.exe"));

            if (interactive)
                uploadBuild(version);

            write("Done!", ConsoleColor.White);
            pauseIfInteractive();
        }

        private static void displayHeader()
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine("  Please note that OSU! and PPY are registered trademarks and as such covered by trademark law.");
            Console.WriteLine("  Do not distribute builds of this project publicly that make use of these.");
            Console.ResetColor();
            Console.WriteLine();
        }

        /// <summary>
        /// Ensure we have all the files in the release directory which are expected to be there.
        /// This should have been accounted for in earlier steps, and just serves as a verification step.
        /// </summary>
        private static void checkReleaseFiles()
        {
            if (!canGitHub) return;

            var releaseLines = getReleaseLines();

            //ensure we have all files necessary
            foreach (var l in releaseLines)
                if (!File.Exists(Path.Combine(releases_folder, l.Filename)))
                    error($"Local file missing {l.Filename}");
        }

        private static IEnumerable<ReleaseLine> getReleaseLines() => File.ReadAllLines(Path.Combine(releases_folder, "RELEASES")).Select(l => new ReleaseLine(l));

        private static void pruneReleases()
        {
            if (!canGitHub) return;

            write("Pruning RELEASES...");

            var releaseLines = getReleaseLines().ToList();

            var fulls = releaseLines.Where(l => l.Filename.Contains("-full")).Reverse().Skip(1);

            //remove any FULL releases (except most recent)
            foreach (var l in fulls)
            {
                write($"- Removing old release {l.Filename}", ConsoleColor.Yellow);
                File.Delete(Path.Combine(releases_folder, l.Filename));
                releaseLines.Remove(l);
            }

            //remove excess deltas
            var deltas = releaseLines.Where(l => l.Filename.Contains("-delta")).ToArray();
            if (deltas.Length > keep_delta_count)
            {
                foreach (var l in deltas.Take(deltas.Length - keep_delta_count))
                {
                    write($"- Removing old delta {l.Filename}", ConsoleColor.Yellow);
                    File.Delete(Path.Combine(releases_folder, l.Filename));
                    releaseLines.Remove(l);
                }
            }

            var lines = new List<string>();
            releaseLines.ForEach(l => lines.Add(l.ToString()));
            File.WriteAllLines(Path.Combine(releases_folder, "RELEASES"), lines);
        }

        private static void uploadBuild(string version)
        {
            if (!canGitHub || string.IsNullOrEmpty(CodeSigningCertificate))
                return;

            write("Publishing to GitHub...");

            write($"- Creating release {version}...", ConsoleColor.Yellow);
            var req = new JsonWebRequest<GitHubRelease>($"{GitHubApiEndpoint}")
            {
                Method = HttpMethod.POST,
            };
            req.AddRaw(JsonConvert.SerializeObject(new GitHubRelease
            {
                Name = version,
                Draft = true,
                PreRelease = true
            }));
            req.AuthenticatedBlockingPerform();

            var assetUploadUrl = req.ResponseObject.UploadUrl.Replace("{?name,label}", "?name={0}");
            foreach (var a in Directory.GetFiles(releases_folder).Reverse()) //reverse to upload RELEASES first.
            {
                write($"- Adding asset {a}...", ConsoleColor.Yellow);
                var upload = new WebRequest(assetUploadUrl, Path.GetFileName(a))
                {
                    Method = HttpMethod.POST,
                    Timeout = 240000,
                    ContentType = "application/octet-stream",
                };

                upload.AddRaw(File.ReadAllBytes(a));
                upload.AuthenticatedBlockingPerform();
            }

            openGitHubReleasePage();
        }

        private static void openGitHubReleasePage() => Process.Start(GitHubReleasePage);

        private static bool canGitHub => !string.IsNullOrEmpty(GitHubAccessToken);

        private static void checkGitHubReleases()
        {
            if (!canGitHub) return;

            write("Checking GitHub releases...");
            var req = new JsonWebRequest<List<GitHubRelease>>($"{GitHubApiEndpoint}");
            req.AuthenticatedBlockingPerform();

            var lastRelease = req.ResponseObject.FirstOrDefault();

            if (lastRelease == null)
                return;

            if (lastRelease.Draft)
            {
                openGitHubReleasePage();
                error("There's a pending draft release! You probably don't want to push a build with this present.");
            }

            //there's a previous release for this project.
            var assetReq = new JsonWebRequest<List<GitHubObject>>($"{GitHubApiEndpoint}/{lastRelease.Id}/assets");
            assetReq.AuthenticatedBlockingPerform();
            var assets = assetReq.ResponseObject;

            //make sure our RELEASES file is the same as the last build on the server.
            var releaseAsset = assets.FirstOrDefault(a => a.Name == "RELEASES");

            //if we don't have a RELEASES asset then the previous release likely wasn't a Squirrel one.
            if (releaseAsset == null) return;

            write($"Last GitHub release was {lastRelease.Name}.");

            bool requireDownload = false;

            if (!File.Exists(Path.Combine(releases_folder, nupkgDistroFilename(lastRelease.Name))))
            {
                write("Last version's package not found locally.", ConsoleColor.Red);
                requireDownload = true;
            }
            else
            {
                var lastReleases = new RawFileWebRequest($"{GitHubApiEndpoint}/assets/{releaseAsset.Id}");
                lastReleases.AuthenticatedBlockingPerform();
                if (File.ReadAllText(Path.Combine(releases_folder, "RELEASES")) != lastReleases.ResponseString)
                {
                    write("Server's RELEASES differed from ours.", ConsoleColor.Red);
                    requireDownload = true;
                }
            }

            if (!requireDownload) return;

            write("Refreshing local releases directory...");
            refreshDirectory(releases_folder);

            foreach (var a in assets)
            {
                if (a.Name.EndsWith(".exe") || a.Name.EndsWith(".app.zip")) continue;

                write($"- Downloading {a.Name}...", ConsoleColor.Yellow);
                new FileWebRequest(Path.Combine(releases_folder, a.Name), $"{GitHubApiEndpoint}/assets/{a.Id}").AuthenticatedBlockingPerform();
            }
        }

        private static void refreshDirectory(string directory)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
            Directory.CreateDirectory(directory);
        }

        /// <summary>
        /// Find the base path of the active solution (git checkout location)
        /// </summary>
        private static void findSolutionPath()
        {
            string path = Path.GetDirectoryName(Environment.CommandLine.Replace("\"", "").Trim());

            if (string.IsNullOrEmpty(path))
                path = Environment.CurrentDirectory;

            while (true)
            {
                if (File.Exists(Path.Combine(path, $"{SolutionName}.sln")))
                    break;

                if (Directory.Exists(Path.Combine(path, "osu")) && File.Exists(Path.Combine(path, "osu", $"{SolutionName}.sln")))
                {
                    path = Path.Combine(path, "osu");
                    break;
                }

                path = path.Remove(path.LastIndexOf(Path.DirectorySeparatorChar));
            }

            path += Path.DirectorySeparatorChar;

            solutionPath = path;
        }

        private static bool runCommand(string command, string args)
        {
            var psi = new ProcessStartInfo(command, args)
            {
                WorkingDirectory = solutionPath,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process p = Process.Start(psi);
            if (p == null) return false;

            string output = p.StandardOutput.ReadToEnd();
            output += p.StandardError.ReadToEnd();

            if (p.ExitCode == 0) return true;

            write(output);
            error($"Command {command} {args} failed!");
            return false;
        }

        private static string readLineMasked()
        {
            var fg = Console.ForegroundColor;
            Console.ForegroundColor = Console.BackgroundColor;
            var ret = Console.ReadLine();
            Console.ForegroundColor = fg;

            return ret;
        }

        private static void error(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FATAL ERROR: {message}");

            pauseIfInteractive();
            Environment.Exit(-1);
        }

        private static void pauseIfInteractive()
        {
            if (interactive)
                Console.ReadLine();
            else
                Console.WriteLine();
        }

        private static bool updateAppveyorVersion(string version)
        {
            try
            {
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.AddScript($"Update-AppveyorBuild -Version \"{version}\"");
                    ps.Invoke();
                }

                return true;
            }
            catch
            {
                // we don't have appveyor and don't care
            }

            return false;
        }

        private static void write(string message, ConsoleColor col = ConsoleColor.Gray)
        {
            if (sw.ElapsedMilliseconds > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write(sw.ElapsedMilliseconds.ToString().PadRight(8));
            }

            Console.ForegroundColor = col;
            Console.WriteLine(message);
        }

        public static void AuthenticatedBlockingPerform(this WebRequest r)
        {
            r.AddHeader("Authorization", $"token {GitHubAccessToken}");
            r.Perform();
        }
    }

    internal class RawFileWebRequest : WebRequest
    {
        public RawFileWebRequest(string url)
            : base(url)
        {
        }

        protected override string Accept => "application/octet-stream";
    }

    internal class ReleaseLine
    {
        public string Hash;
        public string Filename;
        public int Filesize;

        public ReleaseLine(string line)
        {
            var split = line.Split(' ');
            Hash = split[0];
            Filename = split[1];
            Filesize = int.Parse(split[2]);
        }

        public override string ToString() => $"{Hash} {Filename} {Filesize}";
    }
}