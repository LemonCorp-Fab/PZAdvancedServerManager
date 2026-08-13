using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Publishing;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class WorkshopPublicationPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pzasm-publication-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset RemoteUpdatedAt = DateTimeOffset.FromUnixTimeSeconds(1_780_000_000);

    [Fact]
    public void ExactLocalAndRemoteStateProducesVerifiedNoOp()
    {
        var (project, snapshot, remote) = ConfirmedState();

        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remote, false);

        Assert.True(plan.IsNoOp);
        Assert.True(plan.RemoteVerified);
        Assert.False(plan.RequiresServerRestart);
        Assert.False(plan.IncludeContent);
    }

    [Fact]
    public void UnavailableRemoteStateNeverProducesNoOp()
    {
        var (project, snapshot, _) = ConfirmedState();

        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, null, false);

        Assert.False(plan.IsNoOp);
        Assert.Equal(WorkshopPublicationMode.Full, plan.Mode);
        Assert.True(plan.IncludeContent);
        Assert.True(plan.IncludeMetadata);
        Assert.True(plan.IncludePreview);
    }

    [Fact]
    public void ForceAlwaysSubmitsCompleteWorkshopState()
    {
        var (project, snapshot, remote) = ConfirmedState();

        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remote, true);

        Assert.Equal(WorkshopPublicationMode.Full, plan.Mode);
        Assert.True(plan.Force);
        Assert.True(plan.IncludeContent);
        Assert.True(plan.IncludeMetadata);
        Assert.True(plan.IncludePreview);
        Assert.True(plan.RequiresServerRestart);
    }

    [Fact]
    public void ChangedContentOnlyProducesContentUpdate()
    {
        var (project, snapshot, remote) = ConfirmedState();
        snapshot = snapshot with { ContentFingerprint = "content-v2" };

        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remote, false);

        Assert.Equal(WorkshopPublicationMode.Content, plan.Mode);
        Assert.True(plan.IncludeContent);
        Assert.False(plan.IncludeMetadata);
        Assert.False(plan.IncludePreview);
        Assert.True(plan.RequiresServerRestart);
    }

    [Fact]
    public void ChangedMetadataOnlyProducesMetadataUpdateWithoutRestart()
    {
        var (project, snapshot, remote) = ConfirmedState();
        snapshot = snapshot with { MetadataFingerprint = "metadata-v2", Title = "Updated title" };

        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remote, false);

        Assert.Equal(WorkshopPublicationMode.Metadata, plan.Mode);
        Assert.False(plan.IncludeContent);
        Assert.True(plan.IncludeMetadata);
        Assert.False(plan.IncludePreview);
        Assert.False(plan.RequiresServerRestart);
    }

    [Fact]
    public void ChangedPreviewOnlyProducesPreviewUpdateWithoutRestart()
    {
        var (project, snapshot, remote) = ConfirmedState();
        snapshot = snapshot with { PreviewFingerprint = "preview-v2" };

        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remote, false);

        Assert.Equal(WorkshopPublicationMode.Preview, plan.Mode);
        Assert.False(plan.IncludeContent);
        Assert.False(plan.IncludeMetadata);
        Assert.True(plan.IncludePreview);
        Assert.False(plan.RequiresServerRestart);
    }

    [Fact]
    public void ChangedRemoteManifestPreventsNoOpAndRestoresContent()
    {
        var (project, snapshot, remote) = ConfirmedState();
        remote = remote with { ContentHandle = "remote-edited" };

        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remote, false);

        Assert.False(plan.IsNoOp);
        Assert.True(plan.RemoteDiverged);
        Assert.True(plan.IncludeContent);
        Assert.True(plan.RequiresServerRestart);
    }

    [Fact]
    public void ChangedRemoteTimestampPreventsNoOp()
    {
        var (project, snapshot, remote) = ConfirmedState();
        remote = remote with { UpdatedAt = RemoteUpdatedAt.AddMinutes(1) };

        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remote, false);

        Assert.False(plan.IsNoOp);
        Assert.True(plan.IncludeMetadata);
    }

    [Fact]
    public void DifferentialVdfOmitsUnchangedContentAndPreview()
    {
        var (project, snapshot, remote) = ConfirmedState();
        snapshot = snapshot with { MetadataFingerprint = "metadata-v2", Title = "Updated title" };
        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remote, false);
        var build = CreateBuild();

        var vdf = WorkshopPublicationPlanner.GenerateVdf(project, build, plan);
        var vdfPath = Path.Combine(build.BuildRoot, "metadata-only.vdf");
        File.WriteAllText(vdfPath, vdf);

        Assert.DoesNotContain("contentfolder", vdf);
        Assert.DoesNotContain("previewfile", vdf);
        Assert.Contains("\"title\"", vdf);
        Assert.Contains("Updated title", vdf);
        SteamCmdService.ValidatePublishPayload(build, vdfPath, requireContent: false, requirePreview: false);
    }

    [Fact]
    public void FullVdfMapsExactBuildPaths()
    {
        var (project, snapshot, remote) = ConfirmedState();
        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remote, true);
        var build = CreateBuild();
        var vdfPath = Path.Combine(build.BuildRoot, "full.vdf");
        File.WriteAllText(vdfPath, WorkshopPublicationPlanner.GenerateVdf(project, build, plan));

        SteamCmdService.ValidatePublishPayload(build, vdfPath, requireContent: true, requirePreview: true);
    }

    [Fact]
    public void OversizedDescriptionIsRejectedBeforeSteamCmd()
    {
        var build = CreateBuild();
        var vdfPath = Path.Combine(build.BuildRoot, "oversized-description.vdf");
        File.WriteAllText(vdfPath, $"\"workshopitem\"\n{{\n\"title\" \"Valid\"\n\"description\" \"{new string('x', PzasmConstants.SteamWorkshopDescriptionMaximumUtf8Bytes)}\"\n}}");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SteamCmdService.ValidatePublishPayload(build, vdfPath, requireContent: false, requirePreview: false));

        Assert.Contains("avant le calcul du manifeste", exception.Message);
    }

    [Fact]
    public void WorkshopBuildLogReportsConcreteManifestProgress()
    {
        var files = SteamCmdService.ParseWorkshopBuildProgress("[time]: Found 76719 files (10096 MB) for depot 108600");
        var delta = SteamCmdService.ParseWorkshopBuildProgress("[time]: Found 46450 new chunks ( 60 used previously ) for depot 108600, took 2 minutes, 14 seconds");
        var upload = SteamCmdService.ParseWorkshopBuildProgress("[time]: Uploading 4 out of 4 missing chunks.");

        Assert.Equal("manifest-scan", files?.Phase);
        Assert.Contains("76", files?.Message);
        Assert.Contains("fichiers", files?.Message);
        Assert.Equal("manifest-delta", delta?.Phase);
        Assert.Contains("2 minutes, 14 seconds", delta?.Message);
        Assert.Equal("workshop-upload", upload?.Phase);
        Assert.Equal(4, upload?.Current);
        Assert.Equal(4, upload?.Total);
    }

    [Fact]
    public void UnverifiedConfirmationCannotEnableFutureNoOp()
    {
        var (project, snapshot, remote) = ConfirmedState();
        WorkshopPublicationPlanner.ApplyConfirmedState(project, snapshot, null, "manifest-from-steamcmd");

        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remote, false);

        Assert.Null(project.Publication.RemoteVerifiedAt);
        Assert.Equal("manifest-from-steamcmd", project.Publication.RemoteContentHandle);
        Assert.False(plan.IsNoOp);
    }

    [Fact]
    public void StaleRemoteManifestCannotConfirmChangedContent()
    {
        var (project, snapshot, remoteBefore) = ConfirmedState();
        snapshot = snapshot with { ContentFingerprint = "content-v2" };
        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remoteBefore, false);
        var submittedAt = RemoteUpdatedAt.AddMinutes(1);
        var staleRemote = remoteBefore with { UpdatedAt = submittedAt.AddSeconds(1) };

        var confirmed = WorkshopPublicationPlanner.IsRemoteConfirmation(
            project,
            plan,
            staleRemote,
            submittedAt,
            "manifest-v2");

        Assert.False(confirmed);
    }

    [Fact]
    public void NewRemoteManifestConfirmsChangedContent()
    {
        var (project, snapshot, remoteBefore) = ConfirmedState();
        snapshot = snapshot with { ContentFingerprint = "content-v2" };
        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remoteBefore, false);
        var submittedAt = RemoteUpdatedAt.AddMinutes(1);
        var remoteAfter = remoteBefore with
        {
            ContentHandle = "manifest-v2",
            FileSize = 2048,
            UpdatedAt = submittedAt.AddSeconds(1)
        };

        var confirmed = WorkshopPublicationPlanner.IsRemoteConfirmation(
            project,
            plan,
            remoteAfter,
            submittedAt,
            "manifest-v2");

        Assert.True(confirmed);
    }

    [Fact]
    public void LinuxCommitCanBeConfirmedByAdvancedRemoteTimestampWhenSteamOmitsManifestHandle()
    {
        var (project, snapshot, remoteBefore) = ConfirmedState();
        snapshot = snapshot with { ContentFingerprint = "content-v2" };
        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remoteBefore, false);
        var submittedAt = RemoteUpdatedAt.AddMinutes(1);
        var remoteAfter = remoteBefore with { UpdatedAt = submittedAt.AddSeconds(1), FileSize = 4096 };

        var confirmed = WorkshopPublicationPlanner.IsRemoteConfirmation(
            project,
            plan,
            remoteAfter,
            submittedAt,
            allowTimestampOnlyContentConfirmation: true);

        Assert.True(confirmed);
    }

    [Fact]
    public void ReusedManifestFromCurrentSteamCmdRunConfirmsIdempotentContentSubmission()
    {
        var (project, snapshot, remoteBefore) = ConfirmedState();
        snapshot = snapshot with { ContentFingerprint = "content-v2" };
        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remoteBefore, false);
        var submittedAt = RemoteUpdatedAt.AddMinutes(1);

        var confirmed = WorkshopPublicationPlanner.IsRemoteConfirmation(
            project,
            plan,
            remoteBefore,
            submittedAt,
            publishedContentHandle: remoteBefore.ContentHandle,
            allowTimestampOnlyContentConfirmation: true,
            submittedManifestVerified: true);

        Assert.True(confirmed);
    }

    [Fact]
    public void StaleManifestFileCannotProveCurrentSubmission()
    {
        var steamRoot = Path.Combine(_root, "steamcmd");
        var executable = Path.Combine(steamRoot, "linux32", "steamcmd");
        var statePath = Path.Combine(steamRoot, "workshopbuilds", $"depot_build_{PzasmConstants.ProjectZomboidSteamAppId}.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(executable, string.Empty);
        File.WriteAllText(statePath, "\"depotbuild\" { \"manifest\" \"123456789\" }");
        var submittedAt = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(statePath, submittedAt.AddMinutes(-1).UtcDateTime);

        Assert.Contains(steamRoot, SteamCmdService.GetSteamCmdDataRoots(executable));
        Assert.Empty(SteamCmdService.ReadPublishedContentHandle(executable, submittedAt));

        File.SetLastWriteTimeUtc(statePath, submittedAt.UtcDateTime);
        Assert.Equal("123456789", SteamCmdService.ReadPublishedContentHandle(executable, submittedAt));
    }

    [Fact]
    public void ForceRequiresObservableRemoteChange()
    {
        var (project, snapshot, remoteBefore) = ConfirmedState();
        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remoteBefore, true);
        var submittedAt = RemoteUpdatedAt.AddMinutes(1);

        Assert.False(WorkshopPublicationPlanner.IsRemoteConfirmation(project, plan, remoteBefore, submittedAt, "manifest-v1"));
        Assert.True(WorkshopPublicationPlanner.IsRemoteConfirmation(
            project,
            plan,
            remoteBefore with { UpdatedAt = submittedAt.AddSeconds(1) },
            submittedAt,
            "manifest-v1"));
    }

    [Fact]
    public void SteamCmdExitZeroWithoutUploadConfirmationIsRejected()
    {
        var processResult = new SteamCmdResult(0, "Steam Console Client exited", string.Empty);

        var result = SteamCmdService.ValidateWorkshopSubmissionResult(processResult, 123456789, string.Empty);

        Assert.False(result.Success);
        Assert.Contains("sans confirmation explicite", result.StandardError);
    }

    [Fact]
    public void ExplicitWorkshopFailureOverridesExitZero()
    {
        var processResult = new SteamCmdResult(0, "ERROR! Failed to update workshop item (Failure)", string.Empty);

        var result = SteamCmdService.ValidateWorkshopSubmissionResult(
            processResult,
            123456789,
            "Upload workshop item 123456789 failed (Failure)");

        Assert.False(result.Success);
        Assert.Contains("échec", result.StandardError);
    }

    [Fact]
    public void InvalidParameterCompletionIsExplainedEvenWhenProcessFailed()
    {
        var processResult = new SteamCmdResult(1, "ERROR! Failed to update workshop item (Invalid Parameter)", string.Empty);

        var result = SteamCmdService.ValidateWorkshopSubmissionResult(
            processResult,
            123456789,
            "Upload finished for workshop item 123456789 : Invalid Parameter");

        Assert.False(result.Success);
        Assert.Contains("limites UTF-8", result.StandardError);
    }

    [Fact]
    public void ExactWorkshopUploadCompletionAcceptsExitZero()
    {
        var processResult = new SteamCmdResult(0, "Steam Console Client exited", string.Empty);

        var result = SteamCmdService.ValidateWorkshopSubmissionResult(
            processResult,
            123456789,
            "Upload finished for workshop item 123456789 : OK");

        Assert.True(result.Success);
    }

    [Fact]
    public void LinuxCommitSuccessRequiresRemoteProofInsteadOfImmediateRejection()
    {
        const string output = "Uploading content... Uploading preview image... Committing update... Success. Unloading Steam API...OK";
        var processResult = new SteamCmdResult(0, output, string.Empty);

        var result = SteamCmdService.ValidateWorkshopSubmissionResult(processResult, 123456789, string.Empty);

        Assert.True(result.Success);
        Assert.True(SteamCmdService.RequiresRemoteProof(processResult, 123456789, string.Empty));
    }

    [Fact]
    public void ExplicitWorkshopCompletionDoesNotRequireAdditionalProof()
    {
        var processResult = new SteamCmdResult(0, "Steam Console Client exited", string.Empty);
        const string activity = "Upload finished for workshop item 123456789 : OK";

        Assert.False(SteamCmdService.RequiresRemoteProof(processResult, 123456789, activity));
    }

    [Fact]
    public void LinuxCommitSequenceCannotOverrideExplicitFailure()
    {
        const string output = "Committing update... Success. Unloading Steam API...OK ERROR! Failed to update workshop item (Failure)";
        var processResult = new SteamCmdResult(0, output, string.Empty);

        var result = SteamCmdService.ValidateWorkshopSubmissionResult(processResult, 123456789, string.Empty);

        Assert.False(result.Success);
        Assert.False(SteamCmdService.RequiresRemoteProof(processResult, 123456789, string.Empty));
    }

    [Fact]
    public void TerminalControlSequencesAreRemovedFromSteamCmdOutput()
    {
        const string output = "\u001b[0mUploading content...\u001b[32mSuccess.\u001b[0m";

        var cleaned = SteamCmdService.StripTerminalControlSequences(output);

        Assert.Equal("Uploading content...Success.", cleaned);
    }

    private PackageBuildResult CreateBuild()
    {
        var buildRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var contents = Path.Combine(buildRoot, "Contents");
        Directory.CreateDirectory(contents);
        File.WriteAllText(Path.Combine(contents, "payload.txt"), "payload");
        var preview = Path.Combine(buildRoot, "preview.png");
        File.WriteAllText(preview, new string('p', 32));
        return new PackageBuildResult
        {
            BuildRoot = buildRoot,
            WorkshopContentRoot = contents,
            WorkshopDescriptorPath = Path.Combine(buildRoot, "workshop.txt"),
            WorkshopPreviewPath = preview,
            SteamCmdVdfPath = Path.Combine(buildRoot, "steamcmd-item.vdf"),
            LockFilePath = Path.Combine(buildRoot, "pack.lock.json"),
            ServerConfigSnippetPath = Path.Combine(buildRoot, "server-config.txt"),
            Validation = new PackageValidationResult(),
            ContentFingerprint = "content-v1"
        };
    }

    private static (PackageProject Project, WorkshopPublicationSnapshot Snapshot, WorkshopRemoteState Remote) ConfirmedState()
    {
        var project = new PackageProject
        {
            PublishedWorkshopId = 123456789,
            Publication = new WorkshopPublicationState
            {
                WorkshopId = 123456789,
                ContentFingerprint = "content-v1",
                MetadataFingerprint = "metadata-v1",
                PreviewFingerprint = "preview-v1",
                RemoteContentHandle = "manifest-v1",
                RemotePreviewHandle = "preview-handle-v1",
                RemoteFileSize = 1024,
                RemoteUpdatedAt = RemoteUpdatedAt,
                RemoteVerifiedAt = RemoteUpdatedAt
            }
        };
        var snapshot = new WorkshopPublicationSnapshot(
            "content-v1",
            "metadata-v1",
            "preview-v1",
            "Pack title",
            "Pack description\nSecond line",
            WorkshopVisibility.Unlisted);
        var remote = new WorkshopRemoteState(
            project.PublishedWorkshopId,
            "manifest-v1",
            "preview-handle-v1",
            1024,
            RemoteUpdatedAt,
            snapshot.Title,
            "Pack description\r\nSecond line",
            (int)snapshot.Visibility,
            long.Parse(PzasmConstants.ProjectZomboidSteamAppId),
            long.Parse(PzasmConstants.ProjectZomboidSteamAppId),
            false);
        return (project, snapshot, remote);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
