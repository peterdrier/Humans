# Post-event survey — feedback about the Humans app

**Status:** draft build-sheet. Not built. To be entered manually in the Survey admin ~1 week after the event concludes.

This survey gathers feedback about **the Humans app itself** (the tool), not the event. It is meant to be sent ~1 week after the event ends, while the experience is still fresh. Below is everything needed to build it by hand in the Survey admin: send/anonymity config, intro copy, and the full question set in English + Spanish, with the engine question-type, required flag, and branch conditions for each.

Spanish uses the informal **tú** register to match the existing in-app survey strings (e.g. `Survey_AnonymityLegend` = "¿Cómo quieres responder?").

---

## 1. Send & anonymity config

| Setting | Value | Why |
|---|---|---|
| **Audience** | **Everyone who logged into Humans** (anyone who actually used the app) — see §1.1 for the engine gap | App feedback should come from people who actually used the app, including those still in onboarding — not just ticket holders or shift signups. |
| **`AllowAnonymous`** | **true** | Shows the respondent the anonymity chooser on the intro screen. Off would force every response to `Identified`. |
| **Anonymity default** | `Identified` (current engine default — pre-checked radio) | See §1.2. For a *suggestion-gathering* survey, follow-up is the asset, so we lean Identified and use framing to keep candor. |
| **Reminder** | **Fixed: one reminder 7 days after the invite** (not configurable) | `SendDueRemindersAsync` uses a hard-coded 7-day cutoff (`now - Duration.FromDays(7)`) and skips `Completed` invitations. Submitting any tracked response (Identified **or** CompletionTracked) marks the invite `Completed` and stops the reminder; public-slug (`Anonymous`) responders aren't invited at all, so they're never reminded. |
| **Timing** | ~1 week after the event ends | Fresh enough to remember specifics, late enough that the dust has settled. |

### 1.1 Audience — "everyone who logged into Humans" vs. the engine

The intended audience is **everyone who logged into Humans** — the people who actually used the app, which for a tool-feedback survey is the only audience that makes sense (and notably *includes* users still mid-onboarding, who have the freshest signup/profile/consent feedback).

**The engine can't target that set precisely today.** `SurveyAudienceType` has four values — `Team`, `AllActiveMembers`, `TicketHolders`, `ShiftParticipants` — and none means "has logged in." The resolver (`SurveyService.ResolveRecipientIdsAsync`) maps `AllActiveMembers` to *active members* (`ActiveMemberIdsAsync`), which is a **different set**: it can include active members who never logged in, and it excludes logged-in users who aren't yet active members (onboarding in progress). So `AllActiveMembers` is a *proxy*, not the real thing.

This also can't be solved with the public slug link: the slug path is always `Anonymous`, which forfeits the Identified/follow-up strategy this survey is built around (§1.2). Preserving follow-up requires an **invited (tokenised) send**, which means the audience must resolve to a concrete recipient set.

**Decision (resolved):** Add a **`LoggedInSince`** audience type with a **cutoff date** — `LastLoginAt >= cutoff` — so each cycle the cutoff is set to that event year's start and the survey captures only that year's active users. Tracked as `nobodies-collective/Humans#894` (engine extension: new enum value + a `Survey` cutoff field + a resolver branch reading via `IUserServiceRead`; `User.LastLoginAt` already exists and is populated on sign-in, so no backfill).

**Interim fallback:** if #894 hasn't shipped by send time, use `AllActiveMembers` as a close-enough proxy — it invites some never-logged-in members and misses some onboarding users, but the gap is small at ~500 users.

### 1.2 The anonymity choice — and why we lean Identified here

The respondent picks their tier on the intro screen (`ResponseAnonymity`): **Identified** (linked to them, resumable, follow-up possible), **CompletionTracked** (unlinked but participation counted, stops reminders), or **Anonymous** (no trace).

For a normal satisfaction survey you'd protect candor by defaulting to unlinked. **This survey is different:** its main job is to collect feature/fix *suggestions*, and suggestions arrive under-explained. The binding constraint is not getting feedback — it's getting feedback specific enough to act on. Follow-up turns a vague "the shifts thing should be smarter" into shippable work, so **linkability is the asset, not a cost.**

We therefore keep the **Identified default** and use the intro copy to (a) frame Identified as *"this is how your idea actually gets built"* rather than surveillance, and (b) remove the candor brake with the "Peter has thick skin" line. The unlinked options stay genuinely available for anyone who wants to be sharp without a name attached.

> **Optional follow-up (code change, not done here):** if you later decide candor should win over follow-up, change the pre-checked radio + view-model default from `Identified` to `CompletionTracked` (`SurveyViewModels.cs` default + `Intro.cshtml` `checked` attribute). Flagged for your call; not part of this draft.

Do **not** add a separate "email me" contact field — `Identified` already carries the linkage; reuse it.

---

## 2. Intro / welcome copy

**EN**

