using Microsoft.Extensions.Logging.Abstractions;
using RepoPilot.Agent.Capabilities;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Ports;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Runs;
using RepoPilot.Domain.Workspace;
using RepoPilot.Infrastructure.Sandbox;
using RepoPilot.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Sdk;

namespace RepoPilot.IntegrationTests.Sandbox;

/// <summary>
/// FR-022: what may execute comes from committed repository configuration.
/// <para>
/// The load-bearing claim is <em>where</em> the refusal happens. A run that
/// created a container and then decided not to use it would already have handed
/// a model-influenced string to the runtime — so these tests assert that the
/// sandbox is never reached, not merely that the result was a failure.
/// </para>
/// </summary>
[Collection(SandboxCollection.Name)]
public sealed class AllowedCommandTests : IDisposable
{
    private const string FixtureConfig = """
        {
          "slug": "allow-list-fixture",
          "sandbox": {
            "image": "alpine:3",
            "workdir": "/workspace",
            "timeoutSeconds": 60,
            "memoryMegabytes": 256,
            "cpuCount": 1,
            "pidsLimit": 64
          },
          "commands": [
            { "name": "unit", "argv": ["echo", "unit-suite-ran"], "purpose": "verify" }
          ]
        }
        """;

    private readonly string _workingCopy = Directory.CreateTempSubdirectory("repopilot-allowlist-").FullName;
    private readonly RepositoryFixture _fixture;
    private readonly Run _run;

    public AllowedCommandTests()
    {
        _fixture = new RepositoryFixture
        {
            Slug = "allow-list-fixture",
            DisplayName = "Allow-list fixture",
            RootPath = _workingCopy,
            TestConfigJson = FixtureConfig,
        };

        _run = new Run { RepositoryId = _fixture.Id, TaskDescription = "exercise the allow-list" };
    }

    private RunTestsCapability Capability(ISandboxRunner sandbox) => new(
        sandbox,
        new SingleFixtureStore(_fixture),
        new CollectingTestResultStore(),
        new SingleRunStore(_run),
        _ => _workingCopy);

    private CapabilityContext Context() => new(
        _run.Id, _fixture.Id, WorkspaceRoot.Writable(_workingCopy), new RunContextBudget(1_000_000));

    [Fact]
    public async Task ACommandOutsideTheAllowListNeverReachesTheSandbox()
    {
        var spy = new RefusingSandboxRunner();

        var refusal = await Assert.ThrowsAsync<CommandNotAllowedException>(
            () => Capability(spy).InvokeAsync(Context(), """{"command_name":"curl-secrets"}"""));

        Assert.Equal("curl-secrets", refusal.Requested);

        // The point of the test: nothing was created.
        Assert.False(spy.WasCalled);
    }

    [Fact]
    public async Task TheRefusalNamesWhatIsPermitted()
    {
        var refusal = await Assert.ThrowsAsync<CommandNotAllowedException>(
            () => Capability(new RefusingSandboxRunner())
                .InvokeAsync(Context(), """{"command_name":"unit "}"""));

        // Matching is exact — a near-miss is refused, and the message says what
        // would have worked so the model can correct rather than guess.
        Assert.Contains("unit", refusal.Message);
    }

    [Fact]
    public async Task TheCapabilityExposesNoWayToSupplyACommandLine()
    {
        var spy = new RefusingSandboxRunner();

        // Whatever else the arguments carry, only command_name is read. A model
        // that adds "argv" or "shell" gets the allow-listed vector or nothing.
        await Assert.ThrowsAsync<CommandNotAllowedException>(
            () => Capability(spy).InvokeAsync(
                Context(),
                """{"command_name":"rm","argv":["rm","-rf","/"],"shell":"rm -rf /"}"""));

        Assert.False(spy.WasCalled);
    }

    [Fact]
    public async Task AnAllowListedCommandIsExecutedAsTheCommittedVector()
    {
        var recorder = new RecordingSandboxRunner();

        await Capability(recorder).InvokeAsync(Context(), """{"command_name":"unit"}""");

        Assert.NotNull(recorder.Request);
        Assert.Equal(["echo", "unit-suite-ran"], recorder.Request!.Argv);

        // Sandbox settings come from the fixture too, not from the invocation.
        Assert.Equal("alpine:3", recorder.Request.Image);
        Assert.Equal(TimeSpan.FromSeconds(60), recorder.Request.Timeout);
        Assert.Equal(256, recorder.Request.MemoryMegabytes);
    }

    [RequiresDockerFact]
    public async Task AnAllowListedCommandRunsEndToEndAndIsRecorded()
    {
        var results = new CollectingTestResultStore();
        using var runner = new DockerSandboxRunner(NullLogger<DockerSandboxRunner>.Instance);

        var capability = new RunTestsCapability(
            runner, new SingleFixtureStore(_fixture), results, new SingleRunStore(_run), _ => _workingCopy);

        var result = await capability.InvokeAsync(Context(), """{"command_name":"unit"}""");

        Assert.Contains("unit-suite-ran", result.Content);

        var stored = Assert.Single(results.Stored);
        Assert.Equal("unit", stored.CommandName);
        Assert.True(stored.Passed);
        Assert.False(stored.TimedOut);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workingCopy, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private sealed class RefusingSandboxRunner : ISandboxRunner
    {
        public bool WasCalled { get; private set; }

        public Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default)
        {
            WasCalled = true;
            throw new XunitException(
                "The sandbox was reached for a command that should have been refused first.");
        }
    }

    private sealed class RecordingSandboxRunner : ISandboxRunner
    {
        public SandboxRequest? Request { get; private set; }

        public Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(
                new SandboxResult(true, 0, "unit-suite-ran", TimeSpan.FromMilliseconds(12), false));
        }
    }

    private sealed class SingleFixtureStore(RepositoryFixture fixture) : IRepositoryFixtureStore
    {
        public Task<RepositoryFixture?> FindBySlugAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult<RepositoryFixture?>(fixture.Slug == slug ? fixture : null);

        public Task<RepositoryFixture?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<RepositoryFixture?>(fixture.Id == id ? fixture : null);

        public Task<IReadOnlyList<RepositoryFixture>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RepositoryFixture>>([fixture]);

        public Task AddAsync(RepositoryFixture f, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateAsync(RepositoryFixture f, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SingleRunStore(Run run) : IRunStore
    {
        public Task<Run?> FindAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<Run?>(run.Id == id ? run : null);

        public Task<IReadOnlyList<Run>> ListAsync(
            Guid? repositoryId = null, RunStage? stage = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Run>>([run]);

        public Task AddAsync(Run r, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateAsync(Run r, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Run>> ListNonTerminalAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Run>>([]);
    }

    private sealed class CollectingTestResultStore : ITestResultStore
    {
        public List<TestResult> Stored { get; } = [];

        public Task AddAsync(TestResult result, CancellationToken ct = default)
        {
            Stored.Add(result);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TestResult>> ListForRunAsync(Guid runId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TestResult>>([.. Stored]);
    }
}
