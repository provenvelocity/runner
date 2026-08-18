using GitHub.DistributedTask.Pipelines;
using Xunit;

namespace GitHub.Runner.Common.Tests
{
    /// <summary>
    /// Tests for <see cref="ActionReferenceBuilder"/>, the single uses: parser shared by the
    /// legacy PipelineTemplateConverter and the WorkflowParser bridge (ActionManifestManagerWrapper).
    /// </summary>
    public sealed class ActionReferenceBuilderL0
    {
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryParse_DockerImage_ReturnsContainerRegistryReference()
        {
            Assert.True(ActionReferenceBuilder.TryParse("docker://alpine:3.19", out var reference, out var error));
            Assert.Null(error);
            var container = Assert.IsType<ContainerRegistryReference>(reference);
            Assert.Equal("alpine:3.19", container.Image);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryParse_LocalPath_ReturnsSelfAliasReference()
        {
            Assert.True(ActionReferenceBuilder.TryParse("./.github/actions/local", out var reference, out var error));
            Assert.Null(error);
            var repo = Assert.IsType<RepositoryPathReference>(reference);
            Assert.Equal(PipelineConstants.SelfAlias, repo.RepositoryType);
            Assert.Equal("./.github/actions/local", repo.Path);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryParse_SelfRepository_ReturnsSelfRepositoryAliasReference()
        {
            Assert.True(ActionReferenceBuilder.TryParse("$/.github/actions/embedded", out var reference, out var error));
            Assert.Null(error);
            var repo = Assert.IsType<RepositoryPathReference>(reference);
            Assert.Equal(PipelineConstants.SelfRepositoryAlias, repo.RepositoryType);
            Assert.Equal(".github/actions/embedded", repo.Path);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryParse_OwnerRepo_ReturnsGitHubReferenceWithoutUrl()
        {
            Assert.True(ActionReferenceBuilder.TryParse("actions/checkout@v4", out var reference, out var error));
            Assert.Null(error);
            var repo = Assert.IsType<RepositoryPathReference>(reference);
            Assert.Equal(RepositoryTypes.GitHub, repo.RepositoryType);
            Assert.Equal("actions/checkout", repo.Name);
            Assert.Equal("v4", repo.Ref);
            Assert.Equal(string.Empty, repo.Path);
            Assert.Null(repo.Url);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryParse_OwnerRepoSubPath_ReturnsGitHubReferenceWithPath()
        {
            Assert.True(ActionReferenceBuilder.TryParse("owner/repo/.github/actions/foo@v1", out var reference, out var error));
            Assert.Null(error);
            var repo = Assert.IsType<RepositoryPathReference>(reference);
            Assert.Equal("owner/repo", repo.Name);
            Assert.Equal("v1", repo.Ref);
            Assert.Equal(".github/actions/foo", repo.Path);
            Assert.Null(repo.Url);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryParse_CustomUrl_ReturnsGitHubReferenceWithAuthorityUrl()
        {
            Assert.True(ActionReferenceBuilder.TryParse("https://github.kp.org/owner/repo@ref", out var reference, out var error));
            Assert.Null(error);
            var repo = Assert.IsType<RepositoryPathReference>(reference);
            Assert.Equal(RepositoryTypes.GitHub, repo.RepositoryType);
            Assert.Equal("owner/repo", repo.Name);
            Assert.Equal("ref", repo.Ref);
            Assert.Equal(string.Empty, repo.Path);
            Assert.Equal("https://github.kp.org", repo.Url);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryParse_CustomUrlWithSubPath_ParsesRepoPathAndAuthority()
        {
            Assert.True(ActionReferenceBuilder.TryParse("https://github.kp.org/owner/repo/.github/actions/checkout@v10.5.0", out var reference, out var error));
            Assert.Null(error);
            var repo = Assert.IsType<RepositoryPathReference>(reference);
            Assert.Equal("owner/repo", repo.Name);
            Assert.Equal("v10.5.0", repo.Ref);
            Assert.Equal(".github/actions/checkout", repo.Path);
            Assert.Equal("https://github.kp.org", repo.Url);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TryParse_HttpCustomUrlWithPort_PreservesAuthority()
        {
            Assert.True(ActionReferenceBuilder.TryParse("http://ghes.internal:8443/owner/repo@main", out var reference, out var error));
            Assert.Null(error);
            var repo = Assert.IsType<RepositoryPathReference>(reference);
            Assert.Equal("owner/repo", repo.Name);
            Assert.Equal("main", repo.Ref);
            Assert.Equal("http://ghes.internal:8443", repo.Url);
        }

        [Theory]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        [InlineData("actions/checkout")]           // missing @ref
        [InlineData("nope@v1")]                     // missing repo segment
        [InlineData("actions/checkout@")]           // empty ref
        public void TryParse_InvalidRepositoryFormat_ReturnsErrorAndNoReference(string uses)
        {
            Assert.False(ActionReferenceBuilder.TryParse(uses, out var reference, out var error));
            Assert.Null(reference);
            Assert.Contains("{org}/{repo}[/path]@ref", error);
        }

        [Theory]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        [InlineData("https://github.kp.org/owner@ref")]   // missing repo segment
        [InlineData("https://github.kp.org/owner/repo")]  // missing @ref
        public void TryParse_InvalidUrlFormat_ReturnsUrlErrorAndNoReference(string uses)
        {
            Assert.False(ActionReferenceBuilder.TryParse(uses, out var reference, out var error));
            Assert.Null(reference);
            Assert.Contains("{scheme}://{host}/{org}/{repo}[/path]@ref", error);
        }
    }
}
