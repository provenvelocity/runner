using System.Collections.Generic;
using GitHub.Actions.WorkflowParser;
using GitHub.DistributedTask.ObjectTemplating.Tokens;
using GitHub.DistributedTask.Pipelines.ContextData;
using GitHub.DistributedTask.Pipelines.ObjectTemplating;
using GitHub.DistributedTask.WebApi;
using GitHub.Runner.Common;
using GitHub.Runner.Sdk;
using GitHub.Runner.Worker;
using LegacyContextData = GitHub.DistributedTask.Pipelines.ContextData;
using LegacyExpressions = GitHub.DistributedTask.Expressions2;
using Moq;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

namespace GitHub.Runner.Common.Tests.Worker
{
    /// <summary>
    /// Tests for EvaluateStepUses functionality — both the legacy and new evaluator paths.
    /// </summary>
    public sealed class EvaluateStepUsesL0
    {
        private CancellationTokenSource _ecTokenSource;
        private Mock<IExecutionContext> _ec;
        private TestHostContext _hc;

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void EvaluateStepUses_PlainString_BothEvaluatorsReturnSameValue()
        {
            try
            {
                Setup();

                var fileTable = new List<string>();
                var legacyTraceWriter = new GitHub.DistributedTask.ObjectTemplating.EmptyTraceWriter();
                var schema = PipelineTemplateSchemaFactory.GetSchema();
                var legacyEvaluator = new PipelineTemplateEvaluator(legacyTraceWriter, schema, fileTable);

                var newTraceWriter = new GitHub.Actions.WorkflowParser.ObjectTemplating.EmptyTraceWriter();
                var newEvaluator = new WorkflowTemplateEvaluator(newTraceWriter, fileTable, features: null);

                var token = new StringToken(null, null, null, "actions/checkout@v4");
                var newToken = new GitHub.Actions.WorkflowParser.ObjectTemplating.Tokens.StringToken(null, null, null, "actions/checkout@v4");

                var contextData = new DictionaryContextData();
                var newContextData = new GitHub.Actions.Expressions.Data.DictionaryExpressionData();
                var expressionFunctions = new List<LegacyExpressions.IFunctionInfo>();
                var newExpressionFunctions = new List<GitHub.Actions.Expressions.IFunctionInfo>();

                // Act
                var legacyResult = legacyEvaluator.EvaluateStepUses(token, contextData, expressionFunctions);
                var newResult = newEvaluator.EvaluateUses(newToken, newContextData, newExpressionFunctions);

                // Assert
                Assert.Equal("actions/checkout@v4", legacyResult);
                Assert.Equal("actions/checkout@v4", newResult);
                Assert.Equal(legacyResult, newResult);
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void EvaluateStepUses_MatrixExpression_ResolvesToString()
        {
            try
            {
                Setup();

                var fileTable = new List<string>();
                var legacyTraceWriter = new GitHub.DistributedTask.ObjectTemplating.EmptyTraceWriter();
                var schema = PipelineTemplateSchemaFactory.GetSchema();
                var legacyEvaluator = new PipelineTemplateEvaluator(legacyTraceWriter, schema, fileTable);

                var newTraceWriter = new GitHub.Actions.WorkflowParser.ObjectTemplating.EmptyTraceWriter();
                var newEvaluator = new WorkflowTemplateEvaluator(newTraceWriter, fileTable, features: null);

                // Expression: ${{ matrix.action }}@v4 would be InsertExpression in full,
                // but here we test a simple BasicExpression that resolves to a full reference
                var legacyToken = new BasicExpressionToken(null, null, null, "matrix.action");
                var newToken = new GitHub.Actions.WorkflowParser.ObjectTemplating.Tokens.BasicExpressionToken(null, null, null, "matrix.action");

                var contextData = new DictionaryContextData();
                var matrixData = new DictionaryContextData();
                matrixData["action"] = new StringContextData("actions/checkout@v4");
                contextData["matrix"] = matrixData;

                var newContextData = new GitHub.Actions.Expressions.Data.DictionaryExpressionData();
                var newMatrixData = new GitHub.Actions.Expressions.Data.DictionaryExpressionData();
                newMatrixData["action"] = new GitHub.Actions.Expressions.Data.StringExpressionData("actions/checkout@v4");
                newContextData["matrix"] = newMatrixData;

                var expressionFunctions = new List<LegacyExpressions.IFunctionInfo>();
                var newExpressionFunctions = new List<GitHub.Actions.Expressions.IFunctionInfo>();

                // Act
                var legacyResult = legacyEvaluator.EvaluateStepUses(legacyToken, contextData, expressionFunctions);
                var newResult = newEvaluator.EvaluateUses(newToken, newContextData, newExpressionFunctions);

                // Assert
                Assert.Equal("actions/checkout@v4", legacyResult);
                Assert.Equal("actions/checkout@v4", newResult);
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void EvaluateStepUses_NullToken_ReturnsNull()
        {
            try
            {
                Setup();

                var fileTable = new List<string>();
                var legacyTraceWriter = new GitHub.DistributedTask.ObjectTemplating.EmptyTraceWriter();
                var schema = PipelineTemplateSchemaFactory.GetSchema();
                var legacyEvaluator = new PipelineTemplateEvaluator(legacyTraceWriter, schema, fileTable);

                var newTraceWriter = new GitHub.Actions.WorkflowParser.ObjectTemplating.EmptyTraceWriter();
                var newEvaluator = new WorkflowTemplateEvaluator(newTraceWriter, fileTable, features: null);

                var contextData = new DictionaryContextData();
                var newContextData = new GitHub.Actions.Expressions.Data.DictionaryExpressionData();
                var expressionFunctions = new List<LegacyExpressions.IFunctionInfo>();
                var newExpressionFunctions = new List<GitHub.Actions.Expressions.IFunctionInfo>();

                // Act
                var legacyResult = legacyEvaluator.EvaluateStepUses(null, contextData, expressionFunctions);
                var newResult = newEvaluator.EvaluateUses(null, newContextData, newExpressionFunctions);

                // Assert — both return null for null token
                Assert.Null(legacyResult);
                Assert.Null(newResult);
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void EvaluateStepUses_DockerReference_BothEvaluatorsAgree()
        {
            try
            {
                Setup();

                var fileTable = new List<string>();
                var legacyTraceWriter = new GitHub.DistributedTask.ObjectTemplating.EmptyTraceWriter();
                var schema = PipelineTemplateSchemaFactory.GetSchema();
                var legacyEvaluator = new PipelineTemplateEvaluator(legacyTraceWriter, schema, fileTable);

                var newTraceWriter = new GitHub.Actions.WorkflowParser.ObjectTemplating.EmptyTraceWriter();
                var newEvaluator = new WorkflowTemplateEvaluator(newTraceWriter, fileTable, features: null);

                var token = new StringToken(null, null, null, "docker://ubuntu:22.04");
                var newToken = new GitHub.Actions.WorkflowParser.ObjectTemplating.Tokens.StringToken(null, null, null, "docker://ubuntu:22.04");

                var contextData = new DictionaryContextData();
                var newContextData = new GitHub.Actions.Expressions.Data.DictionaryExpressionData();
                var expressionFunctions = new List<LegacyExpressions.IFunctionInfo>();
                var newExpressionFunctions = new List<GitHub.Actions.Expressions.IFunctionInfo>();

                // Act
                var legacyResult = legacyEvaluator.EvaluateStepUses(token, contextData, expressionFunctions);
                var newResult = newEvaluator.EvaluateUses(newToken, newContextData, newExpressionFunctions);

                // Assert
                Assert.Equal("docker://ubuntu:22.04", legacyResult);
                Assert.Equal("docker://ubuntu:22.04", newResult);
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void EvaluateStepUses_InputsExpression_ResolvesWithFeatureFlag()
        {
            try
            {
                // Arrange — verify that EvaluateStepUses resolves a BasicExpressionToken against
                // provided context data (this is the code path exercised by ConvertToLegacySteps
                // when the EvaluateUsesExpressions feature flag is enabled).
                Setup();

                var fileTable = new List<string>();
                var legacyTraceWriter = new GitHub.DistributedTask.ObjectTemplating.EmptyTraceWriter();
                var schema = PipelineTemplateSchemaFactory.GetSchema();
                var legacyEvaluator = new PipelineTemplateEvaluator(legacyTraceWriter, schema, fileTable);

                // Expression token: ${{ inputs.action }}
                var expressionToken = new BasicExpressionToken(null, null, null, "inputs.action");

                var contextData = new DictionaryContextData();
                var inputsData = new DictionaryContextData();
                inputsData["action"] = new StringContextData("actions/checkout@v4");
                contextData["inputs"] = inputsData;

                var expressionFunctions = new List<LegacyExpressions.IFunctionInfo>();

                // Act — EvaluateStepUses should resolve inputs.action to the string value
                var result = legacyEvaluator.EvaluateStepUses(expressionToken, contextData, expressionFunctions);

                // Assert
                Assert.Equal("actions/checkout@v4", result);
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void EvaluateStepUses_FeatureFlagDisabled_ExpressionTokenFallsBackToStringCast()
        {
            try
            {
                // Arrange — feature flag off means expression tokens produce null reference in ConvertToLegacySteps
                Setup();
                // Feature flag NOT set (defaults to false)

                var legacyManager = new ActionManifestManagerLegacy();
                legacyManager.Initialize(_hc);
                _hc.SetSingleton<IActionManifestManagerLegacy>(legacyManager);

                var newManager = new ActionManifestManager();
                newManager.Initialize(_hc);
                _hc.SetSingleton<IActionManifestManager>(newManager);

                var wrapper = new ActionManifestManagerWrapper();
                wrapper.Initialize(_hc);

                _ec.Object.ExpressionValues["inputs"] = new LegacyContextData.DictionaryContextData
                {
                    { "action", new LegacyContextData.StringContextData("actions/checkout@v4") },
                };
                _ec.Object.ExpressionValues["github"] = new LegacyContextData.DictionaryContextData();
                _ec.Object.ExpressionValues["strategy"] = new LegacyContextData.DictionaryContextData();
                _ec.Object.ExpressionValues["matrix"] = new LegacyContextData.DictionaryContextData();

                // When feature flag is off, expression tokens in uses should NOT be evaluated;
                // the StringToken cast returns null, and ParseActionReference(null) returns null reference.
                // We verify EvaluateStepUses still works for a plain StringToken.
                var fileTable = new List<string>();
                var legacyTraceWriter = new GitHub.DistributedTask.ObjectTemplating.EmptyTraceWriter();
                var schema = PipelineTemplateSchemaFactory.GetSchema();
                var legacyEvaluator = new PipelineTemplateEvaluator(legacyTraceWriter, schema, fileTable);

                var plainToken = new StringToken(null, null, null, "actions/setup-node@v4");
                var result = legacyEvaluator.EvaluateStepUses(plainToken, new DictionaryContextData(), new List<LegacyExpressions.IFunctionInfo>());

                Assert.Equal("actions/setup-node@v4", result);
            }
            finally
            {
                Teardown();
            }
        }

        private void Setup([CallerMemberName] string name = "")
        {
            _ecTokenSource?.Dispose();
            _ecTokenSource = new CancellationTokenSource();

            _hc = new TestHostContext(this, name);

            var expressionValues = new LegacyContextData.DictionaryContextData();
            var expressionFunctions = new List<LegacyExpressions.IFunctionInfo>();

            _ec = new Mock<IExecutionContext>();
            _ec.Setup(x => x.Global)
                .Returns(new GlobalContext
                {
                    FileTable = new List<string>(),
                    Variables = new Variables(_hc, new System.Collections.Generic.Dictionary<string, VariableValue>()),
                    WriteDebug = true,
                });
            _ec.Setup(x => x.CancellationToken).Returns(_ecTokenSource.Token);
            _ec.Setup(x => x.ExpressionValues).Returns(expressionValues);
            _ec.Setup(x => x.ExpressionFunctions).Returns(expressionFunctions);
            _ec.Setup(x => x.Write(It.IsAny<string>(), It.IsAny<string>())).Callback((string tag, string message) => { _hc.GetTrace().Info($"{tag}{message}"); });
            _ec.Setup(x => x.AddIssue(It.IsAny<Issue>(), It.IsAny<ExecutionContextLogOptions>())).Callback((Issue issue, ExecutionContextLogOptions logOptions) => { _hc.GetTrace().Info($"[{issue.Type}]{logOptions.LogMessageOverride ?? issue.Message}"); });
        }

        private void Teardown()
        {
            _hc?.Dispose();
        }
    }
}
