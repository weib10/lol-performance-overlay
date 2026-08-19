# Codex 專案工作守則

## 產品方向

這個專案的目標不是維持一個「勉強可以展示的測試版」，而是做成一般朋友下載、解壓、閱讀說明後就能使用的 Windows LoL Overlay。

目前公開的 `v1.0.1-test` 只能視為歷史基準。未達到 `docs/PRODUCT_HANDOFF.md` 的正式發布門檻前，不得只靠改名稱把它稱為正式版；達標後，新的成品、ZIP、Release 標題與朋友文件也不再使用「測試版」字樣。

開始工作前先完整閱讀：

1. `docs/PRODUCT_HANDOFF.md`
2. `README.md`
3. `SECURITY.md`
4. 與任務直接相關的程式碼和測試

這個 repository 沒有自動化的 Issue worker、host poller 或 sandbox 交付流程。`main` 上
曾經有一套 Sandcastle worker（`c52fc15`），已在 2026-08-15 完整移除：它的 ownership
boundary 依賴 base branch 的 protection，而 worker 無法自行驗證，因此不再使用。不要
重新引入會自動 push、開 PR、merge 或在主機上定時執行 repository 程式碼的機制；需要
時請先和使用者確認。相關的 `agent/linux-usability-release` 與
`sandcastle/issue-worker-deployment` branch 已在 2026-08-16 刪除（所有 commit 都是
`main` 的祖先，沒有內容遺失）；`main` 現在是唯一分支，只看到一條分支是預期狀態。

該 worker 是這個 repository 唯一用過 Node 的東西，`package.json` 已隨它移除。但
`node_modules` 仍然刻意留在 `.gitignore` 和 `eng/package-config.json` 的
`scan.excludedDirectories` 裡：既有 checkout 裡可能還躺著一份舊安裝，少了這兩個項目，
release gate 的乾淨 Git tree 檢查會失敗，repository scan 也會去走那幾千個依賴檔。看到
它們不是漏刪，不要順手拿掉。

不要假設文件一定正確；以目前程式碼、實際執行結果和量測為準，發現落差時同步修正文件。

## 自動審視責任

使用者不需要先列出所有細節。每次處理功能、修正或發布前，Codex 必須主動從以下角度檢查，記錄重要發現並依嚴重度排序：

1. **使用體驗**：第一次啟動是否容易理解；所有模式是否容易拖曳、切換、找回、關閉；互動目標是否夠大；是否有 click-through 或焦點陷阱。
2. **效能**：UI 執行緒是否做網路、解析、圖片解碼或整棵重建；無變化資料是否造成重繪；CPU、記憶體、GC、更新延遲及拖曳順暢度是否合理。
3. **可靠性**：LoL 未啟動、Client 重啟、2999 暫時失效、缺欄位、離線、對局結束、多螢幕和設定損壞時能否自行恢復。
4. **安全與隱私**：不洩漏本機憑證、真實測試帳號、原始 KDA 等禁止欄位；歷史資料必須有合法來源、最小保存範圍、清楚快取期限和可關閉／失效降級設計。
5. **相容性**：Windows 10／11 x64、不同 DPI、解析度、多螢幕、全螢幕無邊框及快捷鍵衝突。
6. **可理解性與無障礙**：不能只靠顏色；提示與狀態詞要白話；朋友文件不得要求理解 LCU、2999、PUUID 等開發術語。
7. **打包與維運**：乾淨環境能否一鍵建置；版本、雜湊、文件和 Release 是否一致；ZIP 是否只有應有檔案；能否重現同一個成品。
8. **可維護性與測試性**：用小的 interface 隱藏複雜 implementation；把真正會變化的行為放在 seam 後；測試 observable outcome，不測內部細節。
9. **產品誠實性**：本場表現、歷史近期狀態、官方牌位和推測的風格必須分開呈現；不可把自製指標冒充官方 MMR／ELO、勝率預測或確定的玩家實力。第三方資料來源、樣本數、新鮮度、信心與未簽章狀態必須如實說明。

