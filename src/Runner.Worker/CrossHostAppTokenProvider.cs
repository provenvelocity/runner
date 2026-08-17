using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Runner.Common;
using GitHub.Runner.Sdk;
using Newtonsoft.Json.Linq;

namespace GitHub.Runner.Worker
{
    // Config schema for docs/feature/secure_app_id.md "Option A": a folder of *.json files, each
    // listing one or more allow-listed hosts for the custom uses: URL feature. Only hosts listed
    // here ever get a token attached to their action-download request.
    public sealed class CrossHostAppConfigFile
    {
        public List<CrossHostAppEntry> Hosts { get; set; }
    }

    public sealed class CrossHostAppEntry
    {
        public string Host { get; set; }
        public string AppId { get; set; }
        public string PrivateKeyPath { get; set; }
        public string InstallationId { get; set; }
    }

    [ServiceLocator(Default = typeof(CrossHostAppTokenProvider))]
    public interface ICrossHostAppTokenProvider : IRunnerService
    {
        /// <summary>
        /// Returns a GitHub App installation token scoped to <paramref name="host"/>, or null if
        /// <paramref name="host"/> isn't on the allow-list (meaning: send an anonymous request).
        /// Throws if the host IS allow-listed but is misconfigured or minting fails — a listed host
        /// never silently falls back to an unauthenticated request.
        /// </summary>
        Task<string> TryGetTokenAsync(IExecutionContext executionContext, string host, string owner);
    }

    public sealed class CrossHostAppTokenProvider : RunnerService, ICrossHostAppTokenProvider
    {
        private const string DefaultAllowListDirEnv = "ACTIONS_RUNNER_CROSS_HOST_APPS_DIR";
        private const string DefaultAllowListDirName = ".cross_host_apps";

        private readonly object _loadLock = new();
        private readonly SemaphoreSlim _mintLock = new(1, 1);
        private readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresAtUtc)> _tokenCache = new(StringComparer.OrdinalIgnoreCase);

        private volatile bool _loaded;
        private Dictionary<string, CrossHostAppEntry> _allowList = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _invalidHostReasons = new(StringComparer.OrdinalIgnoreCase);

