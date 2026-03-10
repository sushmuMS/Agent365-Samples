# PlatformAgent — Leadership Design Review

> **Document Purpose**: Executive summary of the PlatformAgent architecture for leadership review prior to implementation.
> **Status**: Design complete — implementation not yet started.
> **Date**: March 2026

---

## The Problem We Are Solving

Every time an agent needs to read email, search the web, analyze sentiment, post to Teams, or read a SharePoint list — the development team rebuilds the same integrations from scratch. Each agent independently wires up authentication, tool connections, and M365 platform access. This creates:

- **Duplicated engineering effort** across every agent project
- **Inconsistent behavior** in how platform capabilities are accessed
- **Slow agent delivery** — teams spend time on plumbing instead of business logic
- **Repeated clarification tax** — long-running tasks ask users the same setup questions every time they run, with no memory of prior answers

---

## The Solution: PlatformAgent

PlatformAgent is a **shared service agent** that centralizes all M365 platform capabilities into a single, reusable AI agent. Any specialized agent (Sales, HR, Finance, etc.) delegates platform tasks to PlatformAgent using natural language and receives structured results back.

**One deployment. All agents benefit.**

```
┌────────────────────────────────────────────────────────────────┐
│              Your Specialized Agents (e.g., SalesAgent)        │
│                                                                │
│  "Analyze campaign emails, log sentiment, send weekly report"  │
│                           │ delegate                           │
└───────────────────────────┼────────────────────────────────────┘
                            │
                            ▼
┌────────────────────────────────────────────────────────────────┐
│                      PlatformAgent                             │
│                                                                │
│   Email  │  Sentiment  │  SharePoint  │  Teams  │  Calendar    │
│          │  Analysis   │  Lists       │         │  Web Search  │
│                                                                │
│          Backed by MCP Platform (11 capability servers)        │
└────────────────────────────────────────────────────────────────┘
```

---

## Hero Scenario: Campaign Efficacy Tracking

An enterprise deploys a Sales Agent to automate campaign performance tracking. The Sales Agent delegates all M365-related work to PlatformAgent using a single natural language instruction.

### The Task

> *"Analyze the efficacy of the campaign over the course of the next 4 weeks by looking at responses and log a market sentiment rating on a scale of 1–5 in a SharePoint list."*

### What Happens — First Call

PlatformAgent does not have the context it needs to run this task repeatedly and correctly. It asks four targeted clarification questions:

| Question | Example Answer |
|---|---|
| How should emails be identified? | Emails with "Campaign results" in the subject |
| Who receives the weekly summary? | salesmanager@contoso.com |
| Which SharePoint list logs the ratings? | https://contoso.sharepoint.com/sites/sales/Lists/CampaignResults |
| When should the weekly summary be sent? | Every Friday at 5:00 AM for 4 weeks |

Once answered, PlatformAgent:
1. **Saves these answers** to persistent task memory (keyed to the calling agent + task type)
2. **Executes Week 1** immediately — reads emails, scores sentiment 4/5, logs to SharePoint, sends the weekly summary email
3. **Returns structured results** to the Sales Agent

### What Happens — Every Subsequent Call

When the weekly scheduler fires (or the Sales Agent calls again):

- PlatformAgent **loads the saved memory** in milliseconds
- **No clarification questions** — executes directly
- Returns the same structured result: week number, sentiment rating, email sent confirmation

**The user is never asked the same question twice.**

---

## Platform Capabilities

PlatformAgent connects to **11 capability servers** on the MCP Platform:

| Capability Area | What It Handles |
|---|---|
| **Email** | Read inbox, filter by sender/subject, send, reply |
| **Summarization** | Generate summaries of text, email threads, documents |
| **Sentiment Analysis** | Classify tone (positive / negative / neutral) with confidence score |
| **SharePoint Lists** | Read and write list entries |
| **Calendar** | Check availability, create events |
| **Microsoft Teams** | Post messages, read channels |
| **Web Search** | External research and enrichment |
| **Knowledge Retrieval** | Search internal knowledge bases |
| **User Profile** | Resolve current user identity and context |
| **Task Memory** | Persist and load resolved task instructions across calls |
| **Utilities** | Formatting, parsing, date/time operations |

