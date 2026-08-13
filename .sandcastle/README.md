# Sandcastle GitHub Issue Worker 使用說明

這個專案把官方 `@ai-hero/sandcastle` 當作執行 agent 的 Docker sandbox；專案自己的
host worker 則負責 GitHub Issue、branch、project gate、review、draft PR 與可恢復
狀態。任務來源永遠是 GitHub Issue，不是手動填寫 `.sandcastle/prompt.md`。

目前 checked-in 設定刻意是**不寫入 GitHub**：`delivery.enabled=false` 且
`merge.enabled=false`。安裝或本機測試不需要特殊 GitHub 授權；要讓 worker push、
建立 draft PR 或留言，必須先由 repository owner 明確啟用下方的 host delivery。

## 實際工作流程

```text
open Issue／Sandcastle label
  → fresh implementation agent（無 GitHub credential）
  → ./scripts/package.sh
  → fresh independent reviewer（同一個 sandcastle/issue-N branch）
  → reviewer 有修正時再跑 ./scripts/package.sh
  → final SHA 驗證
  → trusted host 非 force-push 該 exact SHA
  → 建立或更新唯一一個 draft PR
  → 更新同一則有長度上限的 harness status comment
  → 等待 owner 的 revision comment 或 approval
```

Agent 看得到 Issue 內容與 project worktree，但拿不到 GitHub credential。只有 trusted
host process 能呼叫 machine-local `gh`／`git`；它不會貼出 agent prose、原始錯誤、
本機路徑、token 或未受控的 `@mention`。固定 status 也會揭露這是
「AI-generated and authorized by a human」，且最多 `1200` UTF-8 bytes。

每個 Issue 固定使用 `sandcastle/issue-N`。Implementation 與 review 是兩個 fresh
agent，卻在同一個 branch 上工作。後續由設定中的 trusted actor 留下普通 Issue
comment，就會在 worker 下次處理該 Issue 時啟動 revision round；不必重新加 label，
也不會另開 branch 或第二個 PR。留言只是 durable trigger，仍需要這台受信任主機上
正在執行或再次啟動 worker；GitHub 本身不會替本機啟動 Docker。Owner 不需要自己的
Codex credential：owner 透過 Issue 授權工作，agent 使用 trusted host 上的最小 Codex
credential 執行。

## 安裝與本機驗證

需求：x64 Linux、Docker daemon、主機已完成 `codex login`。要啟用 host delivery
時，machine-local `gh` 還必須以設定中的 delivery actor 登入，且該 actor 對目標
repository 必須是 exact `WRITE` 權限；`MAINTAIN`／`ADMIN` 會被拒絕，以免 actor
具備繞過保護規則的權限。

版本固定為 Node `22.23.2`、npm `10.9.8`、`@ai-hero/sandcastle` `0.12.0`、
`tsx` `4.23.12`、Codex CLI `0.147.0` 與 .NET SDK `8.0.423`：

```bash
npm ci
npm run sandcastle:build
npm run sandcastle:verify
npm run sandcastle:test
npm run sandcastle:smoke
```

Owner 將啟用設定合併到 configured base 後，可在 trusted Linux host 安裝專用 clean
checkout 與兩分鐘 poll timer：

```bash
.sandcastle/install-host-service.sh
```

專用 checkout 位於 `${HOME}/.local/share/lol-performance-overlay-sandcastle/repository`。
每次 poll 先驗證 exact remote 並只允許 fast-forward configured base；dirty、diverged 或
remote identity 改變都 loud failure。Checked-in delivery 仍為 false 時，timer 只會走
inert path，不呼叫 `gh`、agent 或任何 GitHub mutation。

