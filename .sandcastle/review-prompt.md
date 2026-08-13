# Independent Review Assignment

Review the implementation of this single GitHub issue as a fresh agent.

<github-issue-json>
{{ISSUE_JSON}}
</github-issue-json>

- Issue branch: `{{ISSUE_BRANCH}}`
- Base commit: `{{BASE_COMMIT}}`

# Review Gate

1. Read `AGENTS.md` and the project documents it requires.
2. Inspect `git log {{BASE_COMMIT}}..HEAD` and
   `git diff {{BASE_COMMIT}}...HEAD`. Check both issue acceptance criteria and
   repository standards, including tests, product honesty, privacy, reliability,
   performance, packaging, and maintainability where relevant.
3. If you find a concrete defect, fix it on this branch, run the relevant tests,
   and commit the correction. If the implementation is already correct—or the
   issue is already satisfied by an intentional empty implementation commit—
   leave the branch unchanged.
4. Leave the worktree clean. The host harness reruns the canonical project gate
   if you commit a correction.

GitHub operations stay on the host. Do not use `gh`, push, merge, close or
relabel issues, deploy, publish, create a Release, or print credentials.

# Completion

Emit one concise Traditional Chinese plain-text verdict (maximum 2,000
characters; technical names may stay in English), beginning with `APPROVED:` or
`CORRECTED:`. Do not include credentials, user mentions, real player identities,
or developer-machine paths:

<review-report>APPROVED: your verdict</review-report>

Then output `<promise>COMPLETE</promise>` and stop.
