# Using the AI assistant

*For people who use Workshop Manager day to day. No AI experience needed.*

## What it is
The assistant is a chat panel inside Workshop Manager. You type questions in plain English ("which orders are overdue?", "what should I reorder?") and it looks up the answer in your data. It can also make changes, such as raising a priority, but **only after you click Approve**.

It's off until you turn it on.

## What you need
An account with **Anthropic** (Claude) or **OpenAI**, and an API key from that account. You pay the provider directly for what you use. A typical question costs a fraction of a cent to a few cents.

> If your company has set up an **AI gateway**, you don't need a key: the Settings page will say "Your organization's AI gateway" and you can skip step 3.

## Turning it on
1. Open **Settings → AI assistant**.
2. Read the short note about what is sent, then tick **Enable the AI assistant**.
3. Choose your provider, paste your API key, and click **Save key**. Click **Test connection** to check it works.
4. Optionally set a **spending limit per chat**. The default is $1.00.
5. Click **Save**. The **Assistant** panel appears on the right.

![Settings](../images/02-settings-ai-assistant.png)

## Using it
- Ask about work orders and parts. The small "⚙ …" lines show what the assistant looked up.
- When it wants to change something, you'll see an **orange card** with the exact change. **Approve** applies it; **Deny** leaves everything as it was.
- The line under the input box shows how much this chat has used and its estimated cost.
- **New chat** starts over (and resets the spending meter).

![Approval](../images/04-chat-approval.png)

## Privacy
Your questions, and the data needed to answer them, are sent to the provider you picked. Nothing is sent until you ask something. Your key is encrypted so only your Windows account on this PC can use it. Click **Remove** in Settings to delete it.