> ### Help us make Humans better — be honest, not kind
>
> This survey is about the **app** you used during the event, not the event itself. We built Humans *while* this event was being planned and run, so some parts were rough — we know, and that's expected. There's a lot more we want to do, and your answers decide what we fix first. So please be blunt: telling us what frustrated you, or what's missing, is far more useful than telling us it was fine.
>
> Some of the most useful feedback is a half-formed idea. If we can't reach you, a half-explained idea often can't get built — so if you're up for it, choose **Identified** on the next screen. It just means Peter, who builds Humans, might message you to ask "what did you mean by that?" so your idea actually ships. He's got thick skin: blunt is genuinely more useful than polite, and nothing here gets held against anyone. Prefer to stay unlinked? Totally fine — just be as specific as you can.
>
> About 5 minutes. Works on your phone. Skip anything you'd rather not answer.

**ES**

> ### Ayúdanos a mejorar Humans — sé sincero, no amable
>
> Esta encuesta trata sobre la **aplicación** que usaste durante el evento, no sobre el evento en sí. Construimos Humans *mientras* se planificaba y desarrollaba este evento, así que algunas partes fueron toscas — lo sabemos, y es de esperar. Queremos hacer mucho más, y tus respuestas deciden qué arreglamos primero. Así que sé directo: contarnos qué te frustró, o qué falta, es mucho más útil que decirnos que estuvo bien.
>
> Algunas de las ideas más útiles llegan a medio formar. Si no podemos contactarte, una idea a medio explicar a menudo no se puede construir — así que, si te apetece, elige **Identificado** en la siguiente pantalla. Solo significa que Peter, quien desarrolla Humans, podría escribirte para preguntarte "¿qué querías decir con esto?" para que tu idea de verdad se haga realidad. Tiene la piel dura: ser directo es mucho más útil que ser cortés, y nada de lo que digas se usará en tu contra. ¿Prefieres quedar sin vincular? Totalmente bien — solo sé lo más específico que puedas.
>
> Unos 5 minutos. Funciona en el móvil. Puedes saltarte cualquier pregunta que prefieras no responder.

---

## 3. Questions

Engine types: `SingleChoice`, `MultiChoice`, `ShortText`, `LongText`, `Rating`. Scale items below use **`SingleChoice` with fully-labeled options** (better measurement than endpoint-only `Rating`); if you prefer the lighter star/number UI, `Rating` with min/max labels is an acceptable substitute for Q1–Q3.

Only **Q1 is required** (Q2 optional-but-recommended). Leave every open-ended question optional — required text boxes kill completion.

**Every choice option needs a non-empty `Value`** — the admin's per-option machine value, stored separately from the label. The answer flow persists only the `Value`, and `AnswerState.IsAnswered` treats an empty value as *unanswered*, so a blank `Value` makes required Q1 impossible to submit and breaks branching. Convention: set each option's `Value` to the lowercase English slug of its label (`very_dissatisfied`, `dissatisfied`, `neutral`, …). The `(value)` tokens called out below for branch options (`other`, `yes`, `no`) are the ones **branch conditions reference and must match exactly**.

### Page 1 — Overall

**Q1 · `SingleChoice` · required** — *Overall satisfaction*
- EN: **Overall, how satisfied were you with Humans during the event?**
- ES: **En general, ¿qué tan satisfecho estuviste con Humans durante el evento?**
- Options (EN / ES): Very dissatisfied / Muy insatisfecho · Dissatisfied / Insatisfecho · Neutral / Neutral · Satisfied / Satisfecho · Very satisfied / Muy satisfecho

**Q2 · `SingleChoice` · recommended** — *Ease of use*
- EN: **How easy or hard was Humans to use?**
- ES: **¿Qué tan fácil o difícil fue usar Humans?**
- Options: Very hard / Muy difícil · Hard / Difícil · OK / Normal · Easy / Fácil · Very easy / Muy fácil

**Q3 · `SingleChoice`** — *Coverage of needs*
- EN: **How well did Humans cover what you actually needed to do?**
- ES: **¿Hasta qué punto Humans cubrió lo que realmente necesitabas hacer?**
- Options: Not at all / Nada · A little / Un poco · Partly / En parte · Mostly / En su mayoría · Completely / Completamente

### Page 2 — What you used & whether it helped

**Q4 · `MultiChoice`** — *Which parts used* — _align this list to what members actually touched at this event_
- EN: **Which parts of Humans did you use? (Select all that apply)**
- ES: **¿Qué partes de Humans usaste? (Marca todas las que correspondan)**
- Options: Signing up / creating my account · Completing my profile & consent · Tickets · Shifts / sign-ups · Teams · Finding event info · Other `(other)`
- ES: Registrarme / crear mi cuenta · Completar mi perfil y consentimiento · Entradas · Turnos / inscripciones · Equipos · Encontrar información del evento · Otro

**Q4a · `ShortText` · optional · branch: show if Q4 includes `other`** — *Other write-in (workaround — see §4)*
- EN: **You picked "Other" — which part?**
- ES: **Elegiste "Otro" — ¿qué parte?**

