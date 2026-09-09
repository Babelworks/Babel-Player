> Historical planning brief. Marketing copy here is not the source of current provider or runtime truth.
> Use `README.md`, `docs/AI-CONTEXT.md`, and `docs/Engineering-Plan.md` for the current product surface.

# Babel Player — Launch Campaign Brief
## "Run the dub locally"

**Prepared:** 2026-04-07  
**Goal:** Launch announcement  
**Audience:** AI/ML enthusiasts  
**Budget:** $0 — owned and earned channels only  
**Timeline:** 4 weeks (pre-launch prep + launch week + follow-through)

---

## 1. Campaign Overview

**Campaign name:** Run the dub locally

**One-sentence summary:** Babel Player is a desktop dubbing workstation that runs the full transcription → translation → TTS pipeline on your own hardware — no cloud API required.

**Primary objective:** Generate 500 GitHub stars and 1,000 unique visitors to the repo/landing page within 30 days of launch.

**Secondary objectives:**
- Land a post on the front page of Hacker News or r/LocalLLaMA
- Generate at least 3 pieces of community-created content (demos, threads, reposts)
- Establish a baseline of returning contributors (target: 5 PRs from outside contributors in first 30 days)

---

## 2. Target Audience

**Primary segment:** Local AI runners — people who are already running Whisper, Ollama, Stable Diffusion, or similar models on their own hardware. They distrust cloud lock-in, care about privacy, and get excited by "runs offline" as a feature, not a limitation.

Where they spend time: r/LocalLLaMA, r/selfhosted, r/MachineLearning, Hacker News, Hugging Face Discord, Twitter/X AI spaces, GitHub Explore.

Buying stage: Awareness → they don't know this tool exists yet.

Pain points:
- Existing dubbing/localization tools are cloud-only or require expensive subscriptions
- DIY pipelines (cobbling together Whisper + googletrans + edge-tts scripts) work but have no UI
- No good desktop-native option that respects the local-first ethos