---

## Composability: What PlatformAgent Does NOT Do

PlatformAgent deliberately **does not** handle domain-specific actions. This is a design choice, not a limitation.

Actions like Dataverse writes, CRM record updates, or custom ERP integrations remain with the calling agent. PlatformAgent completes everything it can, then hands off the rest with pre-computed values — so the calling agent does not repeat any work.

### Example: Mixed Task with Handoff

**Calling agent sends:**
> *"Read emails from xyz@company.com, analyze sentiment, send a summary, and log to Dataverse."*

**PlatformAgent executes and returns:**

```
Status: partial_handoff

Completed:
  ✓ Read 5 emails from xyz@company.com
  ✓ Sentiment: Negative (81% confidence)
  ✓ Summary email sent to salesmanager@contoso.com

Handed off to caller:
  → Log to Dataverse
    Parameters: sentiment="negative", confidence=0.81, emailCount=5, summary="..."
    Reason: Dataverse is not available to PlatformAgent
```

**The calling agent then executes the Dataverse step using the pre-computed values.** No duplication. No re-processing.

This pattern works for pre-actions and post-actions too — the calling agent can run CRM lookups before calling PlatformAgent, and execute Dataverse writes after. PlatformAgent owns the M365 layer end-to-end; specialized agents own their domain layer.

---

## Key Design Decisions

| Decision | Rationale |
|---|---|
| **Agentic-only access** | PlatformAgent never talks directly to users. It is a service-to-service component. Only authenticated agents can call it. |
| **Stateless per request** | Each call from a calling agent is independent. Task memory is stored in the MCP persistence layer, not in-process. This enables horizontal scaling and scheduled triggers. |
| **Structured JSON responses** | Every response follows a fixed schema (`status`, `results`, `actionsTaken`, `remainingInstructions`). Calling agents can reliably parse and act on the output without custom parsing logic. |
| **Missing capability = handoff, not error** | If PlatformAgent cannot complete a step, it hands it off cleanly with resolved parameters. The calling agent receives a `partial_handoff` status, not a failure. |
| **Task memory is per-caller, per-task-type** | Memory is isolated by calling agent identity and task type. Two different agents running the same task type have separate memories. |
| **Tool surface is curated** | 11 of 20+ available MCP servers are enabled initially. The rest can be activated per use case. This keeps the agent focused and reduces LLM context overhead. |

---

## End-to-End Scenarios

### Scenario A — Campaign Analysis, First Run (Clarification + Execution)
**Input**: "Analyze campaign efficacy for 4 weeks, log 1–5 sentiment to SharePoint"
**Outcome**: Clarification questions returned → user answers → memory saved → Week 1 analysis complete, SharePoint entry created, summary email sent → `status: completed, taskMemoryStatus: created`

### Scenario B — Campaign Analysis, Week 2+ (Memory Loaded)
**Input**: "Run the weekly campaign analysis"
**Outcome**: Memory loaded silently → Week 2 analysis runs → SharePoint entry created → summary email sent → `status: completed, taskMemoryStatus: loaded` — no questions asked

### Scenario C — Mixed Platform + Domain Task (Partial Handoff)
**Input**: "Read emails from xyz, analyze sentiment, send summary, log to Dataverse"
**Outcome**: Email read + sentiment analysis + summary email completed → Dataverse step handed back with pre-computed parameters → `status: partial_handoff` — calling agent finishes with its own tool

### Scenario D — Simple One-Shot Task (No Memory, No Handoff)
**Input**: "Read the latest email from xyz@company.com and return the sentiment"
**Outcome**: Email read → sentiment classified → immediate result returned → `status: completed, taskMemoryStatus: not_applicable` — no clarification, no handoff

