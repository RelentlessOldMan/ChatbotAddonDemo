# ChatbotAddonDemo 🔌

**A reference demo of how to add AI to a legacy Windows desktop tool: an opt-in chat assistant for everyday users, MCP access for people who use coding agents, an MCP hub that also lets the assistant use other MCP servers, and the customer paying for their own tokens.** The "legacy tool" is TestBench, a pretend test-bench application (WPF app + console tool). It turns spec sheets into XML test plans, runs them on a simulated bench, writes CSV and PNG results, and analyzes them for key findings.

![The assistant built a plan from a datasheet, ran it on the bench, and reported the findings](docs/images/07-chat-findings.png)

## Why

Companies ship desktop tools that run on customer machines. Some of those customers would love to "just ask it"; others already use Claude Code or Codex and want their agent to drive the tool. This repo shows one sensible way to serve both, and **writes down why** each decision was made:

- **Off by default**, and nothing leaves the PC until the user turns AI on and asks something.
- **The customer pays**: their own API key (encrypted with Windows DPAPI, the built-in per-user encryption), or better, their company's AI gateway (a proxy run by their IT that holds the vendor key). No vendor key ships in the binary.
- **AI can only do what a person could do**, through the same validated code. Anything that changes data or acts on the outside world (here: creating files, starting the bench) needs a person's OK: in the desktop app, an approval card, the same one whether the chat assistant or a connected coding agent asks; for coding agents using the console tool, the flags chosen when connecting plus the agent's own permission prompts.
- **The app computes, the model explains.** The model never writes the app's files or computes results (here: the XML and the statistics).

## Words used here

| Word | Meaning |
|---|---|
| **MCP** | Model Context Protocol: the standard way AI plugs in outside tools. Coding agents such as Claude Code use it, and so can a chat assistant. Many products offer an MCP server (Perforce, GitHub, Microsoft 365…). |
| **MCP hub** | The desktop app's one door for every AI (project `Demo.Mcp`). The chat panel calls the app's tools through it, coding agents connect to the open app through it, and it connects the chat to other MCP servers. Approvals happen here. |
| **Token** | The unit AI providers bill by, roughly ¾ of a word, counted on everything sent and everything received. |
| **Tools for AI** (capability catalog) | The list of operations the AI may ask the app to run: name, a description the model reads, inputs, and a safety class. Project `Demo.Capabilities`. |
| **Safety class** | Read: only looks. Write: creates or changes files. Execute: acts on the outside world (here, the bench hardware). |
| **AI gateway** | A proxy run by the customer's IT that holds the vendor key and can enforce budgets and logging. |
| **Core logic** | The legacy tool's own code (`Demo.Domain`). Every path ends here. |

## How it works

Two separate programs, four ways in. A and B existed before AI; C and D are what this demo adds. The two programs share nothing: each has its own workspace, and they never talk to each other.

```mermaid
flowchart LR
    eng(["Engineer"])
    dev(["Engineer with<br/>a coding agent"])

    subgraph desktop["demo-desktop.exe (WPF app)"]
        ui["Screens"]
        chat["Chat panel (opt-in)<br/>runs the agent loop"]
        hub["MCP hub<br/>the one door for every AI:<br/>approvals, permissions"]
        dcap["Tools for AI<br/>what it may do,<br/>with permissions"]
        dlogic["Core logic"]
    end
    subgraph cli["demo-cli.exe (console tool)"]
        cmds["Command-line<br/>processing"]
        mcp["MCP door<br/>demo-cli mcp"]
        ccap["Tools for AI<br/>what it may do,<br/>with permissions"]
        clogic["Core logic"]
    end

    llm["💲 Claude / OpenAI API<br/>billed to the customer's key"]
    agent["Claude Code / Codex<br/>runs its own agent loop"]
    agentllm["💲 The agent's own model<br/>billed to the agent user's<br/>subscription or API key"]
    other["Other MCP servers (optional)<br/>e.g. Perforce · GitHub · Microsoft 365<br/>chosen by the user or IT"]
    dws[("Desktop app's workspace<br/>specs · plans · runs")]
    cws[("Console tool's workspace<br/>specs · plans · runs")]

    eng -- A --> ui --> dlogic
    eng -- B --> cmds --> clogic
    eng -- C --> chat
    chat == "💲 C1: question +<br/>list of tools" ==> llm
    llm == "💲 C2: reply: 'please<br/>run tool X'" ==> chat
    chat -- "C3: run tool X over MCP<br/>(in-process)" --> hub
    hub -- "our tools" --> dcap --> dlogic
    hub -- "MCP: their tools" --> other
    dev -- D --> agent
    agent == "💲 tokens" ==> agentllm
    agent -- "D1: MCP to the open app<br/>(local only, no tokens)" --> hub
    agent -- "D2: starts it; MCP over<br/>stdin/stdout (no tokens)" --> mcp --> ccap --> clogic
    dlogic <--> dws
    clogic <--> cws

    classDef paid fill:#fff1c2,stroke:#d97706,color:#000
    classDef new fill:#dbeafe,stroke:#2563eb,color:#000
    class llm,agentllm paid
    class hub,other new
```
<sub>[Download this diagram as PNG](docs/images/diagrams/readme--how-it-works.png)</sub> <!-- diagram-png -->

