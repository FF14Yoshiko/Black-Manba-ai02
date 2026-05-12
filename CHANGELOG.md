# Changelog

All notable changes to this plugin will be documented in this file.

## 1.2.1 - 2026-05-13

- Refactored the oversized MainWindow into clearer partial modules while preserving the existing toolchain, review surfaces, and in-match controls.
- Added AI teacher learning for command and target resolution feedback, including map-separated samples, replay-backed persistence, and regression coverage for the learning path.
- Improved LLM scheduling and overlay behavior with routine pulse gating, stronger AI-led HUD signaling, and better display stickiness for AI directives.
- Continued cleanup of retired features and stale UI wording so removed systems no longer leak into the current workflow.

## 1.2.0 - 2026-05-12

- Promoted LLM battlefield reads into a more practical in-match workflow with fixed strategic pulses, event-priority gating, and clearer AI-led command display controls.
- Added regression coverage for score confirmation, strategic arbitration, target resolution wording, overlay AI display stickiness, and LLM scheduling behavior.
- Extracted frontline parsing and route-risk seams into smaller testable units so battlefield frame input to decision output can be tuned with less guesswork.
- Removed dead or retired features, including in-game auto target marking remnants and the custom camera distance feature chain.

## 1.1.2 - 2026-05-11

- Relaxed the LLM gate so in-match testing can trigger more often, with cleaner JSON payload output and richer debug fields.
- Fixed manual AI probe prompts incorrectly sending a fake pre-match time state when live match time was temporarily unreadable.
- Split battlefield refresh work across more frames and reduced same-frame sampling pressure to smooth out large-team combat stutter.
- Kept local immediate combat handling intact while moving more strategic and debug work off the hot path.

## 1.1.0 - 2026-05-09

- Split the main experience into a compact in-combat command HUD and a separate review/debug page.
- Added configuration export/import with a versioned export document and backward-compatible raw config import.
- Formalized plugin metadata, release versioning, and release notes.
- Added CI build automation plus package validation and SHA256 checksum generation.
- Removed stale duplicate project content and unused reference assets from the workspace.
