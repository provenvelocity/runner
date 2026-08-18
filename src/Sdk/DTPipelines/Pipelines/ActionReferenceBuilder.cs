using System;
using System.Linq;

namespace GitHub.DistributedTask.Pipelines
{
    /// <summary>
    /// Single source of truth for turning a <c>uses:</c> string into an
    /// <see cref="ActionStepDefinitionReference"/>. Shared by the legacy template
    /// converter and the WorkflowParser bridge so both parse identically
    /// (docker://, ./local, $/ self-repository, fully-qualified http(s) URL, and
    /// the plain owner/repo[/path]@ref form).
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class ActionReferenceBuilder
    {
        /// <summary>
        /// Parses a <c>uses:</c> value. Returns true and sets <paramref name="reference"/>
        /// when the value is well-formed; returns false and sets <paramref name="error"/>
        /// (a caller-loggable message) when the org/repo or URL form is invalid.
        /// </summary>
        public static bool TryParse(string uses, out ActionStepDefinitionReference reference, out string error)
        {
            reference = null;
            error = null;

            if (uses == null)
            {
                error = InvalidRepositoryFormat(uses);
                return false;
            }

            if (uses.StartsWith("docker://", StringComparison.Ordinal))
            {
                reference = new ContainerRegistryReference { Image = uses.Substring("docker://".Length) };
                return true;
            }

            if (uses.StartsWith("./") || uses.StartsWith(".\\"))
            {
                reference = new RepositoryPathReference
                {
                    RepositoryType = PipelineConstants.SelfAlias,
                    Path = uses
                };
                return true;
            }

            if (PipelineConstants.TryParseSelfRepository(uses, out var selfPath))
            {
                reference = new RepositoryPathReference
                {
                    RepositoryType = PipelineConstants.SelfRepositoryAlias,
                    Path = selfPath
                };
                return true;
            }

            if (Uri.TryCreate(uses, UriKind.Absolute, out var usesUri) &&
                (String.Equals(usesUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(usesUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                // Fully-qualified uses: URL. The org/repo[/path]@ref portion lives in the URL path;
                // the scheme+host(+port) overrides the runner's normally server-derived host for this action only.
                var usesSegments = usesUri.AbsolutePath.TrimStart('/').Split('@');
                var pathSegments = usesSegments[0].Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                var gitRef = usesSegments.Length == 2 ? usesSegments[1] : String.Empty;

                if (usesSegments.Length != 2 ||
                    pathSegments.Length < 2 ||
                    String.IsNullOrEmpty(pathSegments[0]) ||
                    String.IsNullOrEmpty(pathSegments[1]) ||
                    String.IsNullOrEmpty(gitRef))
                {
                    error = InvalidUrlFormat(uses);
                    return false;
                }

                reference = new RepositoryPathReference
                {
                    RepositoryType = RepositoryTypes.GitHub,
                    Name = $"{pathSegments[0]}/{pathSegments[1]}",
                    Ref = gitRef,
                    Path = pathSegments.Length > 2 ? String.Join("/", pathSegments.Skip(2)) : String.Empty,
                    Url = usesUri.GetLeftPart(UriPartial.Authority),
                };
                return true;
            }
            else
            {
                var usesSegments = uses.Split('@');
                var pathSegments = usesSegments[0].Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                var gitRef = usesSegments.Length == 2 ? usesSegments[1] : String.Empty;

                if (usesSegments.Length != 2 ||
                    pathSegments.Length < 2 ||
                    String.IsNullOrEmpty(pathSegments[0]) ||
                    String.IsNullOrEmpty(pathSegments[1]) ||
                    String.IsNullOrEmpty(gitRef))
                {
                    error = InvalidRepositoryFormat(uses);
                    return false;
                }

                reference = new RepositoryPathReference
                {
                    RepositoryType = RepositoryTypes.GitHub,
                    Name = $"{pathSegments[0]}/{pathSegments[1]}",
                    Ref = gitRef,
                    Path = pathSegments.Length > 2 ? String.Join("/", pathSegments.Skip(2)) : String.Empty,
                };
                return true;
            }
        }

        // todo: loc
        private static string InvalidUrlFormat(string uses)
            => $"Expected format {{scheme}}://{{host}}/{{org}}/{{repo}}[/path]@ref. Actual '{uses}'";

        // todo: loc
        private static string InvalidRepositoryFormat(string uses)
            => $"Expected format {{org}}/{{repo}}[/path]@ref. Actual '{uses}'";
    }
}
