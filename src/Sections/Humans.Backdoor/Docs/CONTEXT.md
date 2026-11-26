# Backdoor — Language

Personal API keys and the API they open. Settled in the 2026-11-24 terminology grill. Shared words live in the root [`CONTEXT.md`](../../../../CONTEXT.md).

## Language

**Backdoor**:
The section's name, an in-joke. Members never see it except in the API's URLs; the page says "API keys".

**Backdoor API**:
Everything under `/api/backdoor/`, reached only with an API key.
_Avoid_: machine surface

**API key**:
A personal key that lets a program act as its owner, with exactly the owner's own rights.
_Avoid_: token, credential

**Owner**:
The person an API key belongs to and acts as.
_Avoid_: holder (Holder means the person named on a ticket), user

**Create / rotate / revoke**:
Making a key (for yourself, or Admin for someone else), swapping it for a fresh one, and switching it off for good.
_Avoid_: make, issue, allocate

**MCP endpoint**:
`/api/backdoor/mcp`, where an AI client connects with an API key and asks which tools it may use.

**Tool**:
One named thing the MCP endpoint lets a client do, such as `hello`. Each declares a scope.
_Avoid_: function, command

**Scope**:
Which people a part of the Backdoor API is for. Today: **Admin and Board**, and **Tasks** (people who can manage tasks on some team).
_Avoid_: Staff
