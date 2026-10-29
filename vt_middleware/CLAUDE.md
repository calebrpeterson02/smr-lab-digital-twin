## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- When graphify-out/graph.json exists, run `graphify query "<question>"` before your first grep, search or source-file read for a codebase question, and put this rule in every subagent prompt that explores code. Read raw files only for lines the graph or an earlier read already located. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
- A committed graphify-out/graph.json is rewritten whole on every build, so a large diff to it in a pull request is expected. Do not flag it as churn or suggest skipping the graph rebuild or its hooks.

## Unity project graph

The Unity side of the digital twin (the C# scripts: bridge clients, RobotJointDriver, TwinClock, UI, camera) lives in the same git repo at `../unity`, with its own knowledge graph. That graph is scoped to `Assets/Scripts` by `../unity/.graphifyignore`.

Both `graphify-out/` folders are gitignored, so a fresh clone has no graphs. If graphify is installed, build them with `graphify update .` and `graphify update ../unity` (AST-only, no API cost); otherwise skip the graph rules below and read the source directly.

Rules:
- For questions about the Unity scripts, query that graph by passing `--graph` (relative to this directory):
  `graphify query "<question>" --graph ../unity/graphify-out/graph.json`
  The same `--graph` option works for `graphify path` and `graphify explain`.
- For questions that span both sides (e.g. a message format sent by the bridge and parsed in Unity), query both graphs: the local one for the Python bridge, the Unity one for the C# client.
- After modifying Unity scripts, update that graph: `graphify update ../unity`.
