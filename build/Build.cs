using System.Linq;
using Nuke.Common;
using Nuke.Common.CI.AzurePipelines;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tooling.ProcessTasks;
using Project = Nuke.Common.ProjectModel.Project;

[AzurePipelines(
    AzurePipelinesImage.WindowsLatest, 
    InvokedTargets =
    [
        nameof(Publish)
    ],
    ImportSecrets =
    [
        nameof(NuGetApiKey),
    ], FetchDepth = 0, 
    AutoGenerate = false)]
class Build : NukeBuild
{
    [Parameter] [Secret] readonly string NuGetApiKey;

    [Solution] readonly Solution Solution;

    [GitVersion] readonly GitVersion GitVersion;

    [GitRepository] readonly GitRepository Repository;

    [Parameter]
    readonly Configuration Configuration = IsServerBuild
        ? Configuration.Release
        : Configuration.Debug;

    AbsolutePath OutputDirectory => RootDirectory / "output";

    Target Clean => d => d
        .Executes(() =>
        {
            OutputDirectory.CreateOrCleanDirectory();
            var absolutePaths = RootDirectory.GlobDirectories("**/bin", "**/obj").Where(a => !((string) a).Contains("build")).ToList();
            Log.Information("Deleting {Dirs}", absolutePaths);
            absolutePaths.DeleteDirectories();
        });

    Target Pack => d => d
        .DependsOn(Clean)
        .Executes(() =>
        {
            var packableProjects = Solution.AllProjects.Where(x => x.GetProperty<bool>("IsPackable")).ToList();

            packableProjects.ForEach(project =>
            {
                Log.Information("Restoring workloads of {Input}", project);
                RestoreProjectWorkload(project);
            });

            DotNetPack(settings => settings
                .SetConfiguration(Configuration)
                .SetVersion(GitVersion.NuGetVersion)
                .SetOutputDirectory(OutputDirectory)
                .CombineWith(packableProjects, (packSettings, project) =>
                    packSettings.SetProject(project)));
        });

    Target Publish => d => d
        .DependsOn(Pack)
        .Requires(() => NuGetApiKey)
        .OnlyWhenStatic(() => Repository.IsOnMainOrMasterBranch())
        .Executes(() =>
        {
            Log.Information("Commit = {Value}", Repository.Commit);
            Log.Information("Branch = {Value}", Repository.Branch);
            Log.Information("Tags = {Value}", Repository.Tags);

            Log.Information("main branch = {Value}", Repository.IsOnMainBranch());
            Log.Information("main/master branch = {Value}", Repository.IsOnMainOrMasterBranch());
            Log.Information("release/* branch = {Value}", Repository.IsOnReleaseBranch());
            Log.Information("hotfix/* branch = {Value}", Repository.IsOnHotfixBranch());

            Log.Information("Https URL = {Value}", Repository.HttpsUrl);
            Log.Information("SSH URL = {Value}", Repository.SshUrl);

            DotNetNuGetPush(settings => settings
                    .SetSource("https://api.nuget.org/v3/index.json")
                    .SetApiKey(NuGetApiKey)
                    .CombineWith(
                        OutputDirectory.GlobFiles("*.nupkg").NotEmpty(), (s, v) => s.SetTargetPath(v)),
                degreeOfParallelism: 5, completeOnFailure: true);
        });

    public static int Main() => Execute<Build>(x => x.Publish);

    void RestoreProjectWorkload(Project project) => StartShell($@"dotnet workload restore --project {project.Path}").AssertZeroExitCode();
}
