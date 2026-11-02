# AGENTS.md

## Avoid Over-Defensive Programming

- **Complexity:** Don't add complexity for speculative edge cases. For significant additional complexity, explain the failure scenario, likelihood, costs, and benefits, and get confirmation before proceeding.
- **Defensive programming:** Avoid speculative checks, redundant validation, unnecessary try/catch blocks, and fallback paths for impossible or unlikely states. Trust established invariants and internal contracts. Add defensive handling only for realistic failure scenarios.
- **Boundary validation:** Validate untrusted input once at trust boundaries. Trust already-validated internal data unless it crosses a new boundary.
- **Abstraction:** Avoid unnecessary abstractions, especially for single-use logic. Extract helpers when they improve clarity, encapsulate complexity, or represent meaningful concepts.
- **Error handling:** Handle errors where they can be meaningfully recovered from or contextualized. Don't silently swallow failures or add speculative fallback paths.
