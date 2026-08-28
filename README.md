# Microsoft Agent Framework + Diagrid Catalyst — Skills Quickstart

This is a verification sample built to confirm that [diagridio/dotnet-ai PR #63](https://github.com/diagridio/dotnet-ai/pull/63)
("Add skills support") and [PR #67](https://github.com/diagridio/dotnet-ai/pull/67) ("Fix workflow
dropping AgentRunOptions", [issue #66](https://github.com/diagridio/dotnet-ai/issues/66)) actually
deliver what they claim for Microsoft Agent Framework (MAF) agents running on
`Diagrid.AI.Microsoft.AgentFramework`, using the real
[Catalyst quickstart](https://docs.diagrid.io/getting-started/quickstarts/ai-agents/?agentframework=microsoft-dotnet)
pattern end-to-end — not just the PRs' own bundled examples/tests.

It mirrors [`catalyst-quickstarts/agents/microsoft-dotnet`](https://github.com/diagridio/catalyst-quickstarts/tree/main/agents/microsoft-dotnet)
almost line-for-line (`WebApplication` + `AddDaprAgents().WithAgent(...)` + `.WithCatalyst(...)` +
`IDaprAgentInvoker`/`/run` endpoint), with one deliberate substitution and two additions:

- **Substitution:** references the PR #67 branch source directly via `ProjectReference` (see
  `skills-quickstart.csproj`), not the released `Diagrid.AI.Microsoft.AgentFramework` NuGet package
  (1.0.10, which predates both Skills support and the AgentRunOptions fix). You'll need a local clone
  of `diagridio/dotnet-ai` as a sibling directory (`../dotnet-ai`), checked out to PR #67's branch
  (`invokerdrop-1`, which already includes PR #63's skills support) for this to build.
- **Addition 1:** the agent (`skills-assistant`) is given skills from all three sources MAF supports,
  mixed via `AgentSkillsProviderBuilder`:
  - **File-based** — `skills/widget-catalog/SKILL.md` (+ `references/compatibility.md`), discovered
    via `UseFileSkill(...)`. Deliberately invented facts (a fictional "Aurora-9 Connector" SKU and
    compatibility list) so a correct answer can only come from the loaded skill content, not the
    model's own knowledge.
  - **Inline** — a `joke-teller` skill defined directly in code with `AgentInlineSkill`.
  - **Class-based** — a `greeting-formalizer` skill (`GreetingSkill : AgentClassSkill<GreetingSkill>`)
    whose script is a plain C# method attributed with `[AgentSkillScript]`, gated by
    `UseScriptApproval()` and a demo `IToolApprovalHandler`.
- **Addition 2:** a fourth, non-skills `AIContextProvider` — `SessionMemoryContextProvider` — attached
  via `WithContextProviders(...)` alongside `WithSkills(...)`. It exists purely to verify PR #67: it
  reads `AIAgent.CurrentRunContext.RunOptions.AdditionalProperties` in both its `InvokingAsync` and
  `InvokedAsync` callbacks, and round-trips a value through `Session.StateBag` between them. The
  `/run` endpoint accepts an optional `sessionId`, passes it as `AgentRunOptions.AdditionalProperties`,
  and echoes back what the provider actually observed as `sessionMemoryProof` in its response — see
  requests 4 and 5 in [`test.http`](./test.http).

## What this proves

Running the five prompts in [`test.http`](./test.http) against a real Diagrid Catalyst project
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
5. **PR #67:** request 4 in `test.http` (`sessionId: "demo-session-1"`) returns
   `sessionMemoryProof.sessionIdSeenDuringInvoking`, `...SeenDuringInvoked`, and
   `...ReadBackFromSessionStateBag` all equal to `"demo-session-1"` — proving `AgentRunOptions`
   supplied to `/run` reaches `SessionMemoryContextProvider` in both
   `ResolveAgentContextActivity` and `CompleteAgentContextActivity` via `AIAgent.CurrentRunContext`,
   and that both activities share one logical `AgentSession` (state written in the first is read back
   in the second). Request 5 (no `sessionId`) returns all three as `null`, confirming the endpoint
   only sets `AgentRunOptions` when asked to.

## Prerequisites

- [Diagrid Catalyst account](https://catalyst.diagrid.io/) + [Diagrid CLI](https://docs.diagrid.io/getting-started/install-cli/)
- .NET 10 SDK
- An OpenAI API key
- A local clone of `diagridio/dotnet-ai` checked out to PR #67's branch (`invokerdrop-1`, which
  already includes PR #63's skills support), as a sibling directory:
  ```bash
  git clone https://github.com/diagridio/dotnet-ai.git ../dotnet-ai
  cd ../dotnet-ai && gh pr checkout 67
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

From another terminal, trigger any of the five prompts in [`test.http`](./test.http), e.g.:

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
