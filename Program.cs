// Verification sample for diagridio/dotnet-ai PR #63 ("Add skills support") and PR #67 ("Fix
// workflow dropping AgentRunOptions", issue #66).
//
// This mirrors the documented Catalyst quickstart for Microsoft Agent Framework
// (docs.diagrid.io/getting-started/quickstarts/ai-agents/?agentframework=microsoft-dotnet) almost
// line-for-line - same WebApplication/AddDaprAgents/WithCatalyst/IDaprAgentInvoker shape - except:
//   1. It references the PR #67 branch source directly (ProjectReference), not the released
//      Diagrid.AI.Microsoft.AgentFramework NuGet package (1.0.10, which predates Skills and the
//      AgentRunOptions fix).
//   2. The agent is additionally given skills from all three MAF sources (file-based, inline,
//      class-based), mixed via AgentSkillsProviderBuilder, plus a script gated by approval.
//   3. A fourth context provider, SessionMemoryContextProvider, proves PR #67's fix: that
//      AgentRunOptions.AdditionalProperties supplied to /run actually reach a context provider's
//      InvokingAsync/InvokedAsync callbacks (via AIAgent.CurrentRunContext), and that provider state
//      written to the session during InvokingAsync survives into InvokedAsync - see its comments and
//      the "sessionMemoryProof" field the /run endpoint returns.

using Diagrid.AI.Microsoft.AgentFramework.Abstractions;
using Diagrid.AI.Microsoft.AgentFramework.Catalyst;
using Diagrid.AI.Microsoft.AgentFramework.Hosting;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

var builder = WebApplication.CreateBuilder(args);

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? throw new InvalidOperationException("OPENAI_API_KEY environment variable is required.");

// Demo-only IToolApprovalHandler: logs then auto-approves, purely so the /run endpoint below
// produces a response without a real human in the loop. A production implementation should await
// an actual decision instead - see IToolApprovalHandler's remarks for why that's safe to do here
// (this runs inside a Dapr Workflow *activity*, not the orchestrator).
builder.Services.AddSingleton<IToolApprovalHandler, ConsoleApprovingToolApprovalHandler>();

var widgetCatalogSkillPath = Path.Combine(AppContext.BaseDirectory, "skills", "widget-catalog");

// Demo-only: a singleton the /run endpoint reads after each invocation to prove what
// SessionMemoryContextProvider observed - see the type's own remarks below for why this is PR #67's
// fix in action. Constructed up front so the same instance can be handed to the provider (which
// WithContextProviders takes as a concrete instance, not a DI-resolved factory) and registered for
// the endpoint to resolve.
var runOptionsObserver = new RunOptionsObserver();
builder.Services.AddSingleton(runOptionsObserver);

builder.Services.AddDaprAgents()
    .WithAgent(sp =>
    {
        IChatClient chatClient = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4.1-2025-04-14")
            .AsIChatClient();
        return chatClient.AsAIAgent(
            instructions: "You are a helpful assistant with access to internal reference skills.",
            name: "skills-assistant");
    })
    .WithSkills("skills-assistant", skills => skills
        // File-based: discovered from skills/widget-catalog/SKILL.md (+ references/compatibility.md).
        // A script runner is required by MAF whenever any file-based source is configured, even
        // when (like here) the skill itself defines no scripts.
        .UseFileSkill(widgetCatalogSkillPath, scriptRunner: (_, _, _, _, _) =>
            throw new NotSupportedException("The widget-catalog skill has no scripts."))
        // Inline: defined directly in code, no files involved.
        .UseSkill(new AgentInlineSkill(
            name: "joke-teller",
            description: "Tells a short, work-appropriate joke on request.",
            instructions: "When asked for a joke, tell exactly one short, clean joke. Do not explain it."))
        // Class-based: instructions + an [AgentSkillScript]-attributed method live together on one
        // C# class. UseScriptApproval() means run_skill_script for THIS script (and any other
        // skill's scripts) is gated by IToolApprovalHandler before it actually runs.
        .UseSkill(new GreetingSkill())
        .UseScriptApproval())
    // A second, non-skills AIContextProvider on the same agent - composes fine alongside WithSkills
    // (both just append to the agent's context-provider list). See SessionMemoryContextProvider.
    .WithContextProviders("skills-assistant", new SessionMemoryContextProvider(runOptionsObserver))
    .WithCatalyst(
        new DiagridCatalystOptions
        {
            Registry = new RegistryMetadata
            {
                ResourceName = "agent-registry",
            },
        });

var app = builder.Build();

