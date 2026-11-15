---
name: docs
description: Rules for writing, reviewing or trimming this repo's documentation (README, ARCHITECTURE, CLAUDE.md, skills, test READMEs). Use when adding or changing a doc, when a code change makes a doc wrong, or when asked to review or clean up docs.
---

# Writing docs for this repo

Sources: Google's documentation best practices ("minimum viable documentation"),
Anthropic's guidance on CLAUDE.md and skills, and the Diátaxis framework.

## Rules
1. **Keep the docs few, short and true.** A small set of fresh docs beats a large stale
   one. If a doc no longer applies, delete it, because git history keeps it.
2. **Give each file one job** (Diátaxis):
   - README orients the reader.
   - The skills are how-to guides.
   - ARCHITECTURE explains the design.
   - The code is the reference.
   Don't mix these jobs in one file.
3. **Link to the source of truth instead of copying it.** Config defaults live in the
   XML comments of `src/Server/Config/*.cs`, production values in `infrastructure/main.bicep*`,
   and the schema in `DatabaseSchema.cs`. A copied table goes stale; a link doesn't.
4. **Describe only how things work now.** No history, PR numbers, rollout steps,
   "before/after" measurements or dated status lines. Those belong in commits and PRs.
   A finished design spec gets deleted.
5. **Update the docs in the same change as the code.** If a change makes a sentence
   false, fix or delete the sentence in that PR.
6. **Write for someone about to act.** Use commands they can copy, then one line on why
   if it isn't obvious. Cut anything a competent reader or agent already knows, such as
   generic git, C# or SQL advice.
7. **CLAUDE.md and skills hold only what the code can't show:** commands, decisions,
   traps and hard rules. Never describe the file layout there. Keep CLAUDE.md under
   about 40 lines.
8. **Style:** sentence-case headings, active voice, short paragraphs, plain words. Make
   relative links that resolve.

## Review checklist
- Does every command, path, port, URL and setting named still exist? Grep for it.
- Is anything copied from code or config that could just be linked?
- Is there history or a measurement that should go?
- Do two docs say the same thing? Keep it in one place.
- Reader test: give a fresh subagent only the doc and 3–5 questions a newcomer would
  ask, such as "how do I run it" or "how does a deploy work". Fix what it gets wrong.