---

## Implementation Roadmap

### P1 — Core Platform Agent (Full Capability)

| Phase | Deliverable | Gate |
|---|---|---|
| **Phase 1** — Project Scaffold | Compilable project, health check endpoint live | `dotnet run` → `/api/health` returns 200 |
| **Phase 2** — MCP Tool Loading | All 11 MCP servers load, tools visible in telemetry | Tools listed in startup logs, no errors |
| **Phase 3** — Core AI Behavior | One-shot tasks work end-to-end | Scenarios A (one-shot) and D pass via Playground UI |
| **Phase 4** — Task Personalization | Hero scenario works, memory persists across calls | Scenario A + B pass; memory visible in MCP server |

P1 delivers a **fully functional, production-ready PlatformAgent** with task memory. Calling agents can be built against it immediately after Phase 3.

### P2 — Partial Handoff Contract (Composability Enhancement)

| Phase | Deliverable | Gate |
|---|---|---|
| **Phase 5** — Partial Execution | `remainingInstructions` returned for unhandled steps | Scenario C passes — Dataverse step handed back with correct values |

P2 is **independent of calling agent development**. Calling agents written against P1 can be upgraded to consume `remainingInstructions` when this phase ships without breaking changes.

### Prerequisites Before Phase 1 Starts

| Item | Owner |
|---|---|
| Azure AD App Registration for PlatformAgent | Platform team |
| Azure OpenAI endpoint + deployment key | Platform team |
| MCP Platform running locally (`localhost:52857`) | Developer |
| Development bearer token for local testing | Developer |
| Confirm actual tool names on `mcp_TaskPersonalizationServer` | Developer |

---

## Architecture Summary

```
Design Time:
  Developer writes AgentInstructions (system prompt)
  → Configures ToolingManifest.json (11 MCP servers)
  → Registers DI services in Program.cs
  → PlatformAgent constructor registers agentic-only message handler

Runtime — per request:
  CallingAgent POST /api/messages (Bearer agenticToken)
  → JWT validation (Entra ID)
  → OpenTelemetry span started, baggage propagated
  → MCP tools loaded (or served from cache on repeat calls)
  → LLM (gpt-4o) receives: [system: AgentInstructions] + [user: task text]
  → LLM calls mcp_TaskPersonalizationServer.GetTaskMemory first
      ├─ Memory found → execute directly
      └─ Memory missing → return awaiting_clarification questions
  → (After clarification) LLM calls capability tools in sequence
  → mcp_TaskPersonalizationServer.SaveTaskMemory (if new task)
  → Structured JSON response streamed back to CallingAgent
```

---

## Interoperability Considerations: A2A Protocol

The current design uses **Microsoft's Bot Framework Activity protocol** for agent-to-agent communication — a proprietary transport layer within the Microsoft 365 Agents SDK. This is the right choice for the immediate use case where all calling agents (SalesAgent, HRAgent, FinanceAgent) are built on the same SDK.

