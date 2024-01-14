// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Net.Http;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using osu.Framework;
using osu.Framework.Extensions;
using osu.Framework.IO.Network;

namespace osu.Desktop.Deploy
{
    internal static class Program
    {
        private const string staging_folder = "staging";
        private const string templates_folder = "templates";
        private const string releases_folder = "releases";

        /// <summary>
        /// How many previous build deltas we want to keep when publishing.
        /// </summary>
        private const int keep_delta_count = 4;

        public static string? GitHubAccessToken = ConfigurationManager.AppSettings["GitHubAccessToken"];
        public static bool GitHubUpload = bool.Parse(ConfigurationManager.AppSettings["GitHubUpload"] ?? "false");
        public static string? GitHubUsername = ConfigurationManager.AppSettings["GitHubUsername"];
        public static string? GitHubRepoName = ConfigurationManager.AppSettings["GitHubRepoName"];

        public static string? SolutionName = ConfigurationManager.AppSettings["SolutionName"];
        public static string? ProjectName = ConfigurationManager.AppSettings["ProjectName"];
        public static string? PackageName = ConfigurationManager.AppSettings["PackageName"];
        public static string? IconName = ConfigurationManager.AppSettings["IconName"];

        public static string? AndroidCodeSigningCertPath = ConfigurationManager.AppSettings["AndroidCodeSigningCertPath"];
        public static string? WindowsCodeSigningCertPath = ConfigurationManager.AppSettings["WindowsCodeSigningCertPath"];
        public static string? AppleCodeSignCertName = ConfigurationManager.AppSettings["AppleCodeSignCertName"];
        public static string? AppleInstallSignCertName = ConfigurationManager.AppSettings["AppleInstallSignCertName"];
        public static string? AppleNotaryProfileName = ConfigurationManager.AppSettings["AppleNotaryProfileName"];

        public static string GitHubApiEndpoint => $"https://api.github.com/repos/{GitHubUsername}/{GitHubRepoName}/releases";
        public static string GitHubRepoUrl => $"https://github.com/{GitHubUsername}/{GitHubRepoName}";

        private static string? solutionPath;

        private static string stagingPath => Path.Combine(Environment.CurrentDirectory, staging_folder);
        private static string templatesPath => Path.Combine(Environment.CurrentDirectory, templates_folder);
        private static string releasesPath => Path.Combine(Environment.CurrentDirectory, releases_folder);

        private static string vpkPath = @"C:\Source\velopack\build\Debug\net8.0\vpk.exe";

        private static string iconPath
        {
            get
            {
                Debug.Assert(solutionPath != null);
                Debug.Assert(ProjectName != null);
                Debug.Assert(IconName != null);

                return Path.Combine(solutionPath, ProjectName, IconName);
            }
        }

        private static string splashImagePath
        {
            get
            {
                Debug.Assert(solutionPath != null);
                return Path.Combine(solutionPath, "assets\\lazer-nuget.png");
            }
        }

        private static bool interactive;

