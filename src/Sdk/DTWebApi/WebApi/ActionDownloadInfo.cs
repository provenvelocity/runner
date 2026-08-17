using System;
using System.Runtime.Serialization;

namespace GitHub.DistributedTask.WebApi
{
    [DataContract]
    public class ActionDownloadInfo
    {
        [DataMember(EmitDefaultValue = false)]
        public ActionDownloadAuthentication Authentication { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public ActionDownloadPackageDetails PackageDetails { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string NameWithOwner { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string ResolvedNameWithOwner { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string ResolvedSha { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string TarballUrl { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string Ref { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string ZipballUrl { get; set; }

        /// <summary>
        /// Set only when this action's uses: specified an explicit URL, overriding the runner's
        /// normally server-derived host. Used to keep the on-disk action cache from colliding
        /// between the same owner/repo@ref hosted on different servers.
        /// </summary>
        [DataMember(EmitDefaultValue = false)]
        public string SourceUrl { get; set; }
    }

    [DataContract]
    public class ActionDownloadAuthentication
    {
        [DataMember(EmitDefaultValue = false)]
        public DateTime ExpiresAt { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string Token { get; set; }
    }

    [DataContract]
    public class ActionDownloadPackageDetails
    {
        [DataMember(EmitDefaultValue = false)]
        public string Version { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string ManifestDigest { get; set; }
    }
}