`sandcastle:smoke` 只驗證 Docker/Codex no-change 路徑，不讀寫 GitHub。它會把當下
tracked 與 nonignored untracked 檔案複製到 OS 的暫存目錄，在那裡建立乾淨的
disposable Git snapshot，並只在該 snapshot 中建立 Sandcastle branch/worktree。
真實 repository 的 `.git`、refs 與 worktrees 都不會被寫入；執行前後會以
byte-for-byte 比對兩邊的 branch、HEAD、status、refs 與 worktree list，最後只刪除
整個暫存 snapshot。因此可在本機 dirty worktree 上驗證，不會建立後再刪除
真實 repository ref。

完整 project gate 是 repository 的 canonical `./scripts/package.sh`；Linux gate 通過
仍不能替代 Windows 10/11 的 WPF、DPI、focus、SmartScreen、真實 LoL 對局或
Release 驗收。

每一輪的 durable `startSha` 都必須是本機存在的完整 commit OID。Host harness 會先
驗證它，再用 `export SANDCASTLE_ROUND_START_SHA='<exact SHA>'; <project gate>` 執行
整段 gate；因此 compound command 的每個 `&&`／`||` clause、pipeline、subshell 與
child process 都會收到同一個 immutable round-start SHA，而不是當下可能已前進的
candidate `HEAD`。

## 預設模式：零 GitHub mutation

目前 [project.json](project.json) 已固定以下 identity，worker 會以 immutable node ID
和 exact URL 驗證，不只比對可能改名的 login：

- Repository：`weib10/lol-performance-overlay`（`R_kgDOTnQLIg`）
- Owner／trusted actor：`weib10`（`U_kgDOBZXTGw`）
- Host delivery actor：`brant92good`（`MDQ6VXNlcjc2ODg0MTc3`，exact `WRITE`）
- Exact fetch／push URL：`https://github.com/weib10/lol-performance-overlay.git`
- Base／queue／branch：`agent/linux-usability-release`、`Sandcastle`、`sandcastle/issue-`

在 `delivery.enabled=false` 時，普通執行必須保持 inert：不留言、不改 label、不 push、
不建立／更新 PR，也不 merge。這是 ownership boundary：`brant92good` 的 collaborator
權限只證明 GitHub 允許寫入，不等於 repository owner 已授權這個自動流程。

## Repository owner 啟用 host delivery

Owner 審查程式與上方 immutable identity 後，才將 [project.json](project.json) 改為：

```json
"delivery": {
  "enabled": true
}
```

提交這項設定後，每次實際允許寫入仍要在 trusted host 明確傳入第二把鑰匙：

```bash
# 指定 open Issue；不要求預先加 Sandcastle label
npm run sandcastle -- --issue 42 --allow-delivery

# 選擇 createdAt 最早的 open + Sandcastle Issue
npm run sandcastle -- --allow-delivery
```

缺少 checked-in `delivery.enabled=true` 或 `--allow-delivery` 任一項時，都不能執行
GitHub mutation。啟用時 trusted host 必須位於乾淨的
`agent/linux-usability-release`，而且本機 `HEAD` 必須
逐位等於 host 重新讀取的 GitHub base SHA；因此 collaborator 自己的 local commit
不能冒充 owner 授權。啟用後，host worker
也只能在通過 implementation、project gates、
independent review 和 final-SHA verification 後：

1. 以一般 non-force push 將 exact candidate SHA 推到固定 Issue branch；remote 若有
   非預期漂移就停止，不能覆蓋。
2. 依 exact head branch 查詢所有狀態的 PR；零個才建立 draft PR，一個就更新／復原
   它，多於一個就停止。
3. 更新固定 marker 的 bounded harness comment，讓重啟不會重複新增留言。

Worker 以單一 process lock 排除同 repository 的並行執行，並在每個外部 side effect
前先把 intent 寫入 atomic、durable state。State 固定在 host-only
`${OS_HOME}/.local/state/lol-performance-overlay-sandcastle/state.json`（`0600`），lock
是同目錄的 `worker.lock/`（`0700`）；兩者永不掛載進 sandbox。這個 lock 依 immutable
repository identity 跨 clone／worktree 共用。若主機在 push、PR 或 comment 後崩潰，
重跑同一命令會先觀察 GitHub／Git 現況再 reconcile，而不是盲目重做；工具不印出
展開後的 home 絕對路徑。人工改動造成 remote SHA、PR 數量或 identity 不一致時會
loud failure，保留 Issue 與證據供人判斷。