        /// <summary>
        /// args[0]: code signing passphrase
        /// args[1]: version
        /// args[2]: platform
        /// args[3]: arch
        /// </summary>
        public static void Main(string[] args)
        {
            interactive = args.Length == 0;
            displayHeader();

            findSolutionPath();

            if (!Directory.Exists(releases_folder))
            {
                Log.write("WARNING: No release directory found. Make sure you want this!", ConsoleColor.Yellow);
                Directory.CreateDirectory(releases_folder);
            }

            string version = DateTime.Now.ToString("yyyy.Mdd.0");

            GithubReleaseHelper gh = null;
            if (canGitHub)
            {
                gh = new GithubReleaseHelper(GitHubApiEndpoint, GitHubAccessToken!);
                version = gh.GetNextGitHubReleaseVersion();
            }

            var targetPlatform = RuntimeInfo.OS;

            if (args.Length > 1 && !string.IsNullOrEmpty(args[1]))
                version = args[1];
            if (args.Length > 2 && !string.IsNullOrEmpty(args[2]))
                Enum.TryParse(args[2], true, out targetPlatform);

            Console.ResetColor();
            Console.WriteLine($"Signing Certificate:   {WindowsCodeSigningCertPath}");
            Console.WriteLine($"Upload to GitHub:      {GitHubUpload}");
            Console.WriteLine();
            Console.Write($"Ready to deploy version {version} on platform {targetPlatform}!");

            pauseIfInteractive();

            refreshDirectory(staging_folder);
            updateAppveyorVersion(version);

            Debug.Assert(solutionPath != null);

            Log.write("Running build process...");

            if (targetPlatform == RuntimeInfo.Platform.Windows || targetPlatform == RuntimeInfo.Platform.Linux || targetPlatform == RuntimeInfo.Platform.macOS)
            {


                var os = targetPlatform switch
                {
                    RuntimeInfo.Platform.Windows => "win",
                    RuntimeInfo.Platform.macOS => "mac",
                    RuntimeInfo.Platform.Linux => "linux",
                    _ => throw new ArgumentOutOfRangeException(nameof(targetPlatform), targetPlatform, null),
                };

                var arch = "x64";
                if (targetPlatform == RuntimeInfo.Platform.macOS)
                {
                    string targetArch = "";
                    if (args.Length > 3)
                    {
                        targetArch = args[3];
                    }
                    else if (interactive)
                    {
                        Console.Write("Build for which architecture? [x64/arm64]: ");
                        targetArch = Console.ReadLine() ?? string.Empty;
                    }
                    if (targetArch != "x64" && targetArch != "arm64")
                        error($"Invalid Architecture: {targetArch}");
                    arch = targetArch;
                }

                var rid = $"{os}-{arch}";
                var channel = rid == "win-x64" ? "win" : rid;

                // download the latest release so we can build a new delta
                if (canGitHub)
                {
                    runCommand(vpkPath, $"download github --repoUrl https://github.com/{GitHubUsername}/{GitHubRepoName} --token {GitHubAccessToken} -o=\"{releasesPath}\" --channel={channel}", throwIfNonZero: false);
                }

                string publishDir = stagingPath;
                string extraCmd = "";

                // on linux only, we use a custom AppDir template. Velopack does have a default one, but it's missing some of our customisations.
                if (targetPlatform == RuntimeInfo.Platform.Linux)
                {
                    const string app_dir = "osu!.AppDir";
                    string stagingTarget = Path.Combine(stagingPath, app_dir);
                    if (Directory.Exists(stagingTarget))
                        Directory.Delete(stagingTarget, true);
                    Directory.CreateDirectory(stagingTarget);
                    foreach (var file in Directory.GetFiles(Path.Combine(templatesPath, app_dir)))
                        new FileInfo(file).CopyTo(Path.Combine(stagingTarget, Path.GetFileName(file)));
                    // mark AppRun as executable, as zip does not contains executable information
                    runCommand("chmod", $"+x {stagingTarget}/AppRun");
                    publishDir = $"{stagingTarget}/usr/bin/";
                    extraCmd += $" --appDir=\"{stagingTarget}\"";
                }

                runCommand("dotnet", $"publish -f net6.0 -r {rid} {ProjectName} -o \"{publishDir}\" --self-contained --configuration Release /p:Version={version}");

                if (targetPlatform == RuntimeInfo.Platform.Windows)
                {
                    // add icon to dotnet stub
                    runCommand("tools/rcedit-x64.exe", $"\"{publishDir}/osu!.exe\" --set-icon \"{iconPath}\"");
                }

                if (targetPlatform == RuntimeInfo.Platform.Windows)
                {
                    if (!string.IsNullOrEmpty(WindowsCodeSigningCertPath))
                    {
                        string? codeSigningPassword;
                        if (args.Length > 0)
                        {
                            codeSigningPassword = args[0];
                        }
                        else
                        {
                            Console.Write("Enter code signing password: ");
                            codeSigningPassword = readLineMasked();
                        }
                        extraCmd += string.IsNullOrEmpty(codeSigningPassword)
                            ? ""
                            : $" --signParams=\"/td sha256 /fd sha256 /f {WindowsCodeSigningCertPath} /p {codeSigningPassword} /tr http://timestamp.comodoca.com\"";
                    }
                    extraCmd += $" --splashImage=\"{splashImagePath}\"";
                }
                else if (targetPlatform == RuntimeInfo.Platform.macOS)
                {
                    if (!string.IsNullOrEmpty(AppleCodeSignCertName))
                        extraCmd += $" --signAppIdentity=\"{AppleCodeSignCertName}\"";
                    if (!string.IsNullOrEmpty(AppleInstallSignCertName))
                        extraCmd += $" --signInstallIdentity=\"{AppleInstallSignCertName}\"";
                    if (!string.IsNullOrEmpty(AppleNotaryProfileName))
                        extraCmd += $" --notaryProfile=\"{AppleNotaryProfileName}\"";
                }

                var exeName = targetPlatform == RuntimeInfo.Platform.Windows ? "osu!.exe" : "osu!";
                if (targetPlatform == RuntimeInfo.Platform.Windows || targetPlatform == RuntimeInfo.Platform.macOS)
                {
                    extraCmd += $" -p=\"{stagingPath}\" --icon=\"{iconPath}\"";
                }

                runCommand(vpkPath, $"pack -u {PackageName} -v {version} -r {rid} -o=\"{releasesPath}\" -e=\"{exeName}\" {extraCmd} --channel={channel}");

                if (canGitHub && GitHubUpload)
                {
                    runCommand(vpkPath, $"upload github --repoUrl https://github.com/{GitHubUsername}/{GitHubRepoName} --token {GitHubAccessToken} -o=\"{releasesPath}\" --tag {version} --releaseName {version} --merge --channel={channel}");
                }
            }
            else if (targetPlatform == RuntimeInfo.Platform.Android)
            {
                string codeSigningArguments = string.Empty;

                if (!string.IsNullOrEmpty(AndroidCodeSigningCertPath))
                {
                    string? codeSigningPassword;

                    if (args.Length > 0)
                    {
                        codeSigningPassword = args[0];
                    }
                    else
                    {
                        Console.Write("Enter code signing password: ");
                        codeSigningPassword = readLineMasked();
                    }

                    codeSigningArguments += " -p:AndroidKeyStore=true"
                                            + $" -p:AndroidSigningKeyStore={AndroidCodeSigningCertPath}"
                                            + $" -p:AndroidSigningKeyAlias={Path.GetFileNameWithoutExtension(AndroidCodeSigningCertPath)}"
                                            + $" -p:AndroidSigningKeyPass={codeSigningPassword}"
                                            + $" -p:AndroidSigningStorePass={codeSigningPassword}";
                }

                string[] versionParts = version.Split('.');
                string appVersion = string.Join
                (
                    separator: string.Empty,
                    versionParts[0].PadLeft(4, '0'),
                    versionParts[1].PadLeft(4, '0'),
                    versionParts[2].PadLeft(1, '0')
                );

                runCommand("dotnet", "publish"
                                     + " -f net6.0-android"
                                     + " -r android-arm64"
                                     + " -c Release"
                                     + $" -o {stagingPath}"
                                     + $" -p:Version={version}"
                                     + $" -p:ApplicationVersion={appVersion}"
                                     + codeSigningArguments
                                     + " --self-contained"
                                     + " osu.Android/osu.Android.csproj");

                // copy update information
                File.Move(Path.Combine(stagingPath, "sh.ppy.osulazer-Signed.apk"), Path.Combine(releasesPath, "sh.ppy.osulazer.apk"), true);
                if (canGitHub && GitHubUpload)
                {
                    gh!.UploadBuild(version, releases_folder);
                }
            }
            else if (targetPlatform == RuntimeInfo.Platform.iOS)
            {
                runCommand("dotnet", "publish"
                                        + " -f net6.0-ios"
                                        + " -r ios-arm64"
                                        + " -c Release"
                                        + $" -o {stagingPath}"
                                        + $" -p:Version={version}"
                                        + " -p:ApplicationDisplayVersion=1.0"
                                        + " --self-contained"
                                        + " osu.iOS/osu.iOS.csproj");

                // copy update information
                File.Move(Path.Combine(stagingPath, "osu.iOS.ipa"), Path.Combine(releasesPath, "osu.iOS.ipa"), true);
                if (canGitHub && GitHubUpload)
                {
                    gh!.UploadBuild(version, releases_folder);
                }
            }
            else
            {
                throw new PlatformNotSupportedException("Unsupported platform: " + targetPlatform);
            }

            if (canGitHub && GitHubUpload)
            {
                openGitHubReleasePage();
            }

            Log.write("Done!");
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

        private static void openGitHubReleasePage()
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://github.com/{GitHubUsername}/{GitHubRepoName}/releases",
                UseShellExecute = true //see https://github.com/dotnet/corefx/issues/10361
            });
        }

        private static bool canGitHub => !string.IsNullOrEmpty(GitHubAccessToken);

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
            string? path = Path.GetDirectoryName(Environment.CommandLine.Replace("\"", "").Trim());

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

        private static bool runCommand(string command, string args, bool useSolutionPath = true, Dictionary<string, string>? environmentVariables = null, bool throwIfNonZero = true)
        {
            Log.write($"Running {command} {args}...");

            var psi = new ProcessStartInfo(command, args)
            {
                WorkingDirectory = useSolutionPath ? solutionPath : Environment.CurrentDirectory,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (environmentVariables != null)
            {
                foreach (var pair in environmentVariables)
                    psi.EnvironmentVariables.Add(pair.Key, pair.Value);
            }

            Process? p = Process.Start(psi);
            if (p == null) return false;

            string output = p.StandardOutput.ReadToEnd();
            output += p.StandardError.ReadToEnd();

            p.WaitForExit();
            if (p.ExitCode == 0) return true;
            Log.write(output);

            if (throwIfNonZero)
            {
                error($"Command {command} {args} failed!");
                throw new Exception($"Command {command} {args} failed!");
            }
            return false;
        }

        private static string? readLineMasked()
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
            Console.ResetColor();

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

        public override string ToString()
        {
            return $"{Hash} {Filename} {Filesize}";
        }
    }
}
