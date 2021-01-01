using System.Linq;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution;
    [GitVersion(NoFetch = true)] readonly GitVersion GitVersion;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath OutputDirectory => RootDirectory / "output";

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(OutputDirectory);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(_ => _
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(_ => _
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .EnableNoRestore());
        });

    AbsolutePath PackageDirectory => OutputDirectory / "packages";

    Target Pack => _ => _
        .DependsOn(Compile)
        //.Produces("*.nuspec")
        .Executes(() =>
        {
            var targetDir = SourceDirectory.GlobDirectories($"GitExtensions.GitLabCommitHintPlugin/bin/{Configuration}").First();

            NuGetTasks.NuGetPack(_ => _
                .SetTargetPath(SourceDirectory / "GitExtensions.GitLabCommitHintPlugin" / "GitExtensions.GitLabCommitHintPlugin.nuspec")
                .SetBasePath(RootDirectory)
                .SetConfiguration(Configuration)
                .SetVersion(GitVersion.NuGetVersionV2)
                .SetOutputDirectory(PackageDirectory)
                .SetProperty("targetDir", $"{targetDir}\\")
                .SetProperty("id", "GitExtensions.GitLabCommitHintPlugin"));
        });

    Target Deploy => _ => _
        .Executes(() =>
        {
            var files = OutputDirectory.GlobFiles("packages/*.nupkg");
            foreach (var file in files)
            {
                Logger.Info($"Push {file}");

                var output = DotNetNuGetPush(_ => _
                    .SetTargetPath(file)
                    .SetSource("GitLab"));

                Logger.Log(LogLevel.Normal, string.Join("", output.Select(c => c.Text)));
            }
        });
}