## Revision 與 merge approval

成功交付 draft PR 後，trusted actor 的一般 Issue comment 會啟動下一輪：fresh
implementation agent → gate → fresh reviewer → final gate，沿用同 branch 和同 draft
PR，不需要重新加 `Sandcastle` label。其他 actor 的留言不是工作授權。

Merge 預設停用，而且不能只靠 Issue 留言打開。Owner 必須先明確提交：

```json
"delivery": {
  "enabled": true
},
"merge": {
  "enabled": true,
  "method": "SQUASH",
  "requiredChecks": ["<owner-confirmed required check name>"]
}
```

執行者還必須同時給兩個 runtime flags：

```bash
npm run sandcastle -- --issue 42 --allow-delivery --allow-merge
```

最後，設定中的 trusted actor 必須留下一則 body **byte-for-byte 等於**下列內容的
Issue comment；前後空白、Markdown code fence、其他文字或不同大小寫都不是 approval：

```text
/sandcastle approve
```

任何不是 exact approval 的 trusted comment（例如尾端多一個空白的
`/sandcastle approve `）都按普通 revision 處理，不會被當成「差不多等於核准」。

Merge 前，host 會重新讀取並確認 approval 未變、repository identity 未變、Issue
仍開啟、唯一 PR 的 base/head 未變、checked-in 明列的 required checks 各自唯一且
通過，並確認沒有未消耗的 trusted revision comment；若需
把 draft 改為 ready，改完會再讀一次。最後只用已驗證的 head SHA 做 compare-and-swap
merge。任一步漂移就停止，不使用 force、admin merge 或 auto-merge。

若 approval 抵達時 config 或 runtime merge key 仍關閉，worker 會把這一則 approval
記為已消耗的 no-op，避免舊授權日後被意外重播；它不會阻塞後續普通 revision
comment。日後真的要 merge，owner 必須在最新 round 通過後另留一則新的 exact
approval。

## 固定不在 worker 權限內的動作

無論 delivery／merge 如何設定，worker 都不會關閉 Issue、deploy、release、publish、
刪除 branch、force-push、使用 admin merge 或開啟 auto-merge。Issue（包含 qualification
Issue #4）保持 open，除非人類另行、個別授權並親自處理。

## Credential 邊界

- Docker image 沒有 `gh`；不掛載 `~/.config/gh`、整個 `.codex`、整個 home 或 Docker
  socket，也不向 agent 注入 GitHub、GitHub App 或 Copilot credential。
- Agent 只取得完成工作所需的最小 Codex credential：優先使用呼叫端明確提供的
  `CODEX_ACCESS_TOKEN`，否則只掛載單一 `~/.codex/auth.json`。
- 每一次 host `gh`／`git` child process 都使用受控 OS home，並移除 GitHub token、
  GitHub App、Copilot、host/repository routing、`gh` config、Git config／worktree、
  askpass／SSH command、proxy／custom CA 及 Codex/OpenAI secret overrides。Host Git
  push 前會重新驗證 remote 與 local config，硬停用 hooks，並拒絕 replacement refs／
  grafts；呼叫端或 agent 因此不能改寫 machine-local identity／transport／ancestry。
- 若 Codex sandbox 內的 `gh auth status` 失敗，該結果不具判定力；回到主機重跑。
  只有主機也失敗時才執行 `gh auth login`，且永不改用 GitHub app／connector。

Sandcastle Docker provider 仍會可寫掛載 Issue worktree 和必要 Git metadata，因此它
適合受信任的 project Issue，不應描述成可安全執行任意惡意程式碼的 hermetic boundary。