        public async Task<string> TryGetTokenAsync(IExecutionContext executionContext, string host, string owner)
        {
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNullOrEmpty(host, nameof(host));
            ArgUtil.NotNullOrEmpty(owner, nameof(owner));

            EnsureAllowListLoaded();

            // Fail loud for a host that's listed but broken, rather than silently degrading to an
            // anonymous request the admin never intended.
            if (_invalidHostReasons.TryGetValue(host, out var reason))
            {
                throw new InvalidOperationException($"Cross-host app entry for host '{host}' is misconfigured: {reason}");
            }

            if (!_allowList.TryGetValue(host, out var entry))
            {
                // Not allow-listed at all: anonymous request, this is the normal/expected case.
                return null;
            }

            if (_tokenCache.TryGetValue(host, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(5))
            {
                return cached.Token;
            }

            await _mintLock.WaitAsync(executionContext.CancellationToken);
            try
            {
                // Re-check now that we hold the lock in case a concurrent download already minted one.
                if (_tokenCache.TryGetValue(host, out cached) && cached.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(5))
                {
                    return cached.Token;
                }

                var jwt = BuildAppJwt(entry.AppId, entry.PrivateKeyPath);
                var apiBase = GetApiBase(entry.Host);
                var installationId = entry.InstallationId;
                if (string.IsNullOrEmpty(installationId))
                {
                    installationId = await ResolveInstallationIdAsync(executionContext, apiBase, jwt, owner, entry.Host);
                }

                var minted = await MintInstallationTokenAsync(executionContext, apiBase, jwt, installationId, entry.Host);
                _tokenCache[host] = minted;
                return minted.Token;
            }
            finally
            {
                _mintLock.Release();
            }
        }

        private void EnsureAllowListLoaded()
        {
            if (_loaded)
            {
                return;
            }

            lock (_loadLock)
            {
                if (_loaded)
                {
                    return;
                }

                var allowList = new Dictionary<string, CrossHostAppEntry>(StringComparer.OrdinalIgnoreCase);
                var invalidHostReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                LoadAllowList(allowList, invalidHostReasons);
                _allowList = allowList;
                _invalidHostReasons = invalidHostReasons;
                _loaded = true;
            }
        }

        private void LoadAllowList(Dictionary<string, CrossHostAppEntry> allowList, Dictionary<string, string> invalidHostReasons)
        {
            var dir = Environment.GetEnvironmentVariable(DefaultAllowListDirEnv);
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Root), DefaultAllowListDirName);
            }

            if (!Directory.Exists(dir))
            {
                Trace.Info($"Cross-host app allow-list directory '{dir}' does not exist; no custom uses: hosts are allow-listed.");
                return;
            }

            foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                List<CrossHostAppEntry> entries;
                try
                {
                    var content = File.ReadAllText(file);
                    entries = StringUtil.ConvertFromJson<CrossHostAppConfigFile>(content)?.Hosts;
                }
                catch (Exception ex)
                {
                    Trace.Warning($"Failed to parse cross-host app allow-list file '{file}': {ex.Message}");
                    continue;
                }

                if (entries == null)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry?.Host))
                    {
                        Trace.Warning($"Skipping cross-host app entry in '{file}' with no host.");
                        continue;
                    }

                    if (allowList.ContainsKey(entry.Host) || invalidHostReasons.ContainsKey(entry.Host))
                    {
                        Trace.Warning($"Duplicate cross-host app entry for host '{entry.Host}' in '{file}'; keeping the first one loaded.");
                        continue;
                    }

                    if (string.IsNullOrEmpty(entry.AppId) || string.IsNullOrEmpty(entry.PrivateKeyPath))
                    {
                        invalidHostReasons[entry.Host] = $"'{file}' is missing required field(s) appId/privateKeyPath.";
                        continue;
                    }

                    if (!File.Exists(entry.PrivateKeyPath))
                    {
                        invalidHostReasons[entry.Host] = $"private key file '{entry.PrivateKeyPath}' not found.";
                        continue;
                    }

                    allowList[entry.Host] = entry;
                    Trace.Info($"Loaded cross-host app allow-list entry for host '{entry.Host}'.");
                }
            }
        }

        // Signs a GitHub App JWT (RS256, <=10 minute lifetime per GitHub's rules) directly, rather than
        // GitHub.Services.WebApi.Jwt.JsonWebToken, which requires an Audience claim GitHub App JWTs
        // don't use. The RSA key is disposed immediately after signing.
        private string BuildAppJwt(string appId, string privateKeyPath)
        {
            string pem;
            try
            {
                pem = File.ReadAllText(privateKeyPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Unable to read GitHub App private key file '{privateKeyPath}': {ex.Message}", ex);
            }

            byte[] signature;
            string unsignedToken;
            using (var rsa = RSA.Create())
            {
                try
                {
                    rsa.ImportFromPem(pem);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Private key file '{privateKeyPath}' is not a valid PEM-encoded RSA private key.", ex);
                }

                var now = DateTimeOffset.UtcNow;
                var header = new JObject { ["alg"] = "RS256", ["typ"] = "JWT" };
                var payload = new JObject
                {
                    ["iat"] = now.AddSeconds(-60).ToUnixTimeSeconds(),
                    ["exp"] = now.AddMinutes(9).ToUnixTimeSeconds(), // stay comfortably under GitHub's 10-minute max
                    ["iss"] = appId,
                };

                unsignedToken = $"{Base64UrlEncode(Encoding.UTF8.GetBytes(header.ToString(Newtonsoft.Json.Formatting.None)))}." +
                    $"{Base64UrlEncode(Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None)))}";
                signature = rsa.SignData(Encoding.UTF8.GetBytes(unsignedToken), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }

            var jwt = $"{unsignedToken}.{Base64UrlEncode(signature)}";
            // The App JWT can mint installation tokens for up to 10 minutes; mask it like any other secret.
            HostContext.SecretMasker.AddValue(jwt);
            return jwt;
        }

        private static string GetApiBase(string host)
        {
            var uriBuilder = new UriBuilder(Uri.UriSchemeHttps, host);
            return UrlUtil.IsHostedServer(uriBuilder)
                ? $"https://api.{host}"
                : $"https://{host}/api/v3";
        }

        private async Task<string> ResolveInstallationIdAsync(IExecutionContext executionContext, string apiBase, string jwt, string owner, string hostForLogging)
        {
            var nextUrl = $"{apiBase}/app/installations?per_page=100";
            for (var page = 0; page < 10 && !string.IsNullOrEmpty(nextUrl); page++)
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, executionContext.CancellationToken);
                using var httpClientHandler = HostContext.CreateHttpClientHandler();
                using var httpClient = new HttpClient(httpClientHandler);
                foreach (var userAgent in HostContext.UserAgents)
                {
                    httpClient.DefaultRequestHeaders.UserAgent.Add(userAgent);
                }
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

                using var response = await httpClient.GetAsync(nextUrl, linkedCts.Token);
                var requestId = UrlUtil.GetGitHubRequestId(response.Headers);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Failed to list GitHub App installations on host '{hostForLogging}' (HTTP {(int)response.StatusCode}, request id: {requestId}).");
                }

                var body = await response.Content.ReadAsStringAsync();
                foreach (var installation in JArray.Parse(body))
                {
                    var login = installation["account"]?["login"]?.ToString();
                    if (string.Equals(login, owner, StringComparison.OrdinalIgnoreCase))
                    {
                        return installation["id"]?.ToString();
                    }
                }

                nextUrl = GetNextLink(response.Headers);
            }

            throw new InvalidOperationException($"No GitHub App installation found for owner '{owner}' on host '{hostForLogging}'.");
        }

        private async Task<(string Token, DateTime ExpiresAtUtc)> MintInstallationTokenAsync(IExecutionContext executionContext, string apiBase, string jwt, string installationId, string hostForLogging)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, executionContext.CancellationToken);
            using var httpClientHandler = HostContext.CreateHttpClientHandler();
            using var httpClient = new HttpClient(httpClientHandler);
            foreach (var userAgent in HostContext.UserAgents)
            {
                httpClient.DefaultRequestHeaders.UserAgent.Add(userAgent);
            }
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

            using var content = new StringContent(string.Empty);
            using var response = await httpClient.PostAsync($"{apiBase}/app/installations/{installationId}/access_tokens", content, linkedCts.Token);
            var requestId = UrlUtil.GetGitHubRequestId(response.Headers);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Failed to mint a GitHub App installation token for host '{hostForLogging}' (HTTP {(int)response.StatusCode}, request id: {requestId}).");
            }

            var body = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(body);
            var token = json["token"]?.ToString();
            var expiresAtRaw = json["expires_at"]?.ToString();
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(expiresAtRaw) ||
                !DateTime.TryParse(expiresAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expiresAtUtc))
            {
                throw new InvalidOperationException($"Unexpected response minting a GitHub App installation token for host '{hostForLogging}'.");
            }

            HostContext.SecretMasker.AddValue(token);
            return (token, expiresAtUtc);
        }

        private static string GetNextLink(HttpResponseHeaders headers)
        {
            if (!headers.TryGetValues("Link", out var values))
            {
                return null;
            }

            foreach (var value in values)
            {
                foreach (var part in value.Split(','))
                {
                    var segments = part.Split(';');
                    if (segments.Length < 2)
                    {
                        continue;
                    }

                    var isNext = segments.Skip(1).Any(s => s.Trim().Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase));
                    if (isNext)
                    {
                        return segments[0].Trim().Trim('<', '>');
                    }
                }
            }

            return null;
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }
    }
}