若一項修改同時影響多個角度，不得只驗證最明顯的那一個。

## 工作方式

- 先重現、量測、找根因，再實作。不能把「感覺比較順」當作效能驗證。
- UX 問題先提出至少兩種可操作方案，使用 Replay 做快速比較，再選擇實作；不要把第一個想到的互動直接固定下來。
- 對 Overlay 互動建立一個清楚的 seam，使 click、drag、click-through、lock position 和 mode switching 能以 observable behaviour 測試。
- 對更新流程建立一個清楚的 seam，使資料取得、評分、節流和 UI 更新能分開量測；網路、JSON 解析與圖片解碼不得阻塞 UI 執行緒。
- 優先保留 `ILeagueSessionSource`、`IStaticGameDataProvider`、`IPerformanceScorer`、`OverlaySnapshot` 的安全資料邊界。若要改 interface，先說明能增加什麼 leverage、locality 或 testability。
- Fixtures、測試、截圖、log 和 Issue 不得包含真實 Riot ID、LCU token 或開發者本機路徑。
- 不把原始 KDA、等級、CS 或死亡時間加入 `OverlaySnapshot` 或 UI view model。
- 2026-08-16 起，裝備價值總和是例外，可以進入 view model。原因：使用者要用它判斷 BP 與戰力差，
  而官方 Live Client Data API 本來就公開所有玩家的 `items`，單價是賽前就有的 Data Dragon 靜態資料，
  加總只是對遊戲客戶端內已有資訊做算術，落在 Riot 政策核可的 overlay 範圍。仍然禁止把原始 item 陣列
  放進 view model，只能放聚合後的總值；也必須標示為「裝備值」而非「經濟」，因為客戶端只提供本人的
  未花費金錢，把裝備值稱作總經濟會高估。
- 可以研究並使用官方 Riot API、OP.GG 公開頁面或其他歷史資料，但必須遵守下方的「歷史資料來源規則」；匿名身分還原、遊戲注入和自動輸入仍然禁止。
- 不因功能困難而靜默縮小範圍；若必須調整產品承諾，要在文件中清楚記錄原因。
- `OverlayWindow` 的 Expanded 面板用 `SizeToContent.Height`；任何用 `Visibility` 隱藏／還原的子元素（例如隊伍卡片、歷史面板）都必須用 `Collapsed`，不能用 `Hidden`——`Hidden` 仍保留版面空間，會讓視窗卡在最高狀態，在對局結束等「應該收起空間」的時刻反而最明顯。
- Push 之後要實際確認 CI 結果，不能以「本機建置與測試都過」當作驗證完成。本機沒有跑 release scan 這道 gate（敏感字串、開發者路徑、raw overlay 欄位等），只在 CI 上執行。

## 歷史資料來源規則

- 歷史資料的產品目的，是描述可見玩家的近期表現、常用英雄／位置與風格；不得分析 Riot 刻意隱藏的玩家，也不得建立自製 MMR／ELO 或替代官方天梯。
- 建立小而明確的歷史資料 interface，至少要有 `Synthetic` 測試 adapter、正式 live adapter 和 unavailable／failure fallback。假資料可完整驗證 UI、評分、極端值與樣本不足，但正式 package 不得把假資料冒充真人資料。
- 歷史 profile 至少包含來源、取得時間、queue／mode、樣本數與信心。UI 必須把「本場即時表現」和「歷史近期狀態／風格」分開，不能混成一個無法解釋的總分。
- Riot API key 不得寫入原始碼、設定範例、log、fixture、EXE 或 ZIP。公開散布若依賴 Riot API，必須使用符合 Riot 規則的 Production Key 架構；不能把 development／personal key 藏進朋友版。
  這條規則限制的是「散布給多個朋友的同一份版本」。使用者自己申請、只存在自己電腦本機設定（不進 git、不進打包）的 Personal key，供申請者本人或小型私人社群使用，是 Riot 官方文件明確允許的用法，不要因為這條規則而拒絕實作「本機貼入自己 key」這種功能；只要確保 key 真的不會出現在打包產物或 repository 裡。
