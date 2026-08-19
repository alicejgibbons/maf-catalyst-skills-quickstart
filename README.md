# Microsoft Agent Framework + Diagrid Catalyst — Skills Quickstart

This is a verification sample built to confirm that [diagridio/dotnet-ai PR #63](https://github.com/diagridio/dotnet-ai/pull/63)
("Add skills support") actually delivers Skills support for Microsoft Agent Framework (MAF) agents
running on `Diagrid.AI.Microsoft.AgentFramework`, using the real
[Catalyst quickstart](https://docs.diagrid.io/getting-started/quickstarts/ai-agents/?agentframework=microsoft-dotnet)
pattern end-to-end — not just the PR's own bundled example.

It mirrors [`catalyst-quickstarts/agents/microsoft-dotnet`](https://github.com/diagridio/catalyst-quickstarts/tree/main/agents/microsoft-dotnet)
almost line-for-line (`WebApplication` + `AddDaprAgents().WithAgent(...)` + `.WithCatalyst(...)` +
`IDaprAgentInvoker`/`/run` endpoint), with one deliberate substitution and one addition:

- **Substitution:** references the PR #63 branch source directly via `ProjectReference` (see
  `skills-quickstart.csproj`), not the released `Diagrid.AI.Microsoft.AgentFramework` NuGet package
  (1.0.10, which predates Skills support). You'll need a local clone of the `skills` branch of
  `diagridio/dotnet-ai` as a sibling directory (`../dotnet-ai`) for this to build.
- **Addition:** the agent (`skills-assistant`) is given skills from all three sources MAF supports,
  mixed via `AgentSkillsProviderBuilder`:
  - **File-based** — `skills/widget-catalog/SKILL.md` (+ `references/compatibility.md`), discovered
    via `UseFileSkill(...)`. Deliberately invented facts (a fictional "Aurora-9 Connector" SKU and
    compatibility list) so a correct answer can only come from the loaded skill content, not the
    model's own knowledge.
  - **Inline** — a `joke-teller` skill defined directly in code with `AgentInlineSkill`.
  - **Class-based** — a `greeting-formalizer` skill (`GreetingSkill : AgentClassSkill<GreetingSkill>`)
    whose script is a plain C# method attributed with `[AgentSkillScript]`, gated by
    `UseScriptApproval()` and a demo `IToolApprovalHandler`.

## What this proves

Running the three prompts in [`test.http`](./test.http) against a real Diagrid Catalyst project
(`diagrid project create` → `diagrid agent create` → `diagrid dev run`) confirmed, via the durable
Dapr Workflow history (`diagrid workflow get`):

1. `ResolveAgentContextActivity` injects an `<available_skills>` catalog (names/descriptions only)
   into the agent's system prompt and registers `load_skill`/`read_skill_resource`/`run_skill_script`
   as tools — once per run, as its own checkpointed activity.
2. The LLM calls `load_skill` then `read_skill_resource` on demand (each a separate
   `ExecuteToolActivity`) to answer the widget-catalog question — and the answer contains the
   invented SKU/compatibility facts verbatim, proving the loaded content actually reached the model.
3. The class-based skill's script runs via `run_skill_script`, gated by `IToolApprovalHandler`
   (logged and auto-approved by the demo handler) before executing.
4. All of the above runs as genuine, separately-checkpointed Dapr Workflow activities on a real
   Catalyst project — not just MAF's in-process agent loop.

## Prerequisites

- [Diagrid Catalyst account](https://catalyst.diagrid.io/) + [Diagrid CLI](https://docs.diagrid.io/getting-started/install-cli/)
- .NET 10 SDK
- An OpenAI API key
- A local clone of `diagridio/dotnet-ai` on the `skills` branch (PR #63), as a sibling directory:
  ```bash
  git clone https://github.com/diagridio/dotnet-ai.git ../dotnet-ai
  cd ../dotnet-ai && gh pr checkout 63
  ```

## Running it

```bash
export OPENAI_API_KEY="your-openai-api-key"
dotnet build

diagrid login
diagrid project create dotnet-skills-quickstart --enable-managed-workflow --deploy-managed-kv --deploy-managed-pubsub --wait --use
diagrid agent create skills-assistant --wait
diagrid dev run -f dev-dotnet-agent.yaml --approve
```

From another terminal, trigger any of the three prompts in [`test.http`](./test.http), e.g.:

```bash
curl -X POST http://localhost:5060/run \
  -H "Content-Type: application/json" \
  -d '{"prompt": "What is the SKU for the Aurora-9 Connector, and what is it compatible with per the compatibility notes?"}'
```

Inspect the durable workflow history for a given run:

```bash
diagrid workflow list
diagrid workflow get <workflow-id> --id skills-assistant
```

## Clean up

```bash
diagrid project delete dotnet-skills-quickstart
```
