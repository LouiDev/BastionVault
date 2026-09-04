@AGENTS.md

Claude Code loads this file automatically; the actual briefing is `AGENTS.md` (imported
above) so that other agents and humans read the same text. Two rules worth repeating here
because they are the ones most often broken by default behaviour:

- Do not commit, and never push, unless the maintainer asks for it in the current session.
- Work on `feature/*` branches from `dev`; pull requests target `dev`; `main` is releases only.
