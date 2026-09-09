# Architecture

## Purpose

This document describes the current intended structure of the app.

It is not a grand design document and it is not permission to build future systems early. `docs/PLAN.md` remains the entry point for milestone order and product sequencing. This file exists to clarify the current structural boundaries of the app so contributors do not invent them ad hoc.

The architecture should stay subordinate to the product chain:

`source media -> timed transcript -> translated/adapted dialogue -> spoken dubbed output -> in-context preview and refinement`

If the repo begins to optimize for shell prestige, framework purity, or speculative extensibility ahead of that chain, the architecture is drifting.

---

## Current Architectural Intent

The app is a desktop dubbing-oriented application built as a sequence of vertical slices.

The app is not treated as a generic media player with AI attached. It is treated as a workflow system for turning source media into transcript, translated/adapted dialogue, generated spoken output, and usable in-context preview.

Playback remains important, but as a supporting capability for inspection, timing, and refinement rather than the core identity of the product.

The architecture should therefore be shaped around these responsibilities:

- loading and representing media inputs
- producing and persisting intermediate artifacts
- orchestrating transcription, translation/adaptation, and TTS stages
- preserving session state across restarts
- previewing generated output in useful context
- exposing truthful system state to the user

---

## Core Workflow Chain

The core product chain is:

1. ingest source media
2. extract or persist reusable media artifacts
3. generate timed transcription
4. produce translated and adapted dialogue
5. generate spoken dubbed output
6. preview and refine that output in context
7. persist session state so work can be resumed later

Implementation order may vary slightly to retire specific technical risks early, but the system should always be judged by how directly it supports this chain.

---

## Major Boundaries

### 1. Shell / UI

The shell is responsible for presenting the workflow, displaying state, accepting user actions, and exposing inspection surfaces.

The shell should not become the owner of business logic, media orchestration, or AI workflow decisions.

Its job is to:

- display current session state
- trigger commands on the workflow owner
- present progress, errors, and verification state honestly
- support inspection and refinement of outputs

The shell should stay as light as practical. Premium-feeling UI is not a priority unless it directly improves the core workflow.

### 2. Session / Workflow Coordination

A central coordinator should own workflow progression and session state.

That coordinator is the main boundary between presentation and real behavior. It should be the place where the app understands:

- what media is loaded
- what artifacts exist
- which stages have run
- which outputs are pending, generated, accepted, or invalidated
- what the current session can truthfully do

State should not be fragmented across random views, helper classes, and code-behind.

### 3. Media Transport

Media transport is a supporting subsystem for load, play, pause, seek, duration, timing, and in-context preview.

It matters because the product still needs media accuracy and useful inspection. But it should remain a transport seam rather than becoming the conceptual center of the app.

The transport layer should:

- load local media reliably
- expose timing information
- support stable teardown and reload cycles
- provide the playback hooks needed for preview and sync-sensitive workflow

### 4. AI / Inference Services

Transcription, translation/adaptation, and TTS belong behind service boundaries rather than being interwoven directly into the shell.

These services are expected to evolve over time, but the app should not pre-build a giant provider matrix before the milestones require it. Early slices should stay narrow and real.

The AI/inference boundary should make it possible to:

- request work for a defined stage
- receive explicit results or errors
- persist stage outputs as reusable artifacts
- swap implementation details later without rewriting the product workflow

### Python inference hosting and deployment posture

Python-backed inference should remain behind a clear service or process boundary.

That boundary should allow multiple hosting modes over time, including:
- native local execution
- WSL-hosted execution
- containerized execution
- NVIDIA-managed serving paths where they fit

The desktop app should not assume one hosting mode too early. The product should first prove a narrow working inference path, then expand into more isolated or accelerated deployment models as local/offline support becomes real.

This means:
- request/response contracts across the app/inference boundary should stay explicit
- model/runtime assumptions should be documented as they are discovered
- model downloads and runtime assets should stay separate from application source code
- deployment isolation is a future-compatible goal, not an early mandatory requirement

### 5. Artifact Storage

The app should treat generated artifacts as first-class outputs.

That includes things like:

- extracted audio or intermediate media assets
- timed transcript data
- translated/adapted dialogue data
- generated speech outputs
- session metadata and state

Persistent artifacts matter because the product is iterative. Users should not have to recompute everything every time they reopen the app or revise one segment.

---

## State Ownership

> For the concrete key-files table and services reference, see [docs/AI-CONTEXT.md](AI-CONTEXT.md).

The default rule is simple:

- the shell displays state
- the coordinator owns workflow state
- services produce results
- storage preserves artifacts and session data

If a new feature requires state, contributors should first ask whether that state belongs in the central session/workflow owner rather than inventing a new local owner in the UI layer.

The architecture should prefer a small number of explicit state owners over a larger number of convenience caches and duplicated view-local models.

---

## Domain Shape

The exact type names may change, but the system should naturally revolve around concepts like:

- source media
- session
- timed transcript segments
- translated/adapted dialogue segments
- generated dub variants
- persisted artifacts
- playback/preview context

Those concepts are closer to the real product than abstract framework-first categories.

If a proposed abstraction does not correspond to an actual product concept or current milestone need, it probably does not belong yet.

---

## Truthfulness and Deferred Work

This architecture deliberately leaves many details open.

That is intentional.

The repo should not lock in large future systems before the current milestones earn them. In particular, the following should remain deferred until the plan requires them:

- broad provider matrices
- elaborate runtime selection systems
- large setup/install orchestration surfaces
- plugin systems
- speculative multi-platform complexity beyond current support goals
- abstractions created mainly for hypothetical future backends
- a second MVVM / functional-UI stack (ReactiveUI, Zafiro.Avalonia) in the shell. See [architecture-reactive-ui-zafiro.md](reference/architecture-reactive-ui-zafiro.md) for the feasibility study. That document is not permission to add those packages.

When something is not decided, say it is not decided.
When something is not implemented, surface that truthfully.

---

## Current Biases

When tradeoffs appear, the architecture should generally prefer:

- real working slices over cleaner speculation
- persistent artifacts over repeated recomputation
- one clear state owner over fragmented convenience state
- narrow service seams over giant early abstraction layers
- truthful failure states over silent fallback
- in-context preview support over shell ornament

---

## How This Document Should Evolve

This document should grow only when the codebase earns new structural decisions.

Good reasons to update it:

- a milestone introduces a real new boundary
- a subsystem becomes important enough to describe explicitly
- a formerly deferred decision is now implemented and verified
- a previous architectural assumption has changed

Bad reasons to update it:

- describing systems that do not exist yet
- documenting aspirational structure as if it were real
- using architecture language to justify scope creep

This file should remain a living field manual, not scripture.