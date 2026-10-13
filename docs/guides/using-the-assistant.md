# Using the AI assistant

*For engineers who use TestBench day to day. No AI experience needed.*

## What it is
The assistant is a chat panel inside TestBench. You type what you want in plain English and it does the legwork: it builds plans from your spec sheets, runs them on the bench, and tells you what the results show. For example:

- "Make a hot/cold plan for the LDO-3300 from its datasheet: -40 to 125 °C, 3 units."
- "Run it and tell me what you find."
- "Which units failed at 125 °C?"
- "Compare this run against the rev-B targets."

It can look things up on its own. **Before it creates a file or uses the bench, it asks you**, and nothing happens until you click Approve.

It's off until you turn it on.

## What you need
An account with **Anthropic** (Claude) or **OpenAI**, and an API key from that account. You pay the provider directly for what you use. A typical question costs from under a cent to around ten cents with the default model; long chats cost more per message.

> If your company has set up an **AI gateway**, you don't need a key. The Settings page will say "Your organization's AI gateway", and you can skip step 3.

## Turning it on
1. Open **Settings**.
2. Read the short note about what is sent, then tick **Enable the AI assistant**.
3. Choose your provider, paste your API key, and click **Save key**. Click **Test connection** to check that it works.
4. Optionally set a **spending limit per chat**. The default is $1.00. (If TestBench doesn't know your model's prices, it limits tokens instead and says so under the input box.)
5. Click **Save**. The **Assistant** panel appears on the right.

![Settings](../images/04-settings-ai-assistant.png)

## Using it
- The small "⚙ …" lines show what the assistant looked up.
- When it wants to **create a file** (a plan or a report) or **use the test bench**, you'll see an orange card with the exact details. **Approve** goes ahead; **Deny** leaves everything as it was, and the assistant will suggest something else.
- When a run starts, the Runs tab opens so you can watch it progress. The findings and plots appear there too.
- The line under the input box shows how much this chat has used and its estimated cost.
- **New chat** starts over (and resets the spending meter).

Creating a plan from the datasheet asks first:

![Plan approval](../images/05-chat-plan-approval.png)

Then it asks before using the bench:

![Bench approval](../images/06-chat-run-approval.png)

When the run finishes, it reports the findings:

![Findings](../images/07-chat-findings.png)

## What costs money, and what doesn't
- 💲 **Every message you send** costs a little. The assistant usually talks to the provider several times per message: once to decide what to look up, again after each lookup, and once to write the answer. All of it goes on your provider account. The line under the input box shows the running total.
- **Free:** using TestBench's own screens, running plans on the bench, writing the CSV files and plots, and the analysis itself. These are TestBench, not AI.
- Long chats get more expensive per message, because the conversation so far is sent each time. Click **New chat** when you change topic.
- The **spending limit** is checked before every request the assistant sends. If the next request would likely go over, it isn't sent and the assistant tells you. The cost is an estimate, so the total can end up slightly above or below the limit.

## Where do the numbers come from?
TestBench calculates every number itself (pass/fail, statistics, drift) from the measurement files. The assistant chooses which analysis to run and explains the result. It doesn't do the maths, so the numbers it quotes match the report and the CSV files.

## Privacy
Your questions, and the data needed to answer them (for example a plan's limits or a run's findings), are sent to the provider you picked. Nothing is sent until you ask something. Your key is encrypted so only your Windows account on this PC can use it. Click **Remove** in Settings to delete it.

*Using Claude Code or Codex? You don't need this panel. See [Connecting a coding agent](connecting-a-coding-agent.md).*