app.MapPost("/run", async (IDaprAgentInvoker invoker, RunOptionsObserver observer, RunRequest req, CancellationToken ct) =>
{
    observer.Reset();

    var agent = invoker.GetAgent("skills-assistant");

    // Only set when the caller supplies a SessionId - lets test.http exercise both the "no options"
    // path (existing behavior, unaffected by PR #67) and the "with options" path that PR #67 fixes.
    AgentRunOptions? options = req.SessionId is null
        ? null
        : new AgentRunOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["sessionId"] = req.SessionId }
        };

    var result = await invoker.RunAgentAsync(agent, req.Prompt, options: options, cancellationToken: ct);

    return Results.Ok(new
    {
        response = result.Text,
        // Proof of PR #67 (issue #66): with the fix, all three should equal req.SessionId whenever
        // it was supplied. Before the fix, all three would be null - AgentRunOptions never reached
        // ResolveAgentContextActivity/CompleteAgentContextActivity, and each activity got its own
        // throwaway session, so state written in one was never visible in the other.
        sessionMemoryProof = new
        {
            sessionIdSeenDuringInvoking = observer.InvokingSessionId,
            sessionIdSeenDuringInvoked = observer.InvokedSessionId,
            sessionIdReadBackFromSessionStateBag = observer.StateBagSessionIdAtInvoked
        }
    });
});

await app.RunAsync();

record RunRequest(string Prompt, string? SessionId = null);

/// <summary>
/// Demo-only sink for what <see cref="SessionMemoryContextProvider"/> observed during the most
/// recent run - a real app would have no need for this; it exists purely so the /run endpoint above
/// can report proof of PR #67's fix back to the caller. Registered as a singleton and reset at the
/// start of each /run call; fine for this single-request-at-a-time demo, not meant as a pattern for
/// concurrent use.
/// </summary>
sealed class RunOptionsObserver
{
    public string? InvokingSessionId { get; private set; }
    public string? InvokedSessionId { get; private set; }
    public string? StateBagSessionIdAtInvoked { get; private set; }

    public void Reset() => (InvokingSessionId, InvokedSessionId, StateBagSessionIdAtInvoked) = (null, null, null);

    public void RecordInvoking(string? sessionId) => InvokingSessionId = sessionId;

    public void RecordInvoked(string? sessionId, string? stateBagSessionId) =>
        (InvokedSessionId, StateBagSessionIdAtInvoked) = (sessionId, stateBagSessionId);
}

/// <summary>
/// Verification-only <see cref="AIContextProvider"/> for diagridio/dotnet-ai PR #67 (issue #66:
/// "Dapr workflow drops AgentRunOptions.AdditionalProperties before AI context providers run").
/// </summary>
/// <remarks>
/// <para>
/// Before the fix, <c>AIAgent.CurrentRunContext</c> was never established inside
/// <c>ResolveAgentContextActivity</c>/<c>CompleteAgentContextActivity</c>, so a provider's
/// <c>InvokingAsync</c>/<c>InvokedAsync</c> had no way to see the run's <see cref="AgentRunOptions"/>
/// (e.g. the "sessionId" set in this app's /run endpoint) - the same gap an external memory provider
/// hit in the linked issue. This provider reads <c>AIAgent.CurrentRunContext</c> in both callbacks and
/// records what it saw via <see cref="RunOptionsObserver"/>, so /run's response can show it directly.
/// </para>
/// <para>
/// It also writes the session id into <c>InvokingContext.Session.StateBag</c> during
/// <c>ProvideAIContextAsync</c> and reads it back during <c>StoreAIContextAsync</c> - proving the fix's
/// other half, that resolution and completion now share one logical <c>AgentSession</c> rather than
/// each activity creating its own throwaway session.
/// </para>
/// </remarks>
sealed class SessionMemoryContextProvider(RunOptionsObserver observer) : AIContextProvider
{
    private const string StateBagKey = "sessionId";

    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var sessionId = AIAgent.CurrentRunContext?.RunOptions?.AdditionalProperties?[StateBagKey] as string;
        observer.RecordInvoking(sessionId);

        context.Session!.StateBag.SetValue(StateBagKey, sessionId);

        return ValueTask.FromResult(new AIContext());
    }

    protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        var sessionId = AIAgent.CurrentRunContext?.RunOptions?.AdditionalProperties?[StateBagKey] as string;
        context.Session!.StateBag.TryGetValue<string>(StateBagKey, out var stateBagSessionId);
        observer.RecordInvoked(sessionId, stateBagSessionId);

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Class-based skill: rewrites a casual greeting as a formal one via an attributed script.
/// </summary>
sealed class GreetingSkill : AgentClassSkill<GreetingSkill>
{
    public override AgentSkillFrontmatter Frontmatter { get; } = new(
        name: "greeting-formalizer",
        description: "Rewrites a casual greeting as a formal, business-appropriate one.");

    protected override string Instructions =>
        "When asked to formalize a greeting, run the formalize-greeting script with the casual text.";

    [AgentSkillScript("formalize-greeting")]
    public static string Formalize(string casualGreeting) =>
        $"Good day. {casualGreeting.Trim().TrimEnd('!', '.')}. I hope this message finds you well.";
}

/// <summary>
/// Demo-only <see cref="IToolApprovalHandler"/> - see the registration comment above for what a
/// real implementation should do instead.
/// </summary>
sealed class ConsoleApprovingToolApprovalHandler : IToolApprovalHandler
{
    public Task<ToolApprovalDecision> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($">>> Approving script call '{request.ToolName}' for agent '{request.AgentName}' (demo auto-approval)");
        return Task.FromResult(ToolApprovalDecision.Approve("Auto-approved by skills-quickstart demo."));
    }
}
