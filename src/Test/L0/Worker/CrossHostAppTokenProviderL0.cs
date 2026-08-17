using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Runner.Common;
using GitHub.Runner.Sdk;
using GitHub.Runner.Worker;
using Moq;
using Moq.Protected;
using Xunit;

namespace GitHub.Runner.Common.Tests.Worker
{
    public sealed class CrossHostAppTokenProviderL0
    {
        private const string EnvVar = "ACTIONS_RUNNER_CROSS_HOST_APPS_DIR";

        private TestHostContext _hc;
        private Mock<IExecutionContext> _ec;
        private CancellationTokenSource _ecTokenSource;
        private string _configDir;
        private string _previousEnvValue;

        private void Setup([System.Runtime.CompilerServices.CallerMemberName] string name = "")
        {
            _previousEnvValue = Environment.GetEnvironmentVariable(EnvVar);
            _ecTokenSource = new CancellationTokenSource();
            _hc = new TestHostContext(this, name);
            _ec = new Mock<IExecutionContext>();
            _ec.Setup(x => x.CancellationToken).Returns(_ecTokenSource.Token);

            _configDir = Path.Combine(_hc.GetDirectory(WellKnownDirectory.Temp), "cross_host_apps_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_configDir);
            Environment.SetEnvironmentVariable(EnvVar, _configDir);
        }

        private void Teardown()
        {
            Environment.SetEnvironmentVariable(EnvVar, _previousEnvValue);
            _ecTokenSource?.Dispose();
            _hc?.Dispose();
        }

        private static string WritePrivateKey(string dir, string fileName = "app.pem")
        {
            using var rsa = RSA.Create(2048);
            var keyPath = Path.Combine(dir, fileName);
            File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem());
            return keyPath;
        }

        private void WriteAllowList(string fileName, string json)
        {
            File.WriteAllText(Path.Combine(_configDir, fileName), json);
        }

