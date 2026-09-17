using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RMC.TotalRisk.Api.DTOs;
using RMC.TotalRisk.Api.Tests.Support;

namespace RMC.TotalRisk.Api.Tests.Integration;

/// <summary>
/// Full-host integration tests over <see cref="WebApplicationFactory{TEntryPoint}"/>: the HTTP
/// contract, the OpenAPI document, the MCP endpoint, a numeric golden anchored to the exact
/// expected-annual-damage closed form, and the byte-level reproducibility of results.
/// </summary>
[TestClass]
public class ApiIntegrationTests
{
    /// <summary>The shared test host.</summary>
    private static WebApplicationFactory<Program>? _factory;

    /// <summary>The shared client.</summary>
    private static HttpClient? _client;

    /// <summary>Boots the host once for the class.</summary>
    /// <param name="context">The test context (unused).</param>
    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    /// <summary>Disposes the host.</summary>
    [ClassCleanup]
    public static void ClassCleanup()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>Posts a compute request and returns the raw response.</summary>
    /// <param name="path">The endpoint path.</param>
    /// <param name="request">The request payload.</param>
    /// <returns>The HTTP response and its body text.</returns>
    private static async Task<(HttpResponseMessage Response, string Body)> PostAsync(string path, ComputeRiskAnalysisRequest request)
    {
        using var content = new StringContent(TestJson.Serialize(request), Encoding.UTF8, "application/json");
        var response = await _client!.PostAsync(path, content);
        string body = await response.Content.ReadAsStringAsync();
        return (response, body);
    }

    /// <summary>Health, info, and the OpenAPI document respond.</summary>
    [TestMethod]
    public async Task Test_HealthInfoOpenApi_Respond()
    {
        // Act
        var health = await _client!.GetAsync("/health");
        var detailed = await _client.GetStringAsync("/health/detailed");
        var info = await _client.GetStringAsync("/api/info");
        var openApi = await _client.GetAsync("/openapi/v1.json");

        // Assert
        Assert.IsTrue(health.IsSuccessStatusCode);
        StringAssert.Contains(detailed, "healthy");
        StringAssert.Contains(info, "RMC-TotalRisk API");
        StringAssert.Contains(info, "apiContractVersion");
        Assert.IsTrue(openApi.IsSuccessStatusCode);
        StringAssert.Contains(await openApi.Content.ReadAsStringAsync(), "risk-analyses");
    }

    /// <summary>
    /// The deployed container runs in the Production environment under a path base behind a
    /// TLS-terminating load balancer: the endpoints must answer under the prefix, over plain HTTP,
    /// with no redirect, or the container's health probe never succeeds.
    /// </summary>
    [TestMethod]
    public async Task Test_ProductionHostingUnderPathBase_ServesPlainHttpWithoutRedirect()
    {
        // Arrange
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("PathBase", "/total-risk");
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        var health = await client.GetAsync("/total-risk/health");
        var detailed = await client.GetAsync("/total-risk/health/detailed");
        var info = await client.GetAsync("/total-risk/api/info");

        // Assert
        Assert.AreEqual(System.Net.HttpStatusCode.OK, health.StatusCode);
        Assert.AreEqual("Healthy", await health.Content.ReadAsStringAsync());
        Assert.AreEqual(System.Net.HttpStatusCode.OK, detailed.StatusCode);
        StringAssert.Contains(await detailed.Content.ReadAsStringAsync(), "healthy");
        Assert.AreEqual(System.Net.HttpStatusCode.OK, info.StatusCode);
        StringAssert.Contains(await info.Content.ReadAsStringAsync(), "RMC-TotalRisk API");
    }

    /// <summary>The example endpoint round-trips through compute successfully over HTTP.</summary>
    [TestMethod]
    public async Task Test_ExampleEndpoint_ComputesOverHttp()
    {
        // Arrange
        string exampleJson = await _client!.GetStringAsync("/api/risk-analyses/example");
        var example = TestJson.Deserialize<ComputeRiskAnalysisRequest>(exampleJson)!;

        // Act
        var (response, body) = await PostAsync("/api/risk-analyses/compute", example);

        // Assert
        Assert.IsTrue(response.IsSuccessStatusCode, body);
        var computed = TestJson.Deserialize<ComputeRiskAnalysisResponse>(body)!;
        Assert.IsTrue(computed.Success);
        Assert.AreEqual(3, computed.Results!.Components[0].FailureModes.Count);
    }

