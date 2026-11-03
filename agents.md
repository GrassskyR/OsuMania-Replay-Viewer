# AGENTS.md

## 1. Avoid Over-Defensive Programming

- **Complexity:** Do not add complexity for speculative edge cases. For any significant additional complexity, clearly explain the failure scenario, likelihood, costs, and benefits, and obtain confirmation before proceeding.
- **Defensive Programming:** Avoid speculative checks, redundant validations, unnecessary `try/catch` blocks, and fallback paths for impossible or highly unlikely states. Trust established invariants and internal contracts. Implement defensive handling only for realistic failure scenarios.
- **Boundary Validation:** Validate untrusted input strictly once at trust boundaries. Trust previously validated internal data unless it crosses an external boundary.
- **Abstraction:** Avoid premature or unnecessary abstractions, especially for single-use logic. Only extract helpers when they materially improve readability, encapsulate inherent complexity, or represent well-defined domain concepts.
- **Error Handling:** Handle errors where they can be meaningfully recovered from or enriched with context. Do not silently swallow errors or introduce speculative fallback mechanisms.

---

## 2. README.md Maintenance Boundaries

`README.md` is exclusively reserved as a user-facing and contributor-facing portal. **Never** treat it as a scratchpad, task log, or output dump.

- **Prohibited Content:**
  - Execution steps, debugging logs, or intermediate scratchpad notes from individual tasks, features, or bug fixes.
  - Raw command outputs, temporary scripts, test execution summaries, or build artifacts.
  - Ad-hoc task checklists, personal TODOs, or development scratch notes.
- **Permitted Core Content:**
  - Project Overview and Purpose.
  - Prerequisites and Environment Setup.
  - Installation and Getting Started Guides.
  - Build, Run, and Test Commands.
  - Architecture Overview and Core Configuration Reference.

---

## 3. README.md Update Triggers

Update `README.md` **only** when a code change alters the project's external behavior, user workflows, or environment contracts:

- **CLI / Scripts:** Added, modified, or removed CLI commands or project scripts (e.g., `package.json` scripts, `Makefile` targets).
- **Environment & Dependencies:** Introduced or modified external dependencies, environment variables, or global configuration keys.
- **Interfaces & Network:** Changed build output paths, runtime ports, exposed APIs, or core protocols.
- **Architecture:** Restructured user-facing functionality or foundational architecture.

> **Rule:** Pure internal refactoring, performance tuning, bug fixes, or code adjustments that leave external behaviors unchanged **must not** modify `README.md`.

---

## 4. Artifact & Task Output Routing

- Output task summaries, verification results, and transient logs directly to the chat conversation.
- If persistent records are required, place them in dedicated documentation paths (e.g., `docs/`, `docs/tasks/`, or `CHANGELOG.md`)—never in `README.md` or as loose files in the root directory.