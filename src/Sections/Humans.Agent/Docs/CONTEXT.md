# Agent — Language

The member help chat. Settled in the 2026-11-24 terminology grill. Shared words live in the root [`CONTEXT.md`](../../../../CONTEXT.md).

## Language

**Assistant**:
The help chat as members see it, on every screen. "Agent" is the section's name in code only.
_Avoid_: agent (on screens), bot, chatbot

**Conversation**:
A member's exchange with the Assistant, from first question to last answer.
_Avoid_: transcript, history, chat

**My conversations**:
The member's list of their own conversations.
_Avoid_: history

**Question**:
What a member sends the Assistant. Daily and hourly limits count questions.
_Avoid_: message (for the member's side)

**Turn**:
One question and the Assistant's answer to it, including any lookups in between. The code word; members see "question".

**Context**:
What the Assistant is told about the member with each question: name, language, roles, teams and similar. On screen: "What the Assistant knows about you".
_Avoid_: snapshot, user snapshot

**Preload**:
What the Assistant knows before it looks anything up: the list of sections, help glossaries and page map. Comes in a **Small** and a **Large** size.
_Avoid_: tier, Tier1, Tier2 (tier means membership tier)

**Section doc**:
A section's rules doc, which the Assistant reads on demand.
_Avoid_: section guide (Guide means only the /Guide pages)

**Community FAQ**:
The separate repository of community answers the Assistant can look up.
_Avoid_: knowledge base, KB, community KB

**Handoff**:
The Assistant drafting an issue for the member to review and submit when it can't answer. It never files the issue itself.
_Avoid_: issue proposal, routing

## Turn outcomes

A turn ends in an answer or in one of these. There is no umbrella word for them.

**Limit reached**:
The member used up their daily or hourly questions, so the Assistant was never asked.

**Distress reply**:
The question contained signs of self-harm, so the member got the fixed reply pointing to a coordinator or emergency services instead of an answer.
_Avoid_: abuse, abuse flag

**Failed**:
Something broke on our side or the provider's before the answer finished.
_Avoid_: error refusal

_Avoid_ for all three together: refusal, refused