    /// <summary>
    /// The expected-annual-damage golden: the engine's total mean reproduces the exact
    /// closed-form integral of the eight-knot damage-frequency curve through the whole HTTP
    /// stack. Tolerances derive from the engine's documented quadrature residuals (1e-5
    /// relative on the mean; value-at-risk carries a 0.1% output-resolution floor).
    /// </summary>
    [TestMethod]
    public async Task Test_ExpectedAnnualDamage_GoldenClosedForm()
    {
        // Arrange
        var request = TestRequests.ExpectedAnnualDamage();

        // Act
        var (response, body) = await PostAsync("/api/risk-analyses/compute", request);

        // Assert
        Assert.IsTrue(response.IsSuccessStatusCode, body);
        var computed = TestJson.Deserialize<ComputeRiskAnalysisResponse>(body)!;
        var total = computed.Results!.Curves.Total.Stats;
        var nonFail = computed.Results.Curves.NonFail.Stats;
        var fail = computed.Results.Curves.Fail.Stats;

        Assert.AreEqual(TestRequests.EadClosedFormMean, total.Mean, TestRequests.EadClosedFormMean * 1e-4);
        Assert.AreEqual(TestRequests.EadClosedFormStandardDeviation, total.StandardDeviation,
            TestRequests.EadClosedFormStandardDeviation * 5e-4);
        Assert.AreEqual(total.Mean, nonFail.Mean, Math.Abs(total.Mean) * 1e-9);
        Assert.AreEqual(0d, fail.TotalProbability, 0d);
        Assert.AreEqual(1d, total.MassBalance, 1e-12);
        Assert.AreEqual(TestRequests.EadClosedFormValueAtRisk, total.ValueAtRisk!.Value,
            TestRequests.EadClosedFormValueAtRisk * 5e-3);
        Assert.AreEqual(TestRequests.EadClosedFormConditionalValueAtRisk, total.ConditionalValueAtRisk!.Value,
            TestRequests.EadClosedFormConditionalValueAtRisk * 5e-3);
    }

    /// <summary>
    /// The transform-equivalence golden: routing a fragility through an exact linear stage
    /// shift (with the response re-keyed to the shifted axis and the consequences bound back
    /// to the raw stage axis at position 0) reproduces the untransformed twin through the whole
    /// HTTP stack. The shift is exact in floating point (see the twin builders), so the two
    /// runs are expected bit-identical; the 1e-12 relative tolerance is headroom, not an
    /// accuracy allowance.
    /// </summary>
    [TestMethod]
    public async Task Test_TransformEquivalenceTwin_MatchesUntransformedOverHttp()
    {
        // Act
        var (baseResponse, baseBody) = await PostAsync("/api/risk-analyses/compute", TestRequests.TransformTwinBaseline());
        var (shiftResponse, shiftBody) = await PostAsync("/api/risk-analyses/compute", TestRequests.TransformTwinShifted());

        // Assert
        Assert.IsTrue(baseResponse.IsSuccessStatusCode, baseBody);
        Assert.IsTrue(shiftResponse.IsSuccessStatusCode, shiftBody);
        var baseline = TestJson.Deserialize<ComputeRiskAnalysisResponse>(baseBody)!.Results!;
        var shifted = TestJson.Deserialize<ComputeRiskAnalysisResponse>(shiftBody)!.Results!;

        var baseTotal = baseline.Curves.Total.Stats;
        var shiftTotal = shifted.Curves.Total.Stats;
        var baseFail = baseline.Curves.Fail.Stats;
        var shiftFail = shifted.Curves.Fail.Stats;
        Assert.AreEqual(baseTotal.Mean, shiftTotal.Mean, Math.Abs(baseTotal.Mean) * 1e-12);
        Assert.AreEqual(baseFail.Mean, shiftFail.Mean, Math.Abs(baseFail.Mean) * 1e-12);
        Assert.AreEqual(baseFail.TotalProbability, shiftFail.TotalProbability,
            Math.Abs(baseFail.TotalProbability) * 1e-12);
        Assert.IsTrue(baseFail.TotalProbability > 0, baseBody);

        // The threaded labels line up along the whole chain, so the transformed run carries no
        // label-continuity warnings.
        Assert.IsFalse(shiftBody.Contains("does not match"), shiftBody);
    }