**Secondary segment:** Open source developers who want to contribute to a real app — not a toy project — that touches interesting problems (native media, AI inference, C#/Avalonia desktop).

---

## 3. Key Messages

**Core message:** You can now run a full dubbing pipeline — transcribe, translate, and voice — entirely on your own machine, with a real desktop UI.

**Supporting messages:**

1. "Local-first, not local-only" — Faster Whisper or Parakeet for transcription, Piper or Qwen3-TTS for local voice generation, with optional cloud provider routing for each stage. You decide per-provider what runs where.

2. "Not a script, a workstation" — segment-level editing, per-speaker voice assignment, timeline scrubbing, SRT export. The kind of control you'd expect from a professional tool, built as open source.

3. "The stack is the story" — C# + Avalonia + libmpv + Python inference subprocesses. An unusual combination that's worth writing about if you care about desktop app architecture or local AI integration.

4. "NVIDIA-ready" — VSR, GPU inference routing, NVDEC hardware decode. Built for people who actually have the hardware.

**Proof points:**
- Working demo video showing the full pipeline end-to-end on local hardware
- GitHub repo with actual code, not a landing page
- Milestone tracking is public (`docs/PLAN.md` in repo) — shows the project is serious and structured
- AGPL-3.0 license — no corporate bait-and-switch risk

---

## 4. Channel Strategy

Zero paid budget means every channel requires time investment. Prioritized by expected reach-per-hour-invested.

### Hacker News — Show HN post (Highest priority)

**Why:** The AI/ML and open source developer audience is heavily concentrated here. A front-page Show HN can generate 5,000–20,000 visitors in 48 hours. Zero cost, high ceiling.

**Format:** `Show HN: Babel Player – an open-source desktop dubbing workstation (runs Whisper/TTS locally)`

**What makes it work:** The title must be specific and surprising. "Dubbing workstation" is more specific than "video tool." "Runs Whisper locally" signals the right tribe. The comment section response matters — be present, answer technical questions in depth.

**Timing:** Post on a weekday between 9–11am US Eastern. Tuesday–Thursday performs best for Show HN.

**Effort:** Medium (post is 30 minutes; responding to comments is 2–4 hours on launch day)

### r/LocalLLaMA — Demonstration post (High priority)

**Why:** This is the most directly aligned community. Local inference, no cloud dependency, GPU routing — these are values that resonate here by default.

**Format:** Video post showing the full pipeline running on a local GPU. Title: "I built a desktop dubbing app that runs Whisper + TTS entirely locally — here's the pipeline."

**Rules check:** r/LocalLLaMA allows project showcases. Avoid making it sound like a commercial pitch. Lead with the technical demo, mention it's open source and AGPL in the first comment.

**Effort:** Low (if demo video already exists); Medium (if video needs to be made)

### r/selfhosted and r/MachineLearning — Cross-posts (Medium priority)

**r/selfhosted:** Angle is "self-hosted desktop dubbing — no API keys required by default." This community cares about data sovereignty more than AI specifically.

**r/MachineLearning:** Angle is the inference pipeline architecture — Python subprocess model, provider routing, Faster-Whisper integration. More technical framing.

**Effort:** Low (adapt existing post, adjust framing)

### GitHub — Repo quality (Foundational, must ship before anything else)

The repo is the product page for this audience. Before posting anywhere:
- README must have a demo GIF or video embedded at the top
- README must clearly state: what it does, what runs locally, what the hardware requirements are, how to install
- `docs/PLAN.md` being public is a differentiator — mention it
- Issues should have `good first issue` labels so contributors have an entry point

**Effort:** Medium (README rewrite if needed; labeling issues is low effort)

### Twitter/X — Technical thread (Medium priority)

**Format:** A 6–8 tweet thread walking through the architecture. Start with the hook: "I built a desktop dubbing workstation in C# + Avalonia that runs Whisper, translation, and TTS locally. Here's how the pipeline works." Then explain each stage with screenshots or short clips.

**Why it works for this audience:** Technical architecture threads perform well in AI Twitter. The unusual stack (C#, not Python; desktop, not web) is itself the hook.

**Effort:** Medium (2–3 hours to write and assemble screenshots)

### Hugging Face — Model page or Space (Low priority, high leverage if done)

If the app ships with Faster-Whisper model download support, create a minimal Hugging Face Space that links to the GitHub repo. The HF community discovery surface is underutilized for desktop tools.

**Effort:** Low–Medium

### YouTube demo video (Required asset, not a channel itself)

A 3–5 minute screen recording showing: open a video file → run transcription → see segments → run translation → run TTS → preview dubbed output. No narration required — just the tool working. This video is the proof asset that every other channel links to.

**Effort:** Medium (recording + light editing: 2–4 hours)

---

## 5. Content Calendar

| Week | Content Piece | Channel | Notes | Status |
|------|--------------|---------|-------|--------|
| Week 0 (Pre-launch) | README rewrite | GitHub | Must be done before any posts go out | — |
| Week 0 | Demo video (3–5 min screen recording) | YouTube / GitHub | Core proof asset; everything else links here | — |
| Week 0 | `good first issue` labels on 3–5 issues | GitHub | Contributor funnel | — |
| Week 1 (Launch) | Show HN post | Hacker News | Tue–Thu, 9–11am ET; be present in comments all day | — |
| Week 1 | r/LocalLLaMA video post | Reddit | Same day or day after HN | — |
| Week 1 | Twitter/X architecture thread | Twitter/X | Day of HN post; cross-promote | — |
| Week 2 | r/selfhosted post | Reddit | Adapt LocalLLaMA post; self-hosting angle | — |
| Week 2 | r/MachineLearning post | Reddit | Architecture/inference angle | — |
| Week 2 | Respond to any HN/Reddit follow-up threads | Community | — | — |
| Week 3 | Follow-up post: "what we learned from launch week" | Hacker News / Twitter | Honest retrospective — performs well with this audience | — |
| Week 3 | HuggingFace Space or model card | HuggingFace | If bandwidth allows | — |
| Week 4 | Milestone 12 progress update | GitHub / Twitter | Keep momentum; show the project is active | — |

---

## 6. Content Assets Needed

| Asset | Description | Priority | Timeline |
|---|---|---|---|
| Demo video | 3–5 min screen recording of full pipeline | Must-have | Before launch |
| README rewrite | Clear hook, hardware reqs, install steps, demo GIF/video embed | Must-have | Before launch |
| Show HN post copy | Drafted title + opening comment | Must-have | Before launch |
| Twitter/X thread | 6–8 tweets, architecture walkthrough with screenshots | Must-have | Launch week |
| r/LocalLLaMA post | Video post + first comment with technical context | Must-have | Launch week |
| r/selfhosted post | Adapted version of LocalLLaMA post | Should-have | Week 2 |
| `good first issue` labels | Identify and label 3–5 accessible issues | Should-have | Before launch |
| HuggingFace Space | Minimal page linking to repo | Nice-to-have | Week 3 |
| "What we learned" post | Honest launch retrospective | Nice-to-have | Week 3 |

---

## 7. Success Metrics

**Primary KPI:** GitHub stars — target 500 within 30 days of launch.

| Metric | Target | How to Track |
|---|---|---|
| GitHub stars | 500 in 30 days | GitHub Insights |
| Repo unique visitors | 1,000 in 30 days | GitHub Traffic tab |
| HN post score | Top 10 Show HN on launch day | HN front page / Algolia HN search |
| Reddit upvotes (LocalLLaMA) | 200+ combined | Reddit post metrics |
| Outside contributor PRs | 5 in 30 days | GitHub PRs tab |
| Demo video views | 500 in 30 days | YouTube Studio |

**Reporting cadence:** Check GitHub stars and traffic daily in week 1, weekly after that.

---

## 8. Risks and Mitigations

**Risk 1: Demo video doesn't show a compelling end-to-end result**
The local inference pipeline is the whole story. If the demo shows lag, errors, or an unclear workflow, the launch falls flat.
Mitigation: Record on hardware where the pipeline runs cleanly. Use a short, pre-chosen video clip you know works. Edit out any wait times or show a progress indicator. Do not launch without a working demo.

**Risk 2: HN post timing is wrong or title undersells the hook**
Show HN posts live and die on their first two hours. A bad title or bad timing means it never gets traction regardless of quality.
Mitigation: Draft 3 title variants and pick the most specific one. Do not post on Monday or Friday. Be ready to respond to the first 5 comments within 30 minutes of posting — early engagement velocity matters to HN ranking.

**Risk 3: Project looks incomplete to technical users**
The AI/ML audience will clone the repo and try to run it. If install is painful or the app crashes on a clean machine, negative first impressions travel fast in these communities.
Mitigation: Do a clean-install test on a machine that has never had the project before. Document every prerequisite. Add a known-issues section to the README if there are rough edges.

---

## 9. Next Steps

1. **Record the demo video** — this is the critical path item. Nothing else should be posted without it.
2. **Rewrite the README** — embed the video, clarify hardware requirements, add install steps.
3. **Label 3–5 `good first issue` items** in GitHub so the contributor funnel is ready on day one.
4. **Draft the HN post title** — write 3 variants, pick the most specific.
5. **Pick launch day** — a Tuesday or Wednesday works best. Everything else slots around it.