**💲 marks every place tokens are spent.** Thick arrows are model calls; everything else is ordinary local code and file access.

**Who does what:** the model decides which tool to use, the loop drives (it sends the question, runs each tool the model asks for, and sends the result back), and the hub controls access (which tools there are, and which wait for the user's approval).

**The model never touches the app.** It only sends back text that asks for a tool (C2). The chat panel, which is our code, asks you first if the tool would create files, use the bench or reach another MCP server (unless IT trusts that server), then runs it through the hub (C3) and sends the result back in the next request (C1 again). This repeats until the model answers in words. Which provider and model to use (Claude or OpenAI, and the model name) comes from the app's Settings.

**The hub is the desktop app's one door for every AI.** The chat panel's tool calls go through it (in-process, so nothing extra leaves the PC). A coding agent can connect to the *open* app through it (D1), so it works with what you see on screen, and its changes wait for your OK in the window. And the hub connects the chat to other MCP servers, if the user or IT adds any, so the assistant can also use, say, Perforce or GitHub.

- **C1 + C2 together are one billed request**: tokens go out with the question, tokens come back with the reply.
- **Every request carries the whole conversation so far**, including earlier tool results such as spec limits and run findings. That is what leaves the PC, and why later requests in a chat cost more.
- **Each exe contains its own copy of the same libraries.** The two "Core logic" boxes are the same code, compiled into both programs. But they are two different programs, each with its own workspace; this demo shows both ways to add AI, not one app in two forms.

| Path | In plain words | AI? | Who pays |
|---|---|---|---|
| **A** | An engineer clicks around the desktop app. Unchanged legacy behavior. | No | n/a |
| **B** | An engineer or a script runs `demo-cli` commands. Unchanged legacy behavior. | No | n/a |
| **C** | An engineer types in the chat panel. 💲 Our app sends the question and a list of tools to Claude/OpenAI; the model replies "call tool X"; if X would create files, use the bench or use another MCP server (unless IT trusts it), our app asks the user first; it runs X through the hub and 💲 sends the result back, until the model answers in words. | Yes, **our app** calls the model | Customer's key or gateway |
| **D1** | An engineer asks Claude Code or Codex to work with the **open desktop app**. 💲 The agent asks its own model what to do; it reaches the app's hub through `demo-desktop.exe mcp` (a small relay it starts, which passes messages to the open window). Changes wait for approval in the window. Off until the user allows it in Settings. | Yes, but **only in their agent** | Their own subscription or API key |
| **D2** | An engineer asks Claude Code or Codex to use the **console tool**. 💲 The agent asks its own model what to do; when it decides to use a tool, it **starts `demo-cli mcp`** as a child process and calls the tool over MCP. | Yes, but **only in their agent** | Their own subscription or API key |

### Where tokens are spent

| Activity | Calls a model? | Whose tokens |
|---|---|---|
| Using the screens or `demo-cli` commands (paths A, B) | **No** | none |
| Running a plan, writing CSVs/plots, analyzing results, writing the report | **No.** The core logic is ordinary code | none |
| 💲 Each message in the chat panel (path C) | **Yes.** Several requests per message: one per model↔tool round trip, each resending the conversation so far | **The customer's** API key, or their company gateway |
| 💲 A coding agent using the open app or `demo-cli mcp` (paths D1, D2) | Our apps never do. The *agent* calls its own model to decide which tool to use next | **The agent user's** own subscription or API key |
| The hub calling another MCP server's tool for the chat | **Not by itself**, but the tool's result goes to the chat's model in the next request | The customer's key or gateway (for that next request) |
| 💲 "Test connection" in Settings | **Yes**, one tiny request | The customer's key or gateway |
| `--demo` mode, the screenshots, `dotnet test` | **No.** A scripted fake model stands in | none |
| 💲 `DEMO_LIVE_TESTS=1` | **Yes**: a short chat per provider, and the whole walkthrough job with each provider and with Claude Code and Codex (through `demo-cli mcp`) | Whoever's key is in the environment, and the agent users' accounts |

The important points:

- **The model never touches files or the bench.** It can only *ask* for a tool; our code decides and runs it.
- **The tools for AI (capability catalog) are defined once**: 13 tools, each with a name and description the model reads, its inputs, and a safety class (Read / Write / Execute). Both programs serve them over MCP. The desktop app adds one more read-only tool, `get_current_view`, so the assistant or an agent can see which plan or run you're looking at. Every path ends in the same core logic, so the same rules apply to everyone.
- **In the desktop app, every AI goes through the hub**, so approvals and permissions live in one place: the chat and connected agents get the same approval cards. Agents see only the app's own tools, never the other MCP servers'. Other servers' tools always ask first, unless IT marks a server as safe; the read-only labels such servers put on their own tools aren't trusted.

**Full explanation, with step-by-step sequence diagrams and the rules for every chat message: [docs/design.md](docs/design.md).** What leaves the PC on each path is drawn in [docs/security.md](docs/security.md#7-data-leaving-the-pc).

## Applying this to your own tool

TestBench is only the example. For your own desktop tool, the existing parts stay as they are and you add these new ones (blue):

```mermaid
flowchart TB
    subgraph before["Before"]
        direction TB
        p1(["People"])
        s1["Your screens"]
        c1["Your command line"]
        l1["Your core logic"]
        p1 --> s1 --> l1
        p1 --> c1 --> l1
    end

    subgraph after["After"]
        direction TB
        p2(["People"])
        a2(["People with<br/>a coding agent"])
        s2["Your screens"]
        chat["Chat panel<br/>opt-in, customer's key"]
        hub["MCP hub<br/>in your app"]
        c2["Your command line"]
        mcp["MCP mode<br/>of your command line"]
        tools["Tools for AI<br/>name, description, inputs,<br/>safety class"]
        other["Other MCP servers<br/>(optional)"]
        l2["Your core logic<br/>unchanged"]
        p2 --> s2 --> l2
        p2 --> c2 --> l2
        p2 --> chat --> hub
        a2 --> hub
        a2 --> mcp
        hub --> tools
        hub --> other
        mcp --> tools
        tools --> l2
    end

    before ~~~ after

    classDef new fill:#dbeafe,stroke:#2563eb,color:#000
    class chat,hub,mcp,tools,other new
```
<sub>[Download this diagram as PNG](docs/images/diagrams/readme--applying-this-to-your-own-tool.png)</sub> <!-- diagram-png -->

1. **Tools for AI:** a class that wraps the operations you already have, one method each, with a description for the model and a safety class (Read / Write / Execute). Here: [`TestBenchCapabilities.cs`](src/Demo.Capabilities/TestBench/TestBenchCapabilities.cs).
2. **Chat panel:** connects to the provider with the customer's key or gateway, sends the tools with each message, and shows an approval card before anything that isn't Read. Here: [`Demo.Ai`](src/Demo.Ai) plus the panel in `Demo.Desktop`.
3. **MCP hub:** serves the tools to the chat panel in-process, lets coding agents connect to the open app (with the same approval cards), and connects the chat to other MCP servers. Here: [`Demo.Mcp`](src/Demo.Mcp) plus `demo-desktop.exe mcp`, the relay an agent starts.
4. **MCP mode:** one more command on your console tool that serves the same tools to coding agents over stdin/stdout. Here: `demo-cli mcp` in [`Demo.Cli`](src/Demo.Cli).

The core logic isn't changed for AI, so its rules apply to every path.

## Walkthrough

These screenshots are generated by `scripts/capture-screenshots.ps1`, which drives the real apps. They are never taken by hand.

**1. The legacy tool, unchanged.** Test plans (XML), runs with findings and plots, and the customer's spec sheets. No AI anywhere until someone opts in.

![Test plans](docs/images/01-test-plans.png)
![Run results](docs/images/02-run-results.png)
![Spec sheets](docs/images/03-spec-sheets.png)

**2. Opting in.** Settings explains what gets sent and who pays, then asks for a key (or uses the company gateway).

![Settings](docs/images/04-settings-ai-assistant.png)

**3. Asking in plain English.** "Make a hot/cold plan for the LDO-3300 from its datasheet … then run it." The assistant reads the spec sheets on its own, but **creating the plan file needs approval**:

![Plan approval](docs/images/05-chat-plan-approval.png)

**4. Using the bench needs a second approval.** The new plan TP-1002 is already in the grid, validated and saved by the app, not written by the model.

![Run approval](docs/images/06-chat-run-approval.png)

**5. Results.** The run executes on the (simulated) bench. The app computes the findings: one unit (DUT 3) fails quiescent current at 125 °C while the others pass, so it's a bad part rather than a design problem. The assistant explains them, and you can ask follow-ups about specific conditions:

![Follow-up](docs/images/08-chat-follow-up.png)

The run's real output files: `results.csv`, `summary.csv`, a plot per test, and `report.html` (when requested: Write report button, `generate_report` or `demo-cli report`):

![IQ plot](docs/images/09-output-plot-iq.png)

**6. Other MCP servers and coding agents.** In Settings, the user can give the assistant other MCP servers (IT can add its own, or allow only its own), and let coding agents work with the open window:

![Settings: other MCP servers and coding agents](docs/images/10-settings-agents-and-servers.png)

**7. A coding agent working with the open app (path D1).** The agent connects through `demo-desktop.exe mcp`. It looks around on its own (here: what you're looking at, and the spec sheets), but its change waits for the same approval card the assistant gets. (In the screenshot, an MCP client stands in for Claude Code.)

![A coding agent's change waiting for approval](docs/images/11-agent-approval.png)

**8. The console tool**, used exactly as before with no AI involved:

![Console tool](docs/images/12-console-tool.png)

**9. A coding agent using the console tool (path D2).** The agent starts `demo-cli mcp`, sees the tools (each marked with its safety class: read / write / exec), and plans, runs and analyzes:

![MCP session](docs/images/13-mcp-session.png)

## Usage

Requires Windows and the .NET 10 SDK.

```powershell
dotnet build ChatbotAddonDemo.sln

# Desktop app in demo mode: fresh example workspace in %TEMP%, scripted assistant, no key needed.
.\src\Demo.Desktop\bin\Debug\net10.0-windows\demo-desktop.exe --demo

# Desktop app for real (workspace in %LOCALAPPDATA%\ChatbotAddonDemo\workspace); turn on AI in Settings
.\src\Demo.Desktop\bin\Debug\net10.0-windows\demo-desktop.exe

# Console tool
.\src\Demo.Cli\bin\Debug\net10.0\demo-cli.exe help

# Tests. Nothing billed: the AI paths talk to a fake AI gateway on 127.0.0.1. The UI tests open the app off-screen.
# GitHub runs the same tests, except the UI tests, on every push to main and every pull request (.github/workflows/build.yml).
dotnet test ChatbotAddonDemo.sln

# 💲 Paid live tests, only when you explicitly opt in: the same job with real Claude and OpenAI (path C)
# and real Claude Code and Codex (path D2). Roughly $1 in all; the Claude chat and Claude Code stop at $0.50 each,
# the OpenAI chat at 200,000 tokens, Codex at 5 minutes. Each test skips itself if its key or agent is missing.
# The switch is removed again afterwards, so a later plain `dotnet test` in this window costs nothing.
$env:DEMO_LIVE_TESTS = '1'; dotnet test --filter Category=Live; Remove-Item Env:DEMO_LIVE_TESTS

# Re-render every diagram to PNG (docs/images/diagrams) after editing one. Needs Node.js, Chrome or Edge, and
# internet access the first time (it downloads the Mermaid renderer from npm). Headless.
node scripts/render-diagrams.mjs

# Regenerate every screenshot in docs/images (the apps open off-screen)
powershell -ExecutionPolicy Bypass -File scripts\capture-screenshots.ps1
```

Environment variables (all optional):

| Variable | Effect |
|---|---|
| `DEMO_WORKSPACE` | Workspace folder for `demo-cli`. A `--workspace` argument wins over it. Default: `%LOCALAPPDATA%\ChatbotAddonDemoCli\workspace`. (The desktop app has a workspace of its own: `--workspace`, else `%LOCALAPPDATA%\ChatbotAddonDemo\workspace`, or with `--demo` a throwaway folder in `%TEMP%`.) |
| `DEMO_BENCH_DELAY_MS` | `0` makes the simulated bench instant (the tests use this). Any other value, or unset, runs at demo speed (a few seconds per run). |
| `ANTHROPIC_API_KEY`, `OPENAI_API_KEY` | A personal key for the chat panel, used when no company gateway is set (see the [IT admin guide](docs/guides/it-admin-deployment.md)). |
| 💲 `DEMO_LIVE_TESTS` | `1` enables the paid live-API tests. |
| `DEMO_CLAUDE_EXE`, `DEMO_CODEX_EXE` | Where the live tests find Claude Code or Codex, if not on PATH. |

Connect a coding agent, to the open desktop app or to the console tool: [docs/guides/connecting-a-coding-agent.md](docs/guides/connecting-a-coding-agent.md).

## Docs

| Document | For |
|---|---|
| [Design](docs/design.md) | Everyone: who calls what, why each piece exists, diagrams |
| [Decision records](docs/adr/) | Why each major choice was made, and what we rejected |
| [Security notes](docs/security.md) | Threats and mitigations |
| [Using the assistant](docs/guides/using-the-assistant.md) | End users |
| [Connecting a coding agent](docs/guides/connecting-a-coding-agent.md) | Claude Code / Codex users |
| [IT admin deployment](docs/guides/it-admin-deployment.md) | Rolling out keys or an AI gateway, MCP servers, agent access |

## Layout

```
src/
  Demo.Domain/         Core logic: plans (XML + XSD), spec import, simulated bench, result files, plots, analysis. No AI.
  Demo.Capabilities/   The tools for AI (capability catalog): what AI may do, with a safety class each (Read / Write / Execute), defined once.
  Demo.Ai/             Calling Claude/OpenAI (IChatClient), chat loop with approvals, credentials, cost meter.
  Demo.Mcp/            MCP: the desktop app's hub (chat, coding agents, other MCP servers) and the tools-to-MCP adapter.
  Demo.Desktop/        demo-desktop.exe, the WPF app: the legacy screens + the opt-in chat panel + the hub's agent panel.
  Demo.Cli/            demo-cli.exe, the console tool: the legacy commands + `demo-cli mcp` for coding agents.
tests/                 Domain, capabilities, AI (scripted model), hub, CLI/MCP end-to-end, every path with the same job, desktop UI tests.
tools/Demo.Screenshots Drives the apps and renders docs/images.
docs/                  Design, decision records, security, guides, images.
scripts/               capture-screenshots.ps1, render-diagrams.mjs
```

## Contributing

This is a personal project, published as-is — **issues and pull requests aren't accepted** (PRs auto-close). Fork it and make it your own. 🔌

## License

MIT — see [LICENSE](LICENSE). © 2026 RelentlessOldMan.