- 歷史 profile 允許只有官方牌位、沒有對局風格樣本的狀態（`HistoricalProfile.PlayStyle` 可為 `null`）。這代表「資料來源沒有比賽紀錄可算」，不是實作疏漏；不要為了滿足非空檢查而編造風格讀數。
- OP.GG 只能透過有明確允許依據的公開資料方式使用；不得依賴未公開私有端點、繞過存取控制或高頻抓取。必須標註來源、限制頻率、加快取，並在 OP.GG 失效時讓核心 Overlay 正常運作。
- OP.GG 的 Help Center 與網站條款對 scraping 存在文字衝突；在取得較明確授權前，不能把 scraping 當成唯一正式資料來源。詳見 `docs/HISTORICAL_DATA_RESEARCH.md`。
- 新資料來源要有 schema／fixture、timeout、cancellation、rate-limit、cache、stale-data 和 malformed-response 測試，且任何網路工作不得阻塞 UI thread。

## 每次 UX／效能修改的最低驗證

- Dot、Compact、Expanded 都能在直覺區域拖曳。
- Dot 能分辨 click 與 drag；拖曳不觸發模式切換，click 不改變位置。
- 所有模式都不搶 LoL 焦點；按鈕可點，其餘指定區域符合 click-through／拖曳規則。
- 拖曳跨螢幕與 DPI 後不消失，重新啟動會回到合理位置。
- Replay 連續執行至少 30 分鐘，記憶體不持續成長，畫面更新和拖曳沒有可見停頓。
- 有量測資料比較修改前後的 UI 更新耗時、CPU、配置／GC 或圖片解碼次數。
- 新行為有自動測試；不能只靠一張截圖。

## 每次發布的最低驗證

正式 Release 必須由 repository 內的自動化腳本或 CI 產生，不可依賴 repository 外的私人腳本。

發布流程至少要自動完成：

1. 還原依賴、Release 建置與全部測試。
2. 產生 .NET 8 x64 自包含單檔 EXE。
3. 產生完全離線、自含圖片與樣式的朋友說明 HTML。
4. 自動把實際 EXE SHA-256 寫入說明與發布資訊。
5. 產生只包含 `LoL即時表現Overlay.exe` 和 `先看這裡.html` 的 ZIP。
6. 掃描真實 Riot ID、憑證、Token、本機路徑、PDB、原始對局欄位和非預期網域。
7. 驗證版本號、檔名、README、HTML、Git tag 與 Release 一致。
8. 在乾淨 Windows 10／11 使用者環境完成啟動、SmartScreen、拖曳、對局生命週期、結束與移除驗收。

若任一必要門檻未通過，只能產生內部候選包，不得發布成穩定版。

## 完成定義

「完成」不等於能編譯或 Replay 看起來正常。只有在功能、UX、效能、真實對局、隱私、乾淨機器安裝／移除與可重現打包都符合 `docs/PRODUCT_HANDOFF.md` 時，才有資格發布第一個不含 `test`、`測試版` 或 `prerelease` 的版本。

## Agent skills

### Issue tracker

Issues 追蹤在 GitHub Issues（`weib10/lol-performance-overlay`），透過 `gh` CLI 操作。詳見 `docs/agents/issue-tracker.md`。

### Triage labels

沿用五個標準 triage 標籤原名：`needs-triage`、`needs-info`、`ready-for-agent`、`ready-for-human`、`wontfix`。詳見 `docs/agents/triage-labels.md`。

### Domain docs

單一 context 佈局：`CONTEXT.md` + `docs/adr/`（尚未建立）。詳見 `docs/agents/domain.md`。