        private CrossHostAppTokenProvider CreateProvider(Mock<HttpClientHandler> mockClientHandler)
        {
            var mockHandlerFactory = new Mock<IHttpClientHandlerFactory>();
            mockHandlerFactory.Setup(p => p.CreateClientHandler(It.IsAny<RunnerWebProxy>())).Returns(mockClientHandler.Object);
            _hc.SetSingleton<IHttpClientHandlerFactory>(mockHandlerFactory.Object);

            var provider = new CrossHostAppTokenProvider();
            provider.Initialize(_hc);
            return provider;
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async void TryGetTokenAsync_HostNotAllowListed_ReturnsNullWithNoHttpCalls()
        {
            try
            {
                Setup();
                var mockClientHandler = new Mock<HttpClientHandler>();
                var provider = CreateProvider(mockClientHandler);

                var token = await provider.TryGetTokenAsync(_ec.Object, "github.kp.org", "someowner");

                Assert.Null(token);
                mockClientHandler.Protected().Verify("SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async void TryGetTokenAsync_HostAllowListedButKeyMissing_ThrowsClearError()
        {
            try
            {
                Setup();
                WriteAllowList("hosts.json", @"{
                    ""hosts"": [
                        { ""host"": ""github.kp.org"", ""appId"": ""123"", ""privateKeyPath"": ""/does/not/exist.pem"" }
                    ]
                }");
                var mockClientHandler = new Mock<HttpClientHandler>();
                var provider = CreateProvider(mockClientHandler);

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => provider.TryGetTokenAsync(_ec.Object, "github.kp.org", "someowner"));

                Assert.Contains("github.kp.org", ex.Message);
                mockClientHandler.Protected().Verify("SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async void TryGetTokenAsync_ExplicitInstallationId_MintsAndCachesToken()
        {
            try
            {
                Setup();
                var keyPath = WritePrivateKey(_configDir);
                WriteAllowList("hosts.json", $@"{{
                    ""hosts"": [
                        {{ ""host"": ""github.kp.org"", ""appId"": ""123"", ""privateKeyPath"": ""{keyPath.Replace("\\", "\\\\")}"", ""installationId"": ""789"" }}
                    ]
                }}");

                var mockClientHandler = new Mock<HttpClientHandler>();
                mockClientHandler.Protected()
                    .Setup<Task<HttpResponseMessage>>("SendAsync",
                        ItExpr.Is<HttpRequestMessage>(m => m.Method == HttpMethod.Post && m.RequestUri == new Uri("https://github.kp.org/api/v3/app/installations/789/access_tokens")),
                        ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StringContent($"{{\"token\":\"ghs_minted_token\",\"expires_at\":\"{DateTime.UtcNow.AddHours(1):O}\"}}")
                    });

                var provider = CreateProvider(mockClientHandler);

                var token1 = await provider.TryGetTokenAsync(_ec.Object, "github.kp.org", "someowner");
                var token2 = await provider.TryGetTokenAsync(_ec.Object, "github.kp.org", "someowner");

                Assert.Equal("ghs_minted_token", token1);
                Assert.Equal("ghs_minted_token", token2);
                Assert.NotEqual("ghs_minted_token", _hc.SecretMasker.MaskSecrets("ghs_minted_token"));

                // Second call should hit the in-memory cache, not mint again.
                mockClientHandler.Protected().Verify("SendAsync", Times.Once(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async void TryGetTokenAsync_NoInstallationId_ResolvesByOwnerThenMints()
        {
            try
            {
                Setup();
                var keyPath = WritePrivateKey(_configDir);
                WriteAllowList("hosts.json", $@"{{
                    ""hosts"": [
                        {{ ""host"": ""github.kp.org"", ""appId"": ""123"", ""privateKeyPath"": ""{keyPath.Replace("\\", "\\\\")}"" }}
                    ]
                }}");

                var mockClientHandler = new Mock<HttpClientHandler>();
                mockClientHandler.Protected()
                    .Setup<Task<HttpResponseMessage>>("SendAsync",
                        ItExpr.Is<HttpRequestMessage>(m => m.Method == HttpMethod.Get && m.RequestUri == new Uri("https://github.kp.org/api/v3/app/installations?per_page=100")),
                        ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"[{""id"": 789, ""account"": {""login"": ""someowner""}}]")
                    });
                mockClientHandler.Protected()
                    .Setup<Task<HttpResponseMessage>>("SendAsync",
                        ItExpr.Is<HttpRequestMessage>(m => m.Method == HttpMethod.Post && m.RequestUri == new Uri("https://github.kp.org/api/v3/app/installations/789/access_tokens")),
                        ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StringContent($"{{\"token\":\"ghs_minted_token\",\"expires_at\":\"{DateTime.UtcNow.AddHours(1):O}\"}}")
                    });

                var provider = CreateProvider(mockClientHandler);

                var token = await provider.TryGetTokenAsync(_ec.Object, "github.kp.org", "someowner");

                Assert.Equal("ghs_minted_token", token);
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async void TryGetTokenAsync_NoInstallationFoundForOwner_ThrowsClearError()
        {
            try
            {
                Setup();
                var keyPath = WritePrivateKey(_configDir);
                WriteAllowList("hosts.json", $@"{{
                    ""hosts"": [
                        {{ ""host"": ""github.kp.org"", ""appId"": ""123"", ""privateKeyPath"": ""{keyPath.Replace("\\", "\\\\")}"" }}
                    ]
                }}");

                var mockClientHandler = new Mock<HttpClientHandler>();
                mockClientHandler.Protected()
                    .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"[{""id"": 111, ""account"": {""login"": ""someotherowner""}}]")
                    });

                var provider = CreateProvider(mockClientHandler);

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => provider.TryGetTokenAsync(_ec.Object, "github.kp.org", "someowner"));

                Assert.Contains("someowner", ex.Message);
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async void TryGetTokenAsync_PerOrgOverride_UsesOwnerSpecificAppInsteadOfHostDefault()
        {
            try
            {
                Setup();
                var defaultKeyPath = WritePrivateKey(_configDir, "default.pem");
                var teamAKeyPath = WritePrivateKey(_configDir, "team-a.pem");
                WriteAllowList("hosts.json", $@"{{
                    ""hosts"": [
                        {{
                            ""host"": ""github.kp.org"",
                            ""appId"": ""111"",
                            ""privateKeyPath"": ""{defaultKeyPath.Replace("\\", "\\\\")}"",
                            ""installationId"": ""999"",
                            ""owners"": [
                                {{ ""owner"": ""team-a"", ""appId"": ""222"", ""privateKeyPath"": ""{teamAKeyPath.Replace("\\", "\\\\")}"", ""installationId"": ""333"" }}
                            ]
                        }}
                    ]
                }}");

                var mockClientHandler = new Mock<HttpClientHandler>();
                mockClientHandler.Protected()
                    .Setup<Task<HttpResponseMessage>>("SendAsync",
                        ItExpr.Is<HttpRequestMessage>(m => m.RequestUri == new Uri("https://github.kp.org/api/v3/app/installations/333/access_tokens")),
                        ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StringContent($"{{\"token\":\"team_a_token\",\"expires_at\":\"{DateTime.UtcNow.AddHours(1):O}\"}}")
                    });
                mockClientHandler.Protected()
                    .Setup<Task<HttpResponseMessage>>("SendAsync",
                        ItExpr.Is<HttpRequestMessage>(m => m.RequestUri == new Uri("https://github.kp.org/api/v3/app/installations/999/access_tokens")),
                        ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StringContent($"{{\"token\":\"host_default_token\",\"expires_at\":\"{DateTime.UtcNow.AddHours(1):O}\"}}")
                    });

                var provider = CreateProvider(mockClientHandler);

                var teamAToken = await provider.TryGetTokenAsync(_ec.Object, "github.kp.org", "team-a");
                var otherOwnerToken = await provider.TryGetTokenAsync(_ec.Object, "github.kp.org", "some-other-owner");

                Assert.Equal("team_a_token", teamAToken);
                Assert.Equal("host_default_token", otherOwnerToken);
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async void TryGetTokenAsync_OwnersOnlyNoHostDefault_UnlistedOwnerIsAnonymous()
        {
            try
            {
                Setup();
                var teamAKeyPath = WritePrivateKey(_configDir, "team-a.pem");
                WriteAllowList("hosts.json", $@"{{
                    ""hosts"": [
                        {{
                            ""host"": ""github.kp.org"",
                            ""owners"": [
                                {{ ""owner"": ""team-a"", ""appId"": ""222"", ""privateKeyPath"": ""{teamAKeyPath.Replace("\\", "\\\\")}"", ""installationId"": ""333"" }}
                            ]
                        }}
                    ]
                }}");

                var mockClientHandler = new Mock<HttpClientHandler>();
                var provider = CreateProvider(mockClientHandler);

                var token = await provider.TryGetTokenAsync(_ec.Object, "github.kp.org", "some-other-owner");

                Assert.Null(token);
                mockClientHandler.Protected().Verify("SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async void TryGetTokenAsync_BrokenOwnerOverride_ThrowsButOtherOwnersStillWork()
        {
            try
            {
                Setup();
                var teamAKeyPath = WritePrivateKey(_configDir, "team-a.pem");
                WriteAllowList("hosts.json", $@"{{
                    ""hosts"": [
                        {{
                            ""host"": ""github.kp.org"",
                            ""owners"": [
                                {{ ""owner"": ""team-a"", ""appId"": ""222"", ""privateKeyPath"": ""{teamAKeyPath.Replace("\\", "\\\\")}"", ""installationId"": ""333"" }},
                                {{ ""owner"": ""team-broken"", ""appId"": ""444"", ""privateKeyPath"": ""/does/not/exist.pem"" }}
                            ]
                        }}
                    ]
                }}");

                var mockClientHandler = new Mock<HttpClientHandler>();
                mockClientHandler.Protected()
                    .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StringContent($"{{\"token\":\"team_a_token\",\"expires_at\":\"{DateTime.UtcNow.AddHours(1):O}\"}}")
                    });

                var provider = CreateProvider(mockClientHandler);

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => provider.TryGetTokenAsync(_ec.Object, "github.kp.org", "team-broken"));
                Assert.Contains("team-broken", ex.Message);

                var teamAToken = await provider.TryGetTokenAsync(_ec.Object, "github.kp.org", "team-a");
                Assert.Equal("team_a_token", teamAToken);
            }
            finally
            {
                Teardown();
            }
        }
    }
}