However, an open industry standard for agent-to-agent communication has emerged: the **[Agent2Agent (A2A) Protocol](https://a2a-protocol.org/latest/)**, a Google-led specification now gaining cross-vendor adoption. Understanding the difference is relevant for long-term platform strategy.

### What the A2A Protocol Offers

A2A defines a vendor-neutral way for any two AI agents — regardless of which framework built them — to discover each other's capabilities and exchange tasks over standard HTTP:

| Concept | A2A Protocol | Current Design (MS Agents SDK) |
|---|---|---|
| **Agent Discovery** | `/.well-known/agent.json` Agent Card declares capabilities publicly | Endpoint known at configuration time; no self-description |
| **Authentication** | Open OAuth 2.0 / OIDC | Microsoft Entra ID JWT (Microsoft-specific) |
| **Message Format** | JSON-RPC `tasks/send` (open standard) | Bot Framework Activity JSON (Microsoft proprietary) |
| **Streaming** | Server-Sent Events (SSE) | Bot Framework streaming response |
| **Caller Compatibility** | Any A2A-compliant agent (Google ADK, CrewAI, LangChain, etc.) | Microsoft 365 Agents SDK agents only |

### Current Design Limitation

PlatformAgent, as designed, **cannot be called by non-Microsoft agents**. A Google ADK agent, a CrewAI workflow, or any third-party system that implements A2A would not be able to invoke PlatformAgent without a custom adapter.

### Recommended Decision Point

| Option | Description | When to Choose |
|---|---|---|
| **Option 1 — Stay on MS Agents SDK** (current plan) | Single `/api/messages` endpoint using Bot Framework Activity. Simpler, fully SDK-supported, no additional work. | All calling agents will always be Microsoft-stack agents. |
| **Option 2 — Add A2A Compliance** | Expose a second endpoint (`/a2a`) implementing the A2A task interface and publish an Agent Card at `/.well-known/agent.json`. Internal implementation unchanged — only the transport layer is added. | Cross-vendor agent interoperability is a requirement now or on the roadmap. |

> **Recommendation**: Proceed with Option 1 for the P1 delivery. Revisit A2A as a P3 work item if the platform needs to accept calls from non-Microsoft agents. The internal architecture (stateless request handling, structured JSON responses, MCP tool execution) maps cleanly to A2A's task model and would not require a redesign — only a new inbound adapter.

---

## Critical Comparison: PlatformAgent vs Skills-Based Orchestration

This section critically examines an alternative architecture: replacing PlatformAgent with a **Skills layer**, where platform capabilities are exposed as discrete, callable functions and the **calling agent's own LLM** decides which to invoke and in what order.

### What "Skills" Means Here

A Skills architecture exposes each platform capability as a typed, deterministic endpoint — no LLM inside the platform layer:

```
Skills Architecture:
  CallingAgent (LLM) → EmailSkill.SearchEmails(filter, count)
  CallingAgent (LLM) → SentimentSkill.Analyze(emailBodies)
  CallingAgent (LLM) → SharePointSkill.WriteListItem(url, data)
  CallingAgent (LLM) → EmailSkill.Send(to, subject, body)

PlatformAgent Architecture:
  CallingAgent (LLM) → "Analyze campaign emails, log sentiment, send report"
                              ↓
                       PlatformAgent (LLM) → MCP tools in sequence
```

The fundamental difference: **where does the orchestration intelligence live?**

---

### Head-to-Head Comparison

| Dimension | PlatformAgent (LLM inside) | Skills (LLM at caller only) | Winner |
|---|---|---|---|
| **LLM inference cost** | 2× per request — calling agent LLM + platform LLM | 1× per request — calling agent LLM only | Skills |
| **Integration effort per new team** | Send natural language — zero API knowledge required | Must register skills, learn each skill's parameter schema | PlatformAgent |
| **Task memory / personalization** | Centralized — one clarification loop, memory persists for all callers | Must be rebuilt in every calling agent, or left out | PlatformAgent |
| **Debuggability** | Two LLM chains — wrong tool calls are invisible to the caller | Single LLM chain — all decisions visible in one trace | Skills |
| **Behavior consistency** | Same LLM, same system prompt, same results across all callers | Each calling agent's LLM independently interprets skill selection — may vary | PlatformAgent |
| **Adding a new capability** | Add a MCP server — all callers benefit automatically, zero changes to callers | Every calling agent must register the new skill and update its system prompt | PlatformAgent |
| **Failure surface** | LLM hallucination can occur at the platform layer, invisible to the caller | LLM failures are at the calling agent layer — directly observable and recoverable | Skills |
| **Unit testability** | Requires end-to-end LLM + tool calls — hard to unit test | Each skill is a deterministic function — trivial to unit test and mock | Skills |
| **Latency (simple 1-step task)** | Overhead: caller network hop + platform LLM inference + MCP call | Direct: caller LLM → skill call | Skills |
| **Latency (complex 5-step task)** | One network round trip — platform LLM handles all 5 steps internally | Five sequential network hops — caller must make 5 skill calls | PlatformAgent |
| **Partial handoff** | Built in — LLM identifies what it cannot handle and returns remainingInstructions | Natural — caller simply doesn't call a skill it doesn't have | Tie |
| **Platform evolution isolation** | Callers are shielded from MCP changes — platform absorbs them | MCP changes may require skill interface updates that propagate to all callers | PlatformAgent |
| **Calling agent complexity** | Low — delegate and interpret JSON | High — calling agent must orchestrate all skill calls | PlatformAgent |

---

### Where Each Architecture Wins Decisively

#### PlatformAgent is clearly better when:

1. **The task personalization / memory pattern is core to the value proposition.** The clarification loop, memory persistence, and "no re-clarification on subsequent calls" is genuinely complex logic. Rebuilding this in every calling agent (SalesAgent, HRAgent, FinanceAgent) would triple the development cost and guarantee inconsistency across teams.

2. **New calling agent teams need minimal integration cost.** A Skills architecture requires every calling agent LLM to know which skills exist, when to call them, and how to compose them. PlatformAgent requires only: "send a natural language task, parse structured JSON." This difference is significant at enterprise scale with many agent teams.

3. **Tasks are multi-step and ambiguous.** "Analyze campaign emails for 4 weeks, rate sentiment 1–5, log to SharePoint, send weekly summary" — this is a 4-step task with implicit ordering. PlatformAgent's LLM handles decomposition once. Skills would require 4 separate skill calls with the caller managing sequencing, error handling between steps, and partial completion state.

4. **Platform capability surface is expected to grow.** When a new MCP server is added to PlatformAgent, all calling agents automatically gain access. In a Skills architecture, the new skill must be registered in every calling agent's configuration and system prompt.

#### Skills are clearly better when:

1. **Tasks are simple, direct, and predictable.** Reading one email and returning sentiment is two operations — there is no value in routing through a second LLM. The PlatformAgent overhead (LLM inference cost, latency, extra network hop) is pure waste for single-step tasks.

2. **LLM inference cost is a primary constraint.** Every PlatformAgent call costs two LLM inferences. At volume, this is a meaningful cost multiplier. Skills eliminate the platform-layer LLM cost entirely.

3. **Calling agents need precise, deterministic control.** Financial agents, compliance workflows, and audit-sensitive processes benefit from explicit, traceable skill calls rather than LLM-mediated tool selection. "I called SentimentSkill.Analyze(v2)" is more auditable than "PlatformAgent decided to call mcp_sentiment_tool."

4. **Engineering teams prioritize testability and debuggability.** A Skills architecture is vastly easier to test. Each skill function has a typed interface, deterministic behavior, and can be mocked. End-to-end LLM chain testing is expensive and non-deterministic.

---

### The Critical Weakness of PlatformAgent

**There is an LLM in the middle of every request where calling agents cannot see or control what it does.**

If PlatformAgent's LLM:
- Calls the wrong MCP tool
- Mis-sequences operations (sends email before writing SharePoint entry)
- Generates a hallucinated `taskMemoryKey` that doesn't match what was saved
- Returns malformed JSON that breaks the caller's parser

...the calling agent receives a wrong or failed result with no visibility into which step failed or why. The caller cannot retry a specific tool call; it can only retry the entire request or give up. This is the most significant architectural risk of the PlatformAgent design.

Skills fail visibly at the calling agent layer. PlatformAgent fails silently inside the platform layer.

---

### The Critical Weakness of Skills

**The task memory / personalization pattern becomes every calling agent team's problem.**

The clarification loop (ask 4 questions → collect answers → persist → load on next call) is non-trivial logic. If this lives in every calling agent:
- Every team implements it independently — inconsistent user experience
- Bugs in the memory layer are replicated across teams
- A centralized fix requires coordinated updates across all calling agents
- New teams building their first agent face this complexity from day one

Without centralized task memory, scheduled/recurring tasks will re-ask the user the same setup questions every single run — which is the exact problem the hero scenario was designed to eliminate.

---

### Hybrid Recommendation

Neither architecture is unconditionally correct. The right answer depends on task complexity:

```
Task Type                           Recommended Architecture
─────────────────────────────────────────────────────────────
Simple, direct, single-step         Skills (lower cost, lower latency, auditable)
  e.g., "read latest email"
  e.g., "check calendar availability"

Multi-step with memory required     PlatformAgent (centralized orchestration + memory)
  e.g., "weekly campaign analysis"
  e.g., "recurring sentiment reports"

Multi-step, one-off, no memory      Either — PlatformAgent simpler to call,
  e.g., "summarize these 5 emails    Skills cheaper and more auditable
         and send to manager"
```

**A pragmatic hybrid path:**

> Deploy PlatformAgent as designed for the hero scenario. Expose a subset of its capabilities as typed Skills (a thin wrapper over the same MCP tools) for calling agents that need direct, low-cost, auditable access. Both interfaces call the same underlying MCP platform — the choice of interface is made per task type.

This preserves the centralized task memory benefit while giving high-volume, simple-task callers a direct Skills path without the LLM overhead.

---

### Systemic Risks of Skills Architecture at Enterprise Scale

The six points below are enterprise-scale failure modes that emerge when orchestration intelligence is distributed across calling agents rather than centralized. Each represents a compounding problem that gets worse as the number of calling agents grows.

---

#### 1. Policy Drift Across Agents

In a Skills architecture, each calling agent team independently decides how to use the same skills. There is no enforcement mechanism.

**What drift looks like in practice:**

| Agent | Sentiment threshold | Email filter strategy | Sentiment scale | Memory expiry |
|---|---|---|---|---|
| SalesAgent | confidence ≥ 0.7 | Subject keyword | 1–5 | 30 days |
| HRAgent | confidence ≥ 0.5 | Sender domain | 1–10 | 7 days |
| FinanceAgent | No threshold | Date range only | Positive/Negative | None implemented |

All three teams independently made reasonable-sounding choices. The result is that the same campaign email analyzed by SalesAgent vs HRAgent produces a different sentiment rating, stored on a different scale, expiring at a different time. Cross-agent reporting becomes meaningless.

**With PlatformAgent:** Policy is encoded once in `AgentInstructions`. The sentiment scale, email ordering, SharePoint write logic, and memory expiry rules are defined in one place and applied identically to every caller. Changing the policy means changing one string.

**With Skills:** Policy is implicit in each team's calling pattern. Enforcing a policy change requires auditing every agent, issuing updated guidance, and waiting for each team to ship a new version — with no guarantee of completeness.

---

#### 2. Debugging Distributed Failures Is Painful

A multi-step task in a Skills architecture spans multiple services, multiple teams, and multiple telemetry systems. When it fails, reconstructing what happened requires correlating across all of them.

**Failure scenario:** Campaign analysis runs, sentiment is classified, but the SharePoint entry is never created and the summary email is never sent.

```
What actually happened:
  Step 1 — EmailSkill.SearchEmails()     → SUCCESS (EmailSkill team's logs)
  Step 2 — SentimentSkill.Analyze()      → SUCCESS (SentimentSkill team's logs)
  Step 3 — SharePointSkill.WriteItem()   → TIMEOUT (SharePointSkill team's logs)
  Step 4 — EmailSkill.Send()             → NEVER CALLED (calling agent gave up at step 3)

Questions that cannot be answered without cross-team investigation:
  - Did the calling agent's LLM decide not to retry step 3, or did the retry also fail?
  - Was the SharePoint timeout a transient error or a permissions issue?
  - Was step 4 intentionally skipped because step 3 failed, or was it a bug?
  - Who owns the retry policy — the calling agent team or the SharePointSkill team?
  - Is correlation ID propagated consistently across all four skill calls?
```

Even with OpenTelemetry in every service, correlating a distributed trace across four independently-owned services — each potentially using different span naming, different baggage propagation, different log formats — is a multi-hour debugging exercise.

**With PlatformAgent:** All four tool calls happen inside a single `InvokeObservedAgentOperation` span with one correlation ID, one telemetry owner, and one place to look. The `actionsTaken` array in the response tells the caller exactly which steps completed before failure.

---

#### 3. Inconsistent Task Memory Semantics

If memory is not centralized, every team invents their own. The result is incompatible memory schemas, conflicting keys, and non-uniform expiry policies on the same underlying `mcp_TaskPersonalizationServer`.

**Three teams, three memory schemas:**

```
SalesAgent saves:
  key: "sales:campaign-v1"
  { emailFilter, summaryRecipient, spListUrl, scheduleExpr }

HRAgent saves:
  key: "hr_agent_campaign_memory"
  { subjectKeyword, notifyEmail, listPath, frequencyDays }

FinanceAgent:
  (does not implement memory — re-asks user every run)
```

**Problems that emerge:**
- Key naming collisions if two agents happen to use the same string
- No shared expiry standard — one agent's memory expires while another's is stale but still loading
- Schema versioning is invisible — if SalesAgent adds a new field, it silently breaks if another agent reads the same key expecting the old shape
- `mcp_TaskPersonalizationServer` becomes a dump of incompatible blobs with no governance
- When a user updates their preferences, only the agent they talked to updates its memory — other agents continue running with stale parameters

**With PlatformAgent:** Memory schema, key derivation (`{callerAgentId}:{taskTypeSlug}`), expiry rules, and the clarification question set are all defined in one system prompt. Every caller gets the same memory contract. A schema change is a single edit to `AgentInstructions`.

---

#### 4. Partial Execution Handled Inconsistently

When a multi-step task partially completes — some steps succeeded, some failed — each calling agent team decides independently how to handle it. There is no shared contract.

**Same underlying failure, four different behaviors:**

| Agent | Step 3 (SharePoint) fails after Step 2 (Sentiment) succeeds | Behavior |
|---|---|---|
| SalesAgent | Retries step 3 three times, then returns error | User sees "Task failed" — no mention of what did complete |
| HRAgent | Skips step 3, continues to step 4 (email send) | Email sent, SharePoint empty — data is now inconsistent |
| FinanceAgent | Stops immediately, reports to user | User re-submits the entire task — steps 1 and 2 repeat unnecessarily |
| A new agent | Not handled — exception propagates | User sees a raw stack trace |

The most dangerous outcome is HRAgent's: it silently skips the SharePoint write and sends the summary email anyway. The user believes the task completed. The data is silently incomplete. This is an invisible partial failure that produces incorrect downstream state.

**With PlatformAgent:** The `partial_handoff` / `partial` / `failed` status contract is defined once and enforced by one LLM. The `actionsTaken` array tells every caller exactly which steps ran. The `errors` array documents what failed. The response contract is identical regardless of which caller invoked the agent — there are no surprises.

---

#### 5. Governance Changes Don't Propagate

When a compliance, security, or business rule changes, the Skills architecture has no enforcement mechanism. Each team must be found, notified, and given time to ship a new version.

**Example governance changes and their propagation cost:**

| Change | PlatformAgent | Skills |
|---|---|---|
| "All emails must be DLP-checked before sending" | Update system prompt + add DLP MCP server. Immediate, all callers comply. | Notify every calling agent team. Each team updates their EmailSkill call sequence. Deploy independently. No enforcement. |
| "Sentiment scale changes from 1–5 to 1–10 for Q2 reporting" | One line change in `AgentInstructions`. | Every team that stores sentiment scores must update their skill call, their SharePoint schema, and their reporting logic. |
| "SharePoint writes now require an audit tag field" | Add the audit tag to the system prompt's SharePoint instructions. | Every team that calls SharePointSkill must add the new parameter. Teams that miss the memo write non-compliant records silently. |
| "All agent-to-agent calls must include a new compliance header" | Update the single outbound call in PlatformAgent. | Every calling agent must update its skill invocation code. |

**The critical risk:** Regulatory and compliance changes have deadlines. A Skills architecture cannot guarantee that all agents comply by the deadline without a coordinated, tracked rollout across every team. PlatformAgent provides a single enforcement point — update it, compliance is immediate across all callers.

---

#### 6. Operational Ownership Is Unclear

In a Skills architecture, accountability for a failed end-to-end task is ambiguous. Every failure becomes a cross-team investigation before it can be triaged.

**Ownership ambiguity at each failure type:**

```
"The email wasn't sent"
  → Did the calling agent pass wrong parameters to EmailSkill?
  → Did EmailSkill accept the request but fail internally?
  → Did the calling agent's LLM decide not to call EmailSkill at all?
  Owner: Unknown until investigation.

"Wrong SharePoint list was written"
  → Did the calling agent pass the wrong URL?
  → Did SharePointSkill fail to validate the URL and write to a default?
  → Did the LLM hallucinate a URL that looked plausible?
  Owner: Unknown until investigation.

"Task ran twice"
  → Did the calling agent's scheduler fire twice?
  → Did the skill return an ambiguous response that caused a retry?
  → Did two different calling agents both have this task in memory?
  Owner: Unknown until investigation.

"Memory wasn't loaded for week 2"
  → Did the calling agent forget to call MemorySkill?
  → Did MemorySkill return a miss on a key that should exist?
  → Did the calling agent use a different key format than it used in week 1?
  Owner: Unknown until investigation.
```

In each case, the failure spans the boundary between the calling agent team and the skill team. Escalation paths are unclear. On-call rotations don't match the failure boundary. The team that gets paged first spends the first hour of every incident proving it wasn't their component.

**With PlatformAgent:** The operational boundary is clear. PlatformAgent owns everything from receipt of the agentic request to delivery of the structured JSON response. The calling agent owns everything before and after. When a task fails, the `errors` array, `actionsTaken` array, and the single telemetry span make root cause assignment immediate.

---

### Summary Verdict

| Consideration | Verdict |
|---|---|
| **Use PlatformAgent if** the primary use case is recurring, multi-step tasks with personalization/memory requirements and you want to minimize calling agent complexity | |
| **Use Skills if** the primary use case is simple, direct, auditable tool calls at volume and every calling agent team is capable of owning their own orchestration logic | |
| **Use both if** the platform will serve a range of callers with different task complexity profiles — this is the most realistic enterprise scenario | |

The current design is **correctly aligned with the hero scenario** (multi-step recurring tasks with memory). The six systemic risks above — policy drift, distributed debugging, memory inconsistency, partial execution variance, governance propagation, and operational ambiguity — all worsen linearly as the number of calling agent teams grows. They are manageable at two or three agents; they become a significant operational burden at ten or more.

The risk to monitor on the PlatformAgent side is over-routing simple tasks through the agent unnecessarily, accruing LLM cost and latency where a direct skill call would be cheaper, faster, and more observable.

---

## What's Out of Scope

The following are **not part of this design** and remain with each specialized agent:

- SalesAgent, HRAgent, FinanceAgent implementation
- Dataverse integration
- CRM / ERP integrations
- Business logic and decision-making
- Direct user conversation (PlatformAgent never responds to end users)
- Agent orchestration and workflow management (owned by calling agents)

---

## Summary

PlatformAgent transforms M365 platform capabilities into a **shared, intelligent service** that any agent can call with natural language. It eliminates duplicated platform integration work, removes repetitive user clarification through task memory, and enables clean composability with specialized agents via a structured partial-handoff contract.

**One deployment. Natural language interface. Persistent task memory. Zero rework for calling agents.**