**Q5 · `SingleChoice`** — *Net value vs. the old way*
- EN: **Compared with how this worked before (paper, spreadsheets, email, WhatsApp, asking an organiser), did Humans make taking part…**
- ES: **Comparado con cómo funcionaba antes (papel, hojas de cálculo, correo, WhatsApp, preguntar a un organizador), ¿Humans hizo que participar fuera…**
- Options: Much harder / Mucho más difícil · Somewhat harder / Algo más difícil · About the same / Más o menos igual · Somewhat easier / Algo más fácil · Much easier / Mucho más fácil · Can't compare (first event) / No puedo comparar (fue mi primer evento)

### Page 3 — The rough edges & your ideas

**Q6 · `SingleChoice`** — *Hit anything broken/confusing?*
- EN: **Did you run into anything that was broken, confusing, or didn't behave the way you expected?**
- ES: **¿Te encontraste con algo que estuviera roto, fuera confuso o no funcionara como esperabas?**
- Options: Yes `(yes)` / Sí · No `(no)` / No

**Q6a · `LongText` · optional · branch: show if Q6 = `yes`** — *What happened*
- EN: **What happened? Be specific — what were you trying to do, and what went wrong?**
- ES: **¿Qué pasó? Sé específico — ¿qué intentabas hacer y qué salió mal?**

**Q7 · `LongText`** — *One change/addition (the suggestion question)*
- EN: **If you could change or add one thing in Humans, what would it be — and what would it let you do that's awkward or impossible now?**
- ES: **Si pudieras cambiar o añadir una sola cosa en Humans, ¿qué sería — y qué te permitiría hacer que ahora es complicado o imposible?**
- HelpText EN: *The "what it would let you do" part is what helps us actually build it.*
- HelpText ES: *La parte de "qué te permitiría hacer" es la que nos ayuda a construirlo de verdad.*

**Q8 · `ShortText`** — *Most useful part*
- EN: **What part of Humans was most useful, or saved you the most hassle?**
- ES: **¿Qué parte de Humans te resultó más útil o te ahorró más complicaciones?**

### Page 4 — Optional

**Q9 · `SingleChoice` · optional** — *How you got unstuck*
- EN: **When you got stuck, how did you usually sort it out?**
- ES: **Cuando te atascabas, ¿cómo lo resolvías normalmente?**
- Options: Figured it out in the app / Lo resolví en la app · Asked a volunteer/organiser / Pregunté a un voluntario u organizador · WhatsApp/Telegram group / Grupo de WhatsApp o Telegram · Gave up / skipped it / Me rendí o lo salté · Didn't get stuck / No me atasqué

**Q10 · `SingleChoice` · optional** — *First event?*
- EN: **Was this your first event with Nobodies?**
- ES: **¿Fue este tu primer evento con Nobodies?**
- Options: Yes, my first / Sí, el primero · No, I've been to others / No, he estado en otros

**Q11 · `LongText` · optional** — *Anything else*
- EN: **Anything else you want us to know — good, bad, or wishlist?**
- ES: **¿Algo más que quieras contarnos — bueno, malo o lista de deseos?**

---

## 4. "Other → specify" — engine note

`SingleChoice`/`MultiChoice` options have no free-text flag today, so an "Other" pick can't capture a write-in directly. **Q4a is the zero-code workaround:** a separate optional `ShortText` shown only when "Other" is selected (`ShowIf`). Same pattern fits Q9 if you want it there too.

If surveys become a recurring tool, the clean fix is a small extension — an `AllowsText` flag on `SurveyQuestionOption`, persisted into the existing `SurveyAnswer.TextValue` — which would retire this workaround. Out of scope for this draft.

---

## 5. Why these questions (survey-science rationale, brief)

- **~10 items, mostly one-tap; only Q1 required.** Completion falls off a cliff past ~5 minutes, and required open-ends drive abandonment.
- **One construct per question** — no double-barreled "easy *and* useful" items that can't be acted on.
- **Balanced, consistently-directed scales** — every scale runs low=bad → high=good, fully labeled to remove interpretation variance across EN/ES.
- **Q5 measures the counterfactual** (vs. paper/WhatsApp/spreadsheets) — the real "is this worth continuing to invest in" signal, which raw satisfaction misses.
- **Q7 forces a single prioritised idea and extracts the *problem behind it*** — the omitted half of every feature request, and the cheapest way to reduce how often you must follow up.
- **Q6 gates its open-end behind Yes/No** — keeps it short for the untroubled, collects detailed bug reports from those who hit something.
- **Q8 (most useful part) is not ego-stroking** — it identifies what's load-bearing so a redesign doesn't break it, and balances the negativity pull of Q6/Q7.
- **Demographics last (Q10), almost nothing required** — standard ordering to protect completion.

**Deliberately excluded:** NPS (meaningless for an internal tool, awkward bilingually), per-feature rating matrix (no matrix type, high mobile skip), option-order randomization (marginal at ~500 known respondents).
