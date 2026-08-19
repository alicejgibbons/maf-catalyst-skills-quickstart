// Verification sample for diagridio/dotnet-ai PR #63 ("Add skills support").
//
// This mirrors the documented Catalyst quickstart for Microsoft Agent Framework
// (docs.diagrid.io/getting-started/quickstarts/ai-agents/?agentframework=microsoft-dotnet) almost
// line-for-line - same WebApplication/AddDaprAgents/WithCatalyst/IDaprAgentInvoker shape - except:
//   1. It references the PR #63 branch source directly (ProjectReference), not the released
//      Diagrid.AI.Microsoft.AgentFramework NuGet package (1.0.10, which predates Skills).
//   2. The agent is additionally given skills from all three MAF sources (file-based, inline,
//      class-based), mixed via AgentSkillsProviderBuilder, plus a script gated by approval.

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
    .WithCatalyst(
        new DiagridCatalystOptions
        {
            Registry = new RegistryMetadata
            {
                ResourceName = "agent-registry",
            },
        });

var app = builder.Build();

app.MapPost("/run", async (IDaprAgentInvoker invoker, RunRequest req, CancellationToken ct) =>
{
    var agent = invoker.GetAgent("skills-assistant");
    var result = await invoker.RunAgentAsync(agent, req.Prompt, cancellationToken: ct);
    return Results.Ok(new { response = result.Text });
});

await app.RunAsync();

record RunRequest(string Prompt);

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