    /// <summary>
    /// Identical requests produce byte-identical results and provenance blocks (content-based
    /// seeding surfaced through the API; the envelope's timestamp and timing legitimately
    /// differ).
    /// </summary>
    [TestMethod]
    public async Task Test_Compute_Reproducible_ByteIdenticalResults()
    {
        // Act
        var (_, first) = await PostAsync("/api/risk-analyses/compute", TestRequests.DamScreening());
        var (_, second) = await PostAsync("/api/risk-analyses/compute", TestRequests.DamScreening());

        // Assert
        using var firstDocument = JsonDocument.Parse(first);
        using var secondDocument = JsonDocument.Parse(second);
        Assert.AreEqual(
            firstDocument.RootElement.GetProperty("results").GetRawText(),
            secondDocument.RootElement.GetProperty("results").GetRawText());
        Assert.AreEqual(
            firstDocument.RootElement.GetProperty("provenance").GetRawText(),
            secondDocument.RootElement.GetProperty("provenance").GetRawText());
    }

    /// <summary>The HTTP 400 contract: structured issues with codes, echoed values, and paths.</summary>
    [TestMethod]
    public async Task Test_ValidationContract_Structured400()
    {
        // Arrange
        var badOrder = TestRequests.DamScreening();
        badOrder.Components[0].Hazard.ExceedanceProbabilities.Reverse();
        var fullRun = TestRequests.DamScreening();
        fullRun.Options = new RiskAnalysisOptionsDto { EstimateMeanRiskOnly = false };

        // Act
        var (orderResponse, orderBody) = await PostAsync("/api/risk-analyses/compute", badOrder);
        var (meanResponse, meanBody) = await PostAsync("/api/risk-analyses/compute", fullRun);
        var (validateResponse, validateBody) = await PostAsync("/api/risk-analyses/validate", badOrder);

        // Assert
        Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, orderResponse.StatusCode, orderBody);
        StringAssert.Contains(orderBody, "API_TABLE_ORDER");
        StringAssert.Contains(orderBody, "exceedanceProbabilities");
        Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, meanResponse.StatusCode, meanBody);
        StringAssert.Contains(meanBody, "API_MEAN_ONLY_REQUIRED");
        Assert.AreEqual(System.Net.HttpStatusCode.OK, validateResponse.StatusCode, validateBody);
        using var verdict = JsonDocument.Parse(validateBody);
        Assert.IsFalse(verdict.RootElement.GetProperty("isValid").GetBoolean(), validateBody);
    }

    /// <summary>The stateless MCP endpoint lists the registered tools over raw JSON-RPC.</summary>
    [TestMethod]
    public async Task Test_Mcp_ToolsList_ReturnsRegisteredTools()
    {
        // Arrange: both Accept values are REQUIRED by the streamable HTTP transport.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        // Act
        var response = await _client!.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, $"MCP endpoint failed: {body}");
        StringAssert.Contains(body, "run_risk_analysis");
        StringAssert.Contains(body, "validate_risk_analysis");
        StringAssert.Contains(body, "get_metadata");
        StringAssert.Contains(body, "get_example_request");
    }

    /// <summary>
    /// A complete MCP round trip: tools/call run_risk_analysis with the typed request object as
    /// the tool argument computes and returns results (proves the complex-typed parameter binds
    /// through the SDK's schema path).
    /// </summary>
    [TestMethod]
    public async Task Test_Mcp_ToolsCall_RunRiskAnalysis()
    {
        // Arrange
        string arguments = TestJson.Serialize(TestRequests.DamScreening());
        string payload = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"run_risk_analysis\",\"arguments\":{\"request\":" + arguments + "}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        // Act
        var response = await _client!.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, body);
        StringAssert.Contains(body, "totalProbability");
        StringAssert.Contains(body, "adjustedCurves");
        Assert.IsFalse(body.Contains("\"isError\":true"), body);
    }
}
