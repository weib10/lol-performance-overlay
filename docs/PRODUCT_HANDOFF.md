# LoL 即時表現 Overlay：產品與 Codex 交接文件

更新日期：2026-08-20

> 這份文件是累積寫成的，第 1–14 節多數段落寫於 2026-08-10 或更早，第 15–17 節是後續
> 里程碑。閱讀時請注意：較早的段落描述問題或計畫時，若後面的里程碑已經處理，內文會加
> 上「[狀態：...]」標記並指向對應章節；沒有標記的段落視為仍然有效。最新狀態一律以第 17
> 節與目前程式碼為準，不要只看章節編號較前面的敘述就當作現況。

## 1. 交接目的

這份文件交給下一個 Codex 使用。它不是單純的程式架構摘要，而是後續產品工作的基準：目前有什麼、使用者實際覺得哪裡難用、可能的根因、應該往哪裡走，以及什麼條件達成後才能稱為真正可用的正式版。

使用者目前沒有完整的介面設計答案，這不是阻塞條件。下一個 Codex 的責任是主動觀察、量測、提出替代方案、使用 Replay 驗證，再和真實使用結果迭代，而不是等待使用者列出每一個小問題。

## 2. 一句話產品目標

做一個幾乎不打擾 LoL、朋友下載後能直接理解和使用，同時分開呈現「本場即時表現」與「可解釋的歷史近期狀態／風格」的 Windows Overlay。

成功時應有以下感覺：

- 平常忘記它存在，需要時一秒看懂。
- 想移動、展開、縮小、找回或關閉時，不需要猜哪裡能點。
- 不會因為 Overlay 更新而讓遊戲或桌面感覺卡頓。
- 朋友不需要安裝 SDK、輸入帳密、看命令列或理解 Riot 本機介面。
- 下載包、說明、安全邊界與移除方式都清楚可信。

## 3. 目前狀態

- Repository：`https://github.com/weib10/lol-performance-overlay`
- 主要分支：`main`（唯一分支；2026-08-16 起 `agent/linux-usability-release` 與相關 PR branch 已刪除，見第 16 節）
- 現有公開基準：`v1.0.1-test` prerelease
- 平台：Windows 10／11 x64
- 技術：.NET 8、WPF、自包含單檔 EXE
- 顯示模式：Dot、Compact、Expanded
- 資料來源：League Client 本機資料、遊戲內 `127.0.0.1:2999`、Riot 靜態素材
- 歷史資料：已有 source-neutral model、Synthetic provider 與 unavailable／policy-disabled fallback；沒有合規 live provider 時不查真人資料，OP.GG 僅提供使用者主動開啟的瀏覽器連結。2026-08-16 起 model 額外支援「只有官方牌位、沒有對局風格樣本」的狀態，見第 16 節。[狀態：`14945ec`（2026-08-17）已接上真正的 live provider——`RiotHistoricalProfileTransport` 加上 Settings 裡的 Riot Personal API key 輸入欄位，key 留白時仍是 unavailable／policy-disabled。第 17 節在此基礎上把牌位鋪到 Expanded 每一列。]
- 自動測試：核心測試已拆成 `net8.0`，可在 Linux 或 Windows 執行；本機開發機（Windows）已確認可直接建置、跑測試、執行 WPF shell（SDK 位於 `%LOCALAPPDATA%\Microsoft\dotnet`，PATH 上的 `dotnet.exe` 可能指向沒有 SDK 的版本，需留意），不必每次都繞 CI。CI 目前只在 `windows-latest` 執行。
- 候選包：repository 內的共用 PackageBuilder 由 `scripts/package.sh`（Linux／WSL 可用）與 `scripts/package.ps1`（Windows）共同呼叫，產生 EXE + 完全離線 HTML 的兩檔 ZIP、manifest 與 SHA-256。CI（`.github/workflows/windows-package.yml`）只呼叫 Windows 入口。
- 現有 Release 不應直接重新命名為正式版；它是比較修正前後行為的基準。
- `outputs/` 目錄下若有既有 package，可能是很舊的建置（例如仍記錄 2026-08-11 前的 commit）；展示或分享前務必重新打包，不要假設它反映目前程式碼。

目前已知還沒有足夠證據證明：真實長時間對局順暢、真實滑鼠拖曳手感、多螢幕與 DPI 完整、Client 重啟可靠、一般朋友能無協助完成所有操作，以及乾淨環境可重現打包。2026-08-16 起，已有本機 Windows 建置的實際畫面截圖與版面尺寸驗證（見第 16 節），但那是靜態渲染與視窗尺寸的驗證，**不包含**實際滑鼠拖曳、焦點搶奪、click-through 或多螢幕／DPI 測試；不要把「畫面截圖看起來對」誤讀成「互動已驗證」。

## 4. 使用者實測後的核心回饋

使用者的結論不是「少幾個功能」，而是距離真正好用仍很遠：

1. 整體用起來太卡。
2. 視窗只能在左上角有文字的狹小區域拖動，拖曳入口不直覺。
3. 縮成 Dot 後完全不能拖動。
4. 存在很多類似的小摩擦；每一個單看都不大，累積後讓產品特別難用。
5. 使用者暫時無法列完所有問題，希望 Codex 能從多個角度主動審視，而不是只修被點名的項目。
6. 最終要能自動打包成朋友下載就能用的 package，不再長期稱為測試版。
7. 使用者沒有禁止 OP.GG 或歷史戰績；希望能用歷史資料描述玩家強度、近期狀態和風格，若不同來源差異不大，可以選成本較低且資訊足夠的方案。
8. 歷史功能可以先用假資料完整測試，但最終正式版必須接上可用、合規且能降級的 live 資料來源，不能停在 mock demo。
9. 2026-08-11 希望看到的是能實際下載、操作和驗收的完整候選版本，不是只交分析、介面稿或半成品。

後續不應把這些拆成互不相干的零碎 bug。它們共同指向兩個 P0 問題：

- Overlay 互動模型沒有被當成一個完整產品設計。
- 更新／渲染流程沒有以低延遲、低配置和 UI 執行緒安全為核心。

### 4.1 2026-08-16 第二輪實測回饋

使用者在本機看到實際畫面後，提出以下具體回饋（依提出順序）：

1. 選角希望能分紅藍方，且記得選角順序（換位置後才不會忘記對面先選後選，方便猜 BP）。
2. 設定視窗點不到——因為 Overlay 視窗固定在最上層，設定視窗開在它下面被蓋住。
3. 想顯示官方牌位。
4. 字太小，看是換字體、放大或加粗比較舒服。
5. 放大後 Expanded 面板有太多無用空間。
6. 希望顯示身上裝備加總的價值。
7. 收到第一版畫面後：面板還是太吵、字不夠顯眼、佔用空間仍嫌多，要求參考 op.gg app 的精簡風格重做。

處理結果與各項目對應章節見第 16 節；牌位（第 3 項）依使用者指示暫緩，只完成資料模型的地基（見第 16 節「牌位資料模型」）。

## 5. 已知實作與可能根因

以下是從目前程式碼得到的高可信假設；下一個 Codex 必須先用 profiler、計時與操作重現確認，不能把假設直接當結論。

### 5.1 畫面更新可能造成卡頓

[狀態：`e0d287a`（早於本文件初版）已處理此架構面問題。`OverlayWindow` 現有兩個 `ApplySnapshot` 多載，其中一個接受 `OverlaySnapshotDiff` 並只更新變化欄位，不是每次都重建整棵樹。以下描述的是修正前的舊行為，保留作為問題脈絡，不代表目前程式碼。]

`OverlayWindow.ApplySnapshot()` 每收到一次 snapshot 就呼叫 `Render()`，而 `Render()` 會依模式重新建立整棵 WPF visual tree。遊戲資料目前約每秒更新一次，即使顯示內容變化很小，也會重新建立 Border、Grid、TextBlock、Button、Image 等物件。

`ChampionVisual()` 每次渲染還會重新：

- 檢查圖片檔案是否存在。
- 建立 `BitmapImage`。
- 從磁碟載入並解碼圖片。
- 建立新的 Image、Clip 與視覺物件。

這可能造成持續配置、圖片解碼、GC 和 UI thread 停頓。Expanded 模式十名玩家的負擔最大，但 Dot 模式目前也會因每個 snapshot 重新建立按鈕。

### 5.2 資料流程可能回到 UI 執行緒

Session loop 從 WPF startup 啟動，async iterator、HTTP await、JSON parsing、英雄資料處理、評分與 snapshot 套用之間沒有一個明確的 background-processing seam。需要量測哪些 continuation 實際在 UI thread 上執行。

目標不是到處加 `Task.Run`，而是建立清楚的更新 module：背景取得與計算、合併／節流、最後只把最小 UI diff 送到 Dispatcher。

### 5.3 拖曳與 click-through 是脆弱的 Tag 規則

[狀態：已有 `PointerInteractionStateMachine`、`OverlayInteractionPolicyRules`／`OverlayInteractionPolicy` 這組可測試的 seam 取代純 Tag 規則，`src/LolPerformanceOverlay.Core/Interaction/` 下有對應測試。這代表**設計上**的可測試性問題已處理；不代表真實滑鼠拖曳手感、click-through 或多螢幕/DPI 已經過真機驗證——這些仍是未完成項目，見第 16 節。以下描述的是修正前的舊行為，保留作為問題脈絡。]

目前 WPF hit testing 透過 `Tag = "Drag"` 和 `Tag = "Interactive"` 配合 `WM_NCHITTEST`：

- Header 才被標成 Drag。
- 一般內容會回傳 click-through。
- Dot 的圓點本身是 Interactive button。

因此 Dot 沒有剩餘可拖曳區；Compact／Expanded 的可拖曳感受也被限制在 Header，而且按鈕、文字、背景和 click-through 之間沒有清楚的一致手勢。

這不應只靠在更多元素上補 Tag。需要一個能測試的 pointer interaction state machine，明確處理：

- 按下位置。
- 移動距離門檻。
- click 與 drag 的區分。
- mouse capture。
- 模式切換是否應發生。
- 拖曳結束後的位置保存與螢幕校正。
- 位置鎖定時的 click-through 行為。

### 5.4 歷史資料的產品與資料來源 concern

歷史資料不是被禁止的方向，但不能直接把「抓得到」等同於「適合放進公開朋友版」。目前需要處理：

- Riot 官方 API 適合取得牌位與 Match-V5 對局資料，但 API key 不得放入要散布的 EXE；development key 會過期，personal key 也不能供公開 alpha／beta 使用。公開產品通常需要 Production Key，以及能保護 key 的 server-side 或等價架構。
  [補充，2026-08-16] 這條規則限制的是「散布給朋友的版本」。如果 key 是使用者自己申請、只存在自己電腦的本機設定（不進 git、不進打包），且產品仍只供申請者本人或極小型私人社群使用，Riot 官方文件明確允許：「Personal API keys should be used for products that are intended for just the developer or a small private community.」兩種情境不要混為一談：本機、自己用、自己的 key＝可以；同一把 key 隨公開發布的 EXE 給多個朋友用＝不行，即使技術上是「手動貼上去」也一樣違反「public consumption」限制。Development key 每 24 小時失效需手動重置，Personal key 沒有這個限制但需要走一次線上申請表單（非 Production 那種人工審核）。
- Riot 的遊戲完整性規則禁止分析刻意隱藏的玩家，也禁止自製 MMR／ELO 等官方天梯替代品。因此歷史功能應呈現官方牌位、近期樣本、風格與不確定性，而不是宣稱算出真正實力或隱藏分數。
- OP.GG 第一方 Help Center 表示一般不禁止低頻 crawling／scraping，但其網站使用條款又有禁止 scraping 的文字。兩者衝突，需保守處理、標註來源並避免把它當唯一正式依賴。
- OP.GG 沒有把內部遊戲資料 API 提供給第三方；抓頁面或未公開端點會有版面／schema 變更、封鎖、資料新鮮度、rate limiting 和維護成本。
- 同時查十名玩家可能拖慢進場並製造額外流量。歷史載入必須非阻塞、有 timeout、cache、stale 標記、部分結果與完全失效 fallback。
- 不同 queue 的資料不能不加區分地比較。ARAM、單雙排、彈性與一般對戰應分開呈現；樣本太少時顯示「資料不足」，不能硬算結論。

完整來源與政策研究記錄在 `docs/HISTORICAL_DATA_RESEARCH.md`。下一個 Codex 應以該文件和最新第一方規則為準，政策有變動時更新研究記錄。

## 6. 目標互動原則

下一個 Codex 應先提出至少兩種完整互動方案並用 Replay 做原型比較。以下是產品要求，不是唯一指定版面：

### Dot

- 圓點本體任何位置都能按住拖曳。
- 移動未超過約 4–6 px 才視為 click 並展開。
- 開始拖曳後不得同時觸發模式切換。
- hover／游標或短提示要讓人知道可以拖。
- 不能只靠顏色傳達領先狀態；tooltip 或可展開資訊需有文字。

### Compact／Expanded

- 除了真正的 Button、Slider、TextBox 等控制項外，卡片大部分背景都應可拖曳。
- 提供可看見但不搶注意力的拖曳 affordance，例如細小 grip 或拖曳游標。
- 可選擇「鎖定位置」；為避免單一 HWND 的跨程序背景穿透留下輸入陷阱，鎖定後整個 Overlay 都不接收滑鼠，並由快捷鍵／系統匣解除鎖定或切換顯示。未鎖定時控制項仍可點，其餘背景使用一致拖曳手勢。
- 縮小、展開、設定與關閉的控制目標不能過小。
- 面板切換尺寸後不能跳到螢幕外。

### 找回與失敗復原

- 系統匣永遠能顯示／重設位置／開啟設定／結束。
- 快捷鍵衝突時要有可理解的提示，不能只靜默改成另一組。
- 多螢幕拔除、DPI 改變或解析度改變後要自動回到可見工作區。

## 7. 效能目標與量測

先建立可重現的 Replay benchmark，再優化。至少記錄：

- snapshot 取得到 UI 呈現的耗時。
- Dispatcher 排隊時間與 UI update 時間。
- 每次更新配置量、Gen 0/1/2 GC 次數。
- 圖片實際解碼次數。
- Dot、Compact、Expanded 各自的 CPU 與記憶體。
- 拖曳時的 frame pacing 或肉眼可見停頓。

第一個正式版的建議門檻：

- Dot 閒置時平均 CPU 低於 1%，Expanded 更新時平均低於 2%（在文件記錄參考硬體）。
- 一次 UI 更新的 P95 低於 50 ms，不出現超過 100 ms 的可見停頓。
- 拖曳期間維持接近螢幕更新率，沒有明顯跳格。
- 30 分鐘 Replay／真實對局後，記憶體不呈現持續線性成長；暖機後增幅建議小於 10 MB。
- 相同圖示載入一次後必須快取，不因每秒 snapshot 重複解碼。
- snapshot 內容沒有可見變化時，不重建 UI。

數字若因合理技術限制需要調整，必須留下量測、硬體與理由，不能直接刪除門檻。

## 8. 建議的 module 與 seam

這不是要求增加大量 interface，而是把真正複雜、會被多處使用和需要替換測試的行為集中起來。

### Overlay presentation module

提供小的 interface，例如接受新的 `OverlaySnapshot`、切換模式、顯示／隱藏與重設位置；implementation 保有長生命週期 visual tree，只更新變化的 view state。呼叫者不需要知道 WPF control 如何組成。

### Overlay interaction module

把 pointer event 轉成 `Click`、`BeginDrag`、`DragTo`、`EndDrag` 等 observable outcome。正式 adapter 接 WPF／Win32，測試 adapter 輸入座標與時間。這個 seam 應能直接驗證 Dot click-vs-drag 和位置鎖定。

### Session update pipeline module

把 session frame 取得、評分、去重／合併與 UI dispatch 組合成一個深 module。正式 adapter 使用 League 本機資料，Replay adapter 用固定時間或可控制 clock。UI 只看到節流後的 snapshot。

### Historical profile module

提供小的歷史 profile interface，回傳官方牌位、queue-specific 近期樣本、可解釋的風格向量、來源、新鮮度與信心。至少有 Synthetic adapter、正式 live adapter 和 unavailable fallback。OP.GG 或 Riot API 的 transport、HTML／JSON schema、rate limit、cache 與 retry 都留在 implementation 裡，不洩漏到 UI。

### Package builder

在 repository 內提供一個有單一入口的打包 module／腳本。它隱藏 publish、文件雜湊替換、ZIP、掃描和 manifest 細節；本機與 CI 呼叫同一個入口，避免兩套流程漂移。

不要為只有一個 implementation 且沒有測試替身的行為建立空洞 interface。需要 seam 時，至少要有正式 adapter 和測試／Replay adapter。

## 9. 優先工作順序

### P0：讓 Overlay 本身可操作且不卡

1. 建立 Replay 效能基準與簡單 profiling 輸出。
2. 重現 Dot、Compact、Expanded 的拖曳與焦點行為。
3. 原型比較至少兩種 click／drag／click-through 模型。
4. 將資料取得與計算移出 UI thread。
5. 改成長生命週期 visual tree 或 binding，只更新變化欄位。
6. 加入圖片快取與 snapshot 去重／合併。
7. 加入三模式互動自動測試與 30 分鐘 soak test。

### P1：真實使用可靠性

1. 真實 ChampSelect → Loading → InGame → EndOfGame 驗收。
2. LoL 未啟動、Client 重啟、2999 短暫失效後自動恢復。
3. 多螢幕、不同 DPI、解析度切換與螢幕拔除。
4. 快捷鍵衝突提示、位置鎖定、系統匣重設位置。
5. 遊戲全螢幕無邊框下不搶焦點、不攔截不該攔截的輸入。

### P1：歷史近期狀態與風格

1. 先用 Synthetic adapter 建立完整 UI、fixture 與極端情況測試。
2. 定義 queue-specific profile：官方牌位、近期勝負／表現樣本、常用英雄／位置、激進度、生存、團隊參與和發育傾向；每一項都要能解釋來源。
3. 本場分數與歷史 profile 分開顯示；不得產生自製 MMR／ELO，也不得用單一「強／爛」標籤遮蔽樣本與不確定性。
4. 若已有 Riot Production API 架構或 OP.GG 明確允許，接上一個可公開使用的 live adapter；若沒有，不得為了趕日期偷偷 scraping，改交付 `PolicyDisabled`／`Unavailable` 狀態和「在瀏覽器開啟 OP.GG」的選用連結。
5. 對十名可見玩家做併發上限、cache、timeout、partial result、stale result、rate-limit 和完全離線測試。
6. 歷史來源失效不得拖慢或破壞本機即時 Overlay。

### P1：可重現打包與發布

1. 把現有 repository 外的打包流程搬進 `scripts/` 或等價目錄。
2. 一個命令完成 restore、test、publish、HTML、hash、scan 和 ZIP。
3. GitHub Actions 使用同一入口產生 candidate artifact。
4. 版本由單一來源產生，Assembly、User-Agent、README、HTML、ZIP、tag 和 Release 一致。
5. 在乾淨 Windows 使用者帳號驗證下載、解壓、SmartScreen、啟動、結束與移除。

### P2：正式產品整理

1. 補上明確的開源或使用授權 `LICENSE`。
2. 評估程式碼簽章；若暫時無法簽章，必須和非技術使用者實測 SmartScreen 流程。
3. 將特殊英雄 override 移到有 schema、可驗證、可隨版本更新的資料檔。
4. 提供只包含非敏感狀態的手動診斷複製功能，不自動上傳。
5. 完成 friend-facing 文件的可用性測試與文字精簡。

## 10. 正式版發布門檻

只有下列條件全部完成，才發布第一個不含 `test`／`測試版`／`prerelease` 的版本：

### 功能與 UX

- 三種模式都可直覺拖曳；Dot click 與 drag 不互相誤觸。
- 系統匣、快捷鍵、設定、重設位置與結束都可由非技術使用者完成。
- 選角、載入、遊戲中、結束生命週期至少在兩台不同電腦完成真實驗收。
- 兩種 DPI／解析度與多螢幕情境通過。
- 一名未參與開發的朋友能只看 `先看這裡.html` 完成安裝、操作、找回、退出和移除。
- 啟用 live 歷史 provider 時，profile 清楚顯示資料來源、queue、樣本數、新鮮度與信心，且和本場分數有明顯視覺區隔；沒有合規 provider 時，功能必須明確隱藏或顯示 unavailable，不能假裝已有真人資料。

### 效能與可靠性

- 達到第 7 節量測門檻，或有經記錄且合理的新門檻。
- 30 分鐘 Replay 與至少一場完整真實對局無持續記憶體成長或可見卡頓。
- Client／2999 暫時失效會顯示白話狀態並自行恢復。
- Overlay 不搶 LoL 焦點，不阻擋正常遊戲輸入。

### 安全與隱私

- Overlay view model、朋友文件、fixture、log 和 package 不含原始 KDA、等級、CS、死亡時間、原始 item 陣列、LCU token 或真實測試 Riot ID。
- 裝備價值**總和**自 2026-08-16 起是允許的例外，理由與界線見 `AGENTS.md`：可顯示聚合總值，不可顯示原始 item 陣列，且必須標示為「裝備值」而非「經濟」。
- 網路目的地僅包含本機 loopback、文件允許的 Riot 網域與經政策審查後列入 allowlist 的歷史資料來源。
- 無注入、記憶體讀取、自動輸入、匿名身分還原或自製 MMR／ELO。
- Riot API key、OP.GG session、cookie 或其他秘密不進入 EXE、ZIP、log、fixture 或公開設定。
- Production 不使用 Synthetic adapter 冒充真人歷史資料；live provider 失效時明確顯示 unavailable／stale 並保持核心 Overlay 可用。
- EXE／ZIP 完成敏感字串、PDB、本機路徑與非預期檔案掃描。

### 打包與交付

- 乾淨 clone 後能以文件中的一個命令產生 package。
- CI 與本機使用相同的 package builder。
- ZIP 頂層只有 EXE 和 HTML，且 HTML 完全離線、自含素材。
- SHA-256 由打包流程自動產生並寫入 HTML／Release，不允許人工複製造成漂移。
- 發布版本、檔名、tag、Assembly 與文件一致。
- Release 不標記 prerelease，標題與檔名不再出現「測試版」。
- 若 EXE 未簽章，SmartScreen 風險有明確說明且通過非技術使用者驗收；若要擴大到朋友圈以外，優先完成可信任簽章。

## 11. 非目標與不可退讓界線

- 不要求安裝 OP.GG App；但可以在符合第一方規則、低頻、標註來源且可降級的前提下使用 OP.GG 公開資料。
- 可以讀取歷史戰績並推測近期狀態／風格，但不將本場表現分或自製歷史指標宣稱為官方牌位、隱藏 MMR、ELO、確定的玩家實力或勝率。
- 不顯示本機資料端點無法可靠取得的全員傷害、承傷、治療量。
- 不破解或還原 Riot 原本隱藏的選角玩家。
- 不為了更快取得資料而注入遊戲、讀記憶體或模擬操作。
- 不自動上傳玩家資料、診斷資料或使用行為。

## 12. 下一個 Codex 的建議第一個任務

2026-08-11 的原始期限已過，這份提示是歷史記錄，保留供比對；目前真正待辦以下方「2026-08-16 起建議任務」為準。

<details>
<summary>2026-08-10 原始提示（已過期，僅供參考）</summary>

> 完整閱讀 AGENTS.md、docs/PRODUCT_HANDOFF.md、docs/HISTORICAL_DATA_RESEARCH.md、README.md 與 SECURITY.md。目標是在 2026-08-11 交付可下載、可操作、可驗收的完整候選版本，不要停在分析。先用 Replay 量測並修正 P0：Dot click／drag、面板大範圍拖曳、UI thread、長生命週期 UI、圖片快取和 snapshot 去重。並行以 Synthetic history adapter 完成歷史近期狀態／風格的 UI、schema、樣本不足與失效測試。若已有 Riot Production API 或 OP.GG 書面允許，再接 live adapter；否則交付清楚的 unavailable／PolicyDisabled 狀態和可選的瀏覽器 OP.GG 連結，禁止為趕日期偷偷 scraping、把 API key 放進 EXE、分析匿名玩家或產生自製 MMR／ELO。最後把 repository 內的一鍵 test／publish／hash／scan／ZIP 流程做完，產生 candidate package，跑自動測試與必要 Replay／真實驗收，依正式發布門檻列出仍未通過的項目。不要僅靠改名宣稱正式版。

</details>

### 2026-08-16 起建議任務

P0 的架構性問題（長生命週期 UI、pointer state machine、snapshot 去重、打包流程）已經處理，Sandcastle 自動化工具已完整移除，Overlay 版面也依第二輪使用者實測回饋大幅精簡（見第 16 節）。下一個真正的瓶頸是**真機互動驗證**，不是再寫更多程式：

> 完整閱讀 AGENTS.md、docs/PRODUCT_HANDOFF.md（特別是第 16 節）與 SECURITY.md。這台機器上 .NET SDK 已可用（見第 3 節路徑備註），先確認能本機建置、跑測試、把 WPF shell 跑起來。用真的滑鼠對 Dot／Compact／Expanded 做拖曳、click-through、鎖定位置測試，不要只靠螢幕截圖判斷「看起來對」。若條件允許，跑一次真實 ChampSelect → Loading → InGame → EndOfGame 全流程，記錄真實 CPU／記憶體／UI 更新延遲，取代目前僅有的 Linux 邏輯層 proxy 數字。若要接牌位資料，Core model 已支援 rank-only profile；下一步是 `IHistoricalProfileTransport` 的 Riot Personal-key 實作與 Settings 裡的 key 輸入欄位（本機儲存，不進 git／不進打包）。每次 push 後務必確認 CI 實際結果，不要只憑本機建置成功就當作驗證完成——本機沒有跑 release scan 那道 gate。
>
> [狀態：這段建議任務寫於 2026-08-16。`14945ec`（2026-08-17）已完成上述「下一步」——`IHistoricalProfileTransport` 的 Riot Personal-key 實作、Settings key 輸入欄位與 region 對應都已存在；`96a3dc7`～`8884b60`（2026-08-20，見第 17 節）接著把牌位鋪到 Expanded 每一列並完成量測。真機拖曳、click-through、多螢幕／DPI 與真實對局的 CPU／記憶體／UI 更新延遲仍是未完成項目，見第 17 節「距離發布門檻仍未完成」。]

## 13. 重要檔案導覽

- `src/LolPerformanceOverlay/App.xaml.cs`：生命週期、phase 行為、session loop。
- `src/LolPerformanceOverlay/UI/OverlayWindow.cs`：目前渲染、拖曳、click-through 的主要問題區。
- `src/LolPerformanceOverlay.Core/`：可在 Linux 測試的互動、更新、歷史 profile、評分與安全 snapshot 邊界。
- `src/LolPerformanceOverlay/Infrastructure/LeagueSessionSource.cs`：LCU／2999 polling 與 reconnect。
- `src/LolPerformanceOverlay/Infrastructure/DataDragonProvider.cs`：靜態資料與圖片快取。
- `src/LolPerformanceOverlay/Infrastructure/ReplaySessionSource.cs`：離線 UX／效能測試基礎。
- `tests/LolPerformanceOverlay.Tests/`：跨平台核心、解析、互動、歷史資料、更新流程與效能／soak 測試。
- `eng/PackageBuilder/`：restore、test、cross-publish、HTML hash 注入、掃描、ZIP 與 manifest 的唯一實作。
- `scripts/package.sh`、`scripts/package.ps1`：Linux 與 Windows 的一鍵入口。
- `docs/先看這裡.html`：朋友實際看到的離線說明。
- `docs/HISTORICAL_DATA_RESEARCH.md`：Riot API、OP.GG 與歷史資料來源的第一方政策研究。
- `SECURITY.md`：公開安全邊界。

## 14. 交接時應持續更新的紀錄

下一個 Codex 每完成一個里程碑，應在本文件或獨立 changelog 補上：

- 修改前問題與可重現步驟。
- 採用方案及沒有採用的替代方案。
- 量測環境與修改前後數字。
- 新增／更新的自動測試。
- 真實對局與朋友測試結果。
- 距離正式發布門檻仍未完成的項目。

不要把「已修一個 bug」等同於「產品已可用」。每次里程碑都要重新從 AGENTS.md 的九個角度審視。

## 15. 2026-08-10 候選打包里程碑

### 已完成的 repository 內機制

- `Directory.Build.props` 是 Assembly、publish、package manifest 與朋友 HTML 的唯一產品版本來源；目前候選版本為 1.1.0。
- `global.json` 將建置 SDK 固定為 8.0.423 並停用 roll-forward；Windows workflow 使用同一版，manifest 記錄實際 SDK，避免 `8.0.x` 隨 runner 更新而讓成品漂移。
- `eng/package-config.json` 集中兩檔 ZIP 契約、允許網域、秘密／本機路徑／fixture 身分／PDB／HTML remote resource／Overlay raw-field 掃描規則。
- `scripts/package.sh` 與 `scripts/package.ps1` 都只啟動同一個跨平台 PackageBuilder，不各自維護一套容易漂移的 publish 或掃描流程。
- Linux 入口會 restore、執行所有 `net8.0` 測試、cross-build `net8.0-windows` tests，再以 `EnableWindowsTargeting=true` cross-publish WPF；Windows 入口才會實際執行兩類測試，之後以相同參數 publish。
- PackageBuilder 將實際 EXE SHA-256 注入完全離線的 `先看這裡.html`，ZIP 頂層只接受 `LoL即時表現Overlay.exe` 與 `先看這裡.html`，並輸出 `package-manifest.json` 和 `SHA256SUMS.txt`。
- PackageBuilder 另以 source gate 阻擋 process-memory read/write、remote-thread／hook／injection、driver I/O 與自動鍵鼠輸入能力進入 shipping source；這是 regression guard，不是 Vanguard 安全認證。
- `.github/workflows/windows-package.yml` 在 `windows-latest` 呼叫相同 `scripts/package.ps1`，只上傳候選 artifact，不自動合併、tag 或建立正式 Release。
- 朋友 HTML 按「安全與隱私 → 開始 → 拖曳／鎖定／重設 → 資料意義 → 疑難排解 → 移除」排列；歷史真人資料未啟用時明確顯示 unavailable，OP.GG 只是使用者主動開啟的普通瀏覽器連結。
- 位置保存以單一 reusable timer／最多一個 worker 合併高頻拖曳事件；歷史 cache 預設最多 256 筆，有 stale TTL、週期清理、LRU eviction、同 key inflight coalescing 與 dispose cancellation。
- 本機與 Data Dragon HTTP client 都禁止 redirect；回應 body 有 byte 上限，League Client 自簽 TLS bypass 只允許 request URI 仍是精確 HTTPS loopback host。

### 尚不能由 Linux 宣稱通過

- Windows 真實拖曳手感、WPF focus／click-through、系統匣、SmartScreen、DPI／多螢幕與 LoL 全螢幕無邊框。
- 兩台乾淨 Windows 10／11 使用者環境的下載、解壓、啟動、完整對局生命週期、結束與移除。
- 未參與開發的朋友只看 HTML 完成所有操作。

因此產物仍標為「候選成品」，不視為穩定 Release；真機門檻通過前不得只靠 1.1.0 名稱或成功 cross-publish 宣稱完成正式發布。

### UX 方案比較與 Linux pipeline 量測

量測環境：Ubuntu 22.04 x64、.NET SDK 8.0.423、Release build。這些數字只代表跨平台純邏輯與既有程式碼路徑的 proxy，不是 WPF frame time、Windows CPU、Dispatcher latency 或真實拖曳手感。

- 互動方案 A：Dot 全區、Compact／Expanded 的非控制背景都使用同一個 5 DIP click-vs-drag gesture；鎖定後整個 Overlay 以透明輸入 window style 停止接收滑鼠，透過快捷鍵／系統匣解除。
- 互動方案 B：只允許專用 grip 拖曳，其餘背景 click-through。
- 固定 100 個 headless hit targets（80 背景、10 grip、10 controls）的 Replay 比較：A 有 90 個可拖目標，B 有 10 個；兩者控制項攔截皆為 0。另以 pointer state-machine tests 驗證 5 DIP 內只 click、超過門檻只 drag、lost capture 可復原。因 A 的可發現拖曳範圍為 B 的 9 倍且沒有新增 control interception，候選實作採 A。

固定 1,800 frames（代表每秒一筆、30 分鐘）的更新 proxy。本輪 audit 前基準取自 `957b03c` 的相同 Release test corpus；先前已完成的 presentation 去重仍維持 30 次 update，沒有倒退：

| 指標 | 本輪 audit 前 | 本輪 audit 後 | 判讀 |
|---|---:|---:|---|
| presentation update policy | 30 | 30 | 相對 legacy 1,800 次仍減少 98.3%；本輪維持相同行為 |
| reducer 總耗時 | 5.555 ms | 4.280 ms | 降低約 23.0% |
| reducer 配置 | 333,752 bytes | 114,912 bytes | 降低約 65.6% |
| scorer + reducer 總耗時 | 211.646 ms | 53.261 ms | 降低約 74.8%；不把它冒充 WPF UI latency |
| scorer + reducer 配置 | 55,749,952 bytes | 9,525,128 bytes | 降低約 82.9%，約 5.17 KB/frame |
| forced-GC retained growth | 8,688 bytes | 7,504 bytes | 舊 snapshot weak references 均僅 1 個仍存活 |

另以真實 wall clock 每秒送入一筆 scorer + reducer frame，連續執行 30.0151 分鐘／1,800 frames；forced GC 後 retained growth 為 281,480 bytes（約 0.27 MB，低於 10 MB 門檻）。這項 soak 驗證純邏輯 pipeline 沒有持續線性成長，但仍不取代 Windows WPF visual、拖曳與真實對局的記憶體觀察。

目前 Linux Release 驗證為跨平台 Core 150／150 與 PackageBuilder policy 22／22 通過；`net8.0-windows` WPF shell 和 Windows integration tests 均以 `EnableWindowsTargeting=true`、單一 MSBuild node cross-build，0 warning／0 error。Windows-only tests 尚須由 Windows runner 實際執行。

修改前的 `OverlayWindow.ApplySnapshot()` 對每個 frame 呼叫 `Render()`，Expanded 每次又對最多十名玩家建立 `BitmapImage`；因此相同 1,800-frame corpus 會走 1,800 次 visual-tree rebuild 與最多 18,000 次 decode 路徑。修改後 mode 內 visual tree 長生命週期、同一路徑圖片只在背景 decode 一次；實際 Windows decode count、UI update P95、CPU、GC 與拖曳 frame pacing仍須由 Windows 真機量測，不能用上述 Linux proxy 取代。

## 16. 2026-08-16 里程碑：Sandcastle 移除、真機互動修正、牌位資料模型

這輪工作起點是審查另一位協作者導入的自動化，途中處理了使用者兩輪真機實測回饋，收尾於牌位功能的資料模型地基。完整 commit 序列見 `git log`；這裡只記錄決策、量測與仍未完成的部分。

### Sandcastle Issue worker：審查、修正、完整移除

`main` 上一度有一套自動化 GitHub Issue worker（PR #5，commit `c52fc15`），會讓一台受信任主機每兩分鐘 poll 一次 `agent/linux-usability-release` branch 並執行其上的程式碼。審查發現：

- 它宣稱的 ownership boundary（`assertOwnerControlledCheckout`）只驗證本機 HEAD 等於 GitHub base SHA，**不驗證那個 commit 的作者**；真正的邊界是 base branch 的 GitHub branch protection，而 worker 本身無法驗證 protection 是否存在。當時該 branch 沒有開保護。
- `trustedActor` 設定為 `weib10`，但 repository 上既有的 Issue 全是另一位協作者開的，代表 worker 實際上處於「作者不符、無法選中任何工作」的狀態，即使啟用也動不了。
- Sandcastle 自己的約 2,800 行 TypeScript 測試沒有接進任何 CI，implement/review/gate 這條鏈完全沒被驗證過。

決定完整移除而非只停用：`delivery.enabled`／`merge.enabled` 當時都是 `false`（fail-closed），但留著一套完整、任何 write collaborator 都能重新啟用的自動化，風險大於保留它的價值。移除範圍：`.sandcastle/` 全部、`package.json`／`package-lock.json`／`.nvmrc`（只為它存在的 Node toolchain）、`docs/SANDCASTLE_SETUP_RESEARCH.md`，並回收當初為容納它而放寬的 release scan 規則（`developerPathRegexes` 曾經對容器的 home 目錄開特例，已還原成嚴格版本，不再排除任何路徑）。`agent/linux-usability-release` 與 PR 來源 branch `sandcastle/issue-worker-deployment` 已確認所有 commit 都是 `main` 的祖先後刪除；`main` 現在是唯一分支。程式碼仍在 git 歷史的 `c52fc15`，需要時可 `git revert` 找回，但不建議重新啟用。

### 真機開發環境：SDK 其實已經在

這台開發機的 `dotnet.exe` 在 PATH 上，但那份只有 .NET 6 runtime、零個 SDK；一開始誤判為「沒裝 SDK」。實際上 SDK 8.0.423（`global.json` 釘的版本）已經裝在 `%LOCALAPPDATA%\Microsoft\dotnet`，只是沒在 PATH 最前面。用完整路徑呼叫後，本機建置／測試／執行 WPF shell 全部可行，161 項核心測試本機執行約 300–400 毫秒，相較每次繞 CI 的 2–3 分鐘差距很大。後續在本機修改、用 Replay/demo 模式（`--demo` 或 `--demo-expanded` 啟動參數）把 Overlay 實際跑起來，用 Win32 API 找視窗控制代碼、`System.Drawing.Bitmap.CopyFromScreen` 截取視窗矩形驗證版面——這是**畫面渲染與尺寸的驗證**，過程中沒有做實際滑鼠拖曳、click-through 或多螢幕/DPI 測試，不要混為一談。

### 第二輪使用者回饋逐項處理

對應第 4.1 節列出的七項回饋：

| 回饋 | 處理 |
|---|---|
| 選角紅藍分邊、記錄選角順序 | `LeagueSessionParser` 新增從 LCU `actions` 陣列解析 pick 順序（跳過 ban，只算真正的 pick），`ChampSelectMember`／`OverlayPlayer` 新增 `PickOrder`。Compact 選角原本是十人攤平一排、敵我不分，改成左右兩欄各五格、各自標籤與顏色。 |
| 設定視窗點不到 | 根因：`SettingsWindow` 從未設定 `Owner`，而 `OverlayWindow` 是 `Topmost = true`，對話框永遠開在下層碰不到。改為 `Owner = _overlay` 且自己也 `Topmost = true`。 |
| 顯示官方牌位 | 依使用者指示暫緩（見下方「牌位資料模型」），先只完成 Core model 地基。 |
| 字太小 | 玩家列／標題／隊伍標題字級全面調大；第二輪回饋後改為精簡版面（見下）。 |
| Expanded 面板空間浪費 | 迭代三次：720×610 → 648×552（初次縮小）→ 實測發現底部仍有約 54px 死空間，改用 `SizeToContent.Height` 讓高度跟內容走 → 第二輪回饋要求進一步精簡，玩家列從每人 5 段文字（56px 高）砍成 1 行（頭像＋英雄名＋徽章＋分數，34px 高），玩家名／評語／逐列信心移到 tooltip，標題兩行併一行，歷史區塊整塊隱藏。最終尺寸 520×286（對局結束時 520×60）。 |
| 顯示裝備加總價值 | 查證 Riot 政策：官方 Live Client Data API 本來就公開所有玩家的 `items`，單價是賽前既有的 Data Dragon 靜態資料，加總是對客戶端既有資訊做算術，屬於政策核可的 overlay 範圍。`AGENTS.md`／本文件第 10 節同步修訂為允許聚合總值（原始 item 陣列仍禁止），並標示為「裝備值」而非「經濟」——因為客戶端只回報本人的未花費金錢，稱作總經濟會高估。 |

`SizeToContent` 改動過程中，實測發現一個真實 regression：對局結束、名單清空時，隊伍卡片用 `Visibility.Hidden`（保留版面空間但不可見），在舊的固定尺寸視窗下無感，但改成內容自適應高度後，會讓視窗卡在最高狀態——正好是最該收起空間的時刻。已改成 `Visibility.Collapsed` 並用即時量測視窗高度的方式驗證（對局結束時窗高確實收到 60px，只剩標題列）。

### 牌位資料模型

使用者詢問「自己申請 Riot API key、手動填入」是否合規。查證 Riot Developer Portal 原文確認：**只存在本機設定、不進 git／不進打包的 Personal key，供申請者本人或小型私人社群使用是官方允許的**；這跟「把 key 塞進發給多個朋友的同一份 EXE」是完全不同的兩件事，後者才是規則禁止的「public consumption」。詳細落款見第 5.4 節補充。

要顯示牌位，Core 的 `HistoricalProfile` 型別原本強制要求 `PlayStyle`（五個對局風格維度）非空——即使資料來源只查了一次牌位、沒有比賽紀錄可以算，也被迫塞入編造的「Balanced」讀數，違反本文件與 `AGENTS.md` 的產品誠實性原則。已將 `PlayStyle` 改為可為 `null`，代表「沒有風格可讀」而非「風格中立」；`HistoricalProfileCoordinator` 的驗證邏輯本來就沒有依賴 `PlayStyle`，新增測試釘住這件事而非只是假設。UI 端（`OverlayWindow.UpdateHistoryControls`）已能正確處理 `PlayStyle` 為 null 的情況，但目前歷史面板整塊沒有掛進畫面（見上表），所以這條路徑還沒有實際畫面可驗證。

**沒有做的**：實際的 `IHistoricalProfileTransport` 實作（呼叫 Riot `ACCOUNT-V1`／`LEAGUE-V4`）、Settings 裡貼 key 的欄位、region 對應。這是下一個獨立的工作項目，本次只確認合規性與鋪好 model 地基。[狀態：`14945ec`（2026-08-17）已完成這三項；第 17 節在此基礎上把牌位鋪到 Expanded 每一列，並補上效能回歸量測。]

### 過程中的一次 CI 疏失

其中一次 push（隱藏歷史面板、標題併行的那次改動）讓 CI 的 release scan 失敗——註解裡寫的檔名 `App.xaml.cs` 被掃描規則當成 `Cs`（CreepScore 縮寫）獨立單字誤判（`.` 前、詞尾後，符合 `\b...\b` 邊界）。這個誤判本身合理（不該放寬 gate，改措辭即可），但過程有一個流程疏失：那次 push 之後我沒有回頭確認 CI 結果就繼續下一項工作，直到下一個 commit 才注意到、且一度誤記錯問題來源於哪個 commit。main 因此有約 70 分鐘處於 CI 紅燈但沒有主動回報。**根因是把「本機建置成功」誤當成「驗證完成」**——本機沒有跑 release scan 這道 gate，只有 build 和 test。已修正並確認後續每次 push 都有追蹤 CI 結果。

### 本輪測試與 CI

- 核心測試從 161 增至 163（新增選角順序解析、rank-only profile 驗證），加上 Windows 專屬 9 項、打包政策 29 項，共 201 項全數通過。
- CI（`.github/workflows/windows-package.yml`）目前只在 `push`／`pull_request` 到 `main` 時觸發（Sandcastle 移除後也同步移除了舊 branch 的觸發條件）。
- 最新驗證通過的 commit：`7942c14`。

### 距離第 10 節發布門檻仍未完成

- 三種模式的真實滑鼠拖曳、click-through、位置鎖定——本輪只驗證了畫面渲染與尺寸，沒有實際互動測試。
- 兩台乾淨 Windows 環境的下載／安裝／完整對局／移除驗收；未參與開發的朋友測試。
- 多螢幕、不同 DPI、解析度切換。
- 真實對局的 CPU／記憶體／UI 更新延遲量測（現有數字仍是 Linux 邏輯層 proxy）。
- `LICENSE` 檔仍未補上。[狀態：`0e59d17`（2026-08-19）已補上 MIT `LICENSE`。]
- `outputs/` 目錄若有舊 package，早於本輪所有變動，展示前需重新打包。

## 17. 2026-08-20 里程碑：逐列官方牌位與效能回歸驗證

這輪工作是 issue #6（Expanded 十人面板每一列顯示官方牌位）拆出的四張 ready-for-agent 票（#7–#10）加上收尾的量測與文件票（#11），起點是 `14945ec` 已經鋪好的 rank-only model 與 live transport 地基。四張實作票依序是 `96a3dc7`（每列短碼）、`977fa62`（白話失敗狀態）、`edcb59b`（tooltip）、`8884b60`（底部面板收斂），本節記錄決策、量測與仍未完成的部分；commit 內文本身已經很完整，這裡不逐行重述。

### Seam：牌位進 `OverlaySnapshot`，不開第二條 UI 路徑

牌位以 `OverlayPlayer.OfficialRank`（新的 `OfficialRankDisplay?` 欄位）進入既有的 `OverlaySnapshot`／`OverlayPlayer`，而不是另外新增一個投影 seam。這是寫 spec 前就定案的方案：`OfficialRankAttachment.Attach` 是一個無 IO 的同步純函式，輸入 `OverlaySnapshot` 加最近一次 `HistoricalProfilesResult`，輸出新的 `OverlaySnapshot`；沒有變動時回傳同一個 instance，避免製造假 diff。join key 是 `OverlayPlayer.StableKey` 對 `RevealedPlayerIdentity.StableKey`——`VisibleSnapshot` 本來就用同一把 key 比對玩家，不必新增比對邏輯，也不會因為改名或特殊字元錯位。`OverlayPlayerFields` 同步新增 `OfficialRank` flag 並加進 `DiffPlayerFields`／`All`，牌位變動因此自動走既有的 `VisibleSnapshot.Diff`／`Merge` 與 `OverlayUpdateReducer` 節流，`OverlayWindow.ApplySnapshot(snapshot, diff)` 仍然是唯一一條 UI 更新路徑。

歷史查詢是非同步的，結果幾乎一定晚於觸發它的那個 frame。`App.xaml.cs` 原本收到結果後直接呼叫 `_overlay.ApplyHistoricalProfiles`，繞過 reducer；現在改成 session loop 每個 frame 都先用 `AttachLatestHistoricalProfiles` 把「目前已知的最新牌位」貼到當前 snapshot 再送進 reducer，而晚到的查詢結果（`RefreshHistoricalProfilesAsync`）也改為貼到*最新*一次已評分的 snapshot（`_latestFrame`／`_latestEvaluatedSnapshot`，由 `_historyGate` 鎖保護），再呼叫 `OfferFrame` 走一次 reducer，而不是貼到觸發查詢當下的那個舊 snapshot。roster 換人或退出 InGame 時，`CancelHistoricalLookup(clearRoster: true)` 除了既有的 `_historyRosterGeneration` 遞增，也把 `_latestHistoricalProfiles` 清空，避免同一個 `StableKey` 被下一場比賽的新玩家撞上、繼承到舊牌位；晚到的查詢結果另外會先比對 `IsCurrentHistoryGeneration`，過期世代的結果直接丟棄，不會貼到任何 snapshot 上。

版面上，34px 列高與 520 寬完全沒變：新的牌位欄（25px）是用既有欄位讓出來的——大頭貼 34→28、meta 42→38、分數 46→34，各自仍能放得下最長的實際內容（三位數分數、`#10`、`99.9k`），英雄名欄寬不變。

### 決策與捨棄的方案

- **失敗狀態共用一個中性符號（`—`），不是一個原因一個符號。** 這些失敗幾乎都是全隊性的（沒設 key、離線、額度用完），十列一個生僻符號讀起來像程式壞了，不是資訊；真正的原因留在 tooltip 的完整句子裡（`OfficialRankDisplay.StatusText`／`TooltipText`）。
- **沒有排位天梯的 queue（ARAM）整格收起，不是打符號。** 這個概念在 ARAM 對任何人、任何一局都不可能有值，符號只是每一列都重複的雜訊，`ShortCode` 直接留空字串，`OverlayWindow.UpdatePlayerRank` 已有的「文字為空就收起 cell」邏輯自然適用，不需要新的顯示分支。[狀態：第 18 節加入 Solo／Flex 牌位 fallback 後，ARAM 現在會顯示玩家的 Solo 或 Flex 牌位（找不到時才是「未」），不再整格收起；這裡描述的是 fallback 之前、ARAM 完全查不到任何牌位時的舊行為。]
- **順手抓到一個真的 bug：`RiotHistoricalProfileTransport` 把「沒有排位天梯」回報成 `ProviderUnavailable`**——和「來源真的壞掉」共用同一個信號。每一場 ARAM 都會告訴玩家「資料來源故障」，但其實什麼都沒壞。新增 `HistoricalFailureReason.NoRankedLadder`，讓這個語意從 transport 一路帶到 coordinator 再到 presentation，`Describe()` 特別在通用 `Availability` switch 之前先攔截這個 reason，而且刻意讓它和「profile 存在但 `OfficialRank` 為 null 且 queue 沒有天梯」那條路徑輸出逐字相同（`NoRankedLadderFailureReasonMatchesTheProfilePresentNoLadderDisplayExactly` 測試釘住這件事，因為兩條路徑今後很容易在不知不覺中分岔）。[狀態：第 18 節的單／彈牌位 fallback 工作把 ARAM 短路徑本身拿掉了——沒有天梯的 queue 現在會實際查 Solo／Flex 牌位，不再短路——`NoRankedLadder` 因此不再有任何路徑能產生它，已經整個從 enum 移除；上面提到的兩條路徑與那個測試都不存在了，`Describe()` 的「沒有天梯」分支也併回「未定位」。]
- **牌位段位名稱改用台灣服用語（鐵／銅／銀／金），不是黑鐵／青銅／白銀／黃金。** 第一版把中國服的低段位詞和宗師／菁英（台灣專用詞）混在同一句 tooltip 裡，等於兩個伺服器的用語同時出現；`edcb59b` 修正為統一使用台灣服全套詞彙。
- **底部單人歷史面板保留，但職責縮小到牌位無法覆蓋的部分。** 每一列都有牌位之後，底部原本的牌位那半段變成純重複；移除後只留來源、queue、樣本數、取得時間、信心和近期風格（最常用英雄、五維風格），這些是逐列的 25px 欄位無論如何都放不下、只有整段落文字才能講清楚的資訊。曾經考慮整塊移除，但那會連帶砍掉風格呈現，屬於另一個工作項目，因此沒有採用。`HistoricalPanelPresenter.Describe` 在沒有 profile（未查到或查詢失敗）時回傳 `HistoricalPanelDisplay.Empty`，`OverlayWindow` 用 `Visibility.Collapsed`（不是 `Hidden`）整塊收起，理由和第 16 節 `SizeToContent.Height` 的既有規則一致：`Hidden` 仍占版面，會讓視窗卡在最高狀態，尤其在對局結束、名單清空、最該收起空間的時刻最明顯。

### 產品誠實性呈現

每一種狀態（包含全部失敗狀態）的 tooltip 都以同一句「牌位是 Riot 官方資料，分數是本場相對表現，兩者分開呈現，不會合併或換算成單一數值」收尾，一個缺失或過期的牌位不會被誤讀成「已經悄悄算進分數裡」。牌位與分數不只靠顏色區分：牌位欄位在獨立的欄（位置差異）、字級較小、斜體（一個不佔額外寬高、讀起來就是「引用自別處資料」的排版慣例），滿足 AGENTS.md 第 6 點色覺不能是唯一區分依據的要求。`NoTooltipOrStatusTextEverMentionsMmrEloOrWinRateWording` 等測試把「不出現 MMR／ELO／勝率」與「不洩漏 PUUID、LEAGUE-V4、rate limit 等開發者詞彙」都釘成自動測試，不是只在 code review 時人工確認一次。

### 效能回歸量測

量測環境：這台 Windows 開發機、Release build、.NET SDK 8.0.423、既有的 1,800-frame corpus（`PipelinePerformanceTests`，代表每秒一筆、30 分鐘）。改動前的基準取自 `0e59d17`（另開一個 git worktree 量測，避免切換分支影響工作目錄），改動後取自 `8884b60`。兩邊都各跑三次，因為單一次計時樣本不能算量測。

| 指標 | 改動前（3 次） | 改動後（3 次） | 判讀 |
|---|---|---|---|
| presentation update 次數 | 30 / 30 / 30 | 30 / 30 / 30 | 未變；相對 legacy 1,800 次仍維持減少 98.3% |
| reducer 配置 | 114,912 bytes（三次相同） | 114,912 bytes（三次相同） | 完全未變 |
| reducer 耗時 | 4.569 / 4.881 / 4.916 ms | 4.469 / 4.525 / 5.069 ms | 落在雜訊範圍，無法宣稱有差 |
| scorer + reducer 耗時 | 59.917 / 60.774 / 61.131 ms | 56.832 / 57.296 / 59.722 ms | 略低但區間重疊，不宣稱改善 |
| scorer + reducer 配置 | 10,389,664 bytes（三次相同） | 10,533,664 bytes（三次相同） | +144,000 bytes（+1.39%） |
| forced-GC retained growth | 6,896 bytes | 6,888–6,960 bytes | 未變 |

+144,000 bytes 不是雜訊，是可以精確解釋的數字：`1,800 frames × 10 players × 8 bytes`，也就是 `OverlayPlayer` 新增一個 nullable reference 欄位（`OfficialRankDisplay? OfficialRank`）在 64 位元執行環境下每個 player instance 多付的參考位元組。reducer 本身的耗時與配置完全沒有變化，因為 `OfficialRankAttachment.Attach` 發生在 reducer 之前，且沒有變動時直接回傳同一個 snapshot instance；presentation update 次數維持 30 次，代表新欄位沒有讓任何原本被節流掉的 frame 重新觸發更新。

必須誠實說明這些數字不是什麼：這是跨平台 Core 邏輯層的 proxy，不是 WPF frame time、不是 Windows 真實 CPU、不是 Dispatcher 排隊延遲、不是圖片解碼次數，也不是真實拖曳手感。新增的牌位欄（每列一個 `TextBlock`）與每列多一段 tooltip 文字組裝，在真實 WPF UI 執行緒上的繪製與 tooltip 顯示成本完全沒有被這組數字覆蓋到，這點第 16 節已經對舊有數字說過一次，這裡對新增的部分同樣成立。

### 本輪測試與 CI

新增測試：

- `OfficialRankAttachmentTests`（32 案例）：join by `StableKey`、無變動回傳同一 instance、重複貼合不產生 diff／reducer update、roster 換人後舊 entry 不貼到新人、牌位變動只標記牌位 flag（不連帶標記分數／英雄／圖示）、三種 cell 視覺狀態、每種失敗各自的白話句子、`NoRankedLadder` 與「profile 存在但無牌位」兩條路徑輸出逐字相同、stale 標記、`PlayStyle` 為 null 時不編造風格字樣、匿名玩家永遠沒有牌位、tooltip 內容（完整段位名、LP、queue、來源、取得時間、誠實聲明句）、不洩漏開發者詞彙與 MMR／ELO／勝率用語等。
- `HistoricalPanelPresenterTests`（6 案例）：底部面板文字不再提牌位、meta 文字涵蓋來源／queue／樣本數／取得時間／信心、`PlayStyle` 有無兩種情況、無 profile 時回傳可讓呼叫端收起面板的空結果。
- `HistoricalCoordinatorTests` 新增 `NoRankedLadderReasonSurvivesTheCoordinatorUnchanged`，`RiotHistoricalProfileTransportTests` 新增／修改對 ARAM 短路徑的斷言，釘住 `NoRankedLadder` 而不是 `Unavailable`／`ProviderUnavailable`。

現況（本次審視時獨立重跑一次確認，不是只看 commit 訊息裡的數字）：核心測試 254／254、Windows-adapter 測試 11／11、PackageBuilder 政策測試 29／29 全數通過，三個測試專案的 Release build 都是 0 warning／0 error。`Tier`／`Division`／`LeaguePoints`／`OfficialRank`／`ShortCode` 等新名稱都不在 `eng/package-config.json` 的 `rawOverlayFieldNames` 阻擋清單內，本輪不需要放寬 gate，release scan 也沒有因為新欄位命名誤判。

CI 狀態必須誠實記錄：這四個實作 commit（`96a3dc7`～`8884b60`）目前只存在於本機 `feature/per-player-rank` 分支，領先 `origin/main` 四個 commit、尚未 push，因此 CI 完全沒有在這幾個 commit 上跑過——本節之前的「本機建置與測試都過」只證明了 build 和 test，release scan 這道 gate 只有 CI 會跑。push 之後仍須依 AGENTS.md 的既有紀律回頭確認 CI 實際變綠，不能把這輪的本機驗證當成完成。

### 文件同步

`14945ec`（2026-08-17，早於這四張票）已經把 live provider、Settings key 欄位與 region 對應全部做完，但當時沒有人回頭把散落在其他文件裡「歷史資料 live provider 未啟用」一類的敘述改掉；這輪一併清掉：

- `README.md`：「歷史資料 live provider 目前未啟用」改為說明 live provider 已實作、opt-in、key 留白時仍是 unavailable／policy-disabled；資料流程圖裡「不含…物品價值」改為「不含…原始裝備陣列；裝備值聚合總和與官方牌位是允許的例外」；對外連線只提了 Data Dragon，加上「貼入 key 後才會連線 Riot 區域主機」；「操作」段落補上逐列牌位／tooltip 的說明。
- `SECURITY.md`：資料邊界那句和 README 犯了同一個錯（把「物品價值」列為排除項），一併修正；對外連線清單補上 19 個 Riot 區域主機（見九角度審視第 2 點，這是本輪查出最值得記的落差）；補上 Riot key 只存在本機 `settings.json`、不進 log 的說明。
- `docs/先看這裡.html`：這是**手動維護的原始檔**，不是由 PackageBuilder 產生——`eng/package-config.json` 的 `friendGuideTemplate` 直接指向這個檔案，PackageBuilder 只替換雜湊與版本號兩個 placeholder、注入離線 CSP，不改寫任何文字內容，因此直接編輯這份檔案本身，沒有另外的產生器原始碼要改。「完整面板」卡片補上逐列牌位的說明；「歷史近期狀態／風格」卡片與資料表格裡的「目前未啟用」改為「需要你自己貼入 API key 才會查詢」。
- `docs/PRODUCT_HANDOFF.md` 第 3、12、16 節：凡是被 `14945ec` 或 `0e59d17`（LICENSE）補上的「沒有做的」項目，都依本文件開頭的既有慣例加上「[狀態：...]」標記並指向對應章節，沒有直接刪改原文——第 3 節歷史資料現況、第 12 節建議任務、第 16 節「沒有做的」與發布門檻清單各一處。
- `AGENTS.md`：逐條核對後沒有發現需要修正的錯誤敘述；裡面關於 Personal key 的規則本來就是寫給「還沒做這個功能時」的允許性說明，功能做出來之後仍然成立，不需要改。

### 九個角度重新審視

依嚴重度排序；每個角度都有明確結論，不是找不到東西就跳過。

1. **【高】安全與隱私（角度 4）——工作目錄裡一份未追蹤的真人截圖。** repository 根目錄有一個未加入 git 的 `Screen15.png`（約 1.77 MB，最後修改時間 2026-08-17 21:13；`git log --all -- Screen15.png` 確認從未進過任何 commit，`git check-ignore` 確認沒有被 `.gitignore` 排除）。時間點與內容線索都對得上 issue #6「Further Notes」提到的「使用者提供的遊戲內十人計分板截圖，含真實 Riot ID」——正是 AGENTS.md 明講不得進 repository、fixture、log 或 Issue 的那類檔案。它目前只是未追蹤狀態、還沒造成外洩，但正好放在下一次誤觸 `git add` 會撿到的位置。這份審視沒有代為刪除（不在本票範圍，也不是我該單方面處理使用者檔案的決定），但必須點名：**建議儘快刪除或移出 repository 目錄**，並考慮在 `.gitignore` 加一條規則防止同類檔案再次被誤加。
2. **【中】安全與隱私／打包與維運（角度 4／7）——安全文件曾經漏列新的對外主機。** 在這輪之前，`SECURITY.md` 與 `README.md` 的對外連線清單只列了 loopback 與 `ddragon.leagueoflegends.com`，完全沒提到 `14945ec` 新增、`eng/package-config.json` 的 `runtimeHosts` 裡已經有的 19 個 Riot `ACCOUNT-V1`／`LEAGUE-V4` 區域主機，即使那個連線只在使用者自己貼入 key 之後才會發生。安全文件如果沒有窮舉所有可能的對外目的地，就稱不上是完整的邊界說明。已在本輪一併補上（見「文件同步」章節），但代表 `14945ec`、`44ea328`、`0e59d17` 三個 commit 之間，實際網路行為和文件默默漂移了一段時間都沒被抓到。建議往後每次新增 `runtimeHosts` 時，`SECURITY.md` 的連線清單一併檢查更新。
3. **【中】使用體驗／相容性／可靠性（角度 1／5／3）——真機互動完全未驗證。** 這台機器螢幕鎖定，無法用真的滑鼠對新的牌位欄做 hover／tooltip 觸發測試，無法確認 25px 欄寬、12.5pt 斜體字在不同 DPI 下是否清楚可讀或被截斷，也沒有跑一場真實對局讓「牌位晚到、re-attach、reducer 再次節流」這條路徑在真實網路延遲與真實 Client 生命週期下跑過。本輪新增的 39 項測試涵蓋了每一種 observable outcome（cell 內容、diff flag、reducer 是否觸發），但「畫面上實際長什麼樣子、滑鼠移過去有沒有反應、對局中會不會卡頓」本身仍然是**未驗證**，不是**已通過**——這點延續第 16 節就已經記錄的既有保留，本輪新增的牌位欄沒有讓它變得更好或更差。
4. **【中】效能（角度 2）——量測數字不含真實 WPF 繪製成本。** 上方表格的數字是 Linux／Windows 邏輯層 proxy（reducer／scorer 耗時與配置），不包含新增的每列一個 `TextBlock`（牌位欄）與每列多組裝一段 tooltip 文字在真實 WPF UI 執行緒上的繪製與 tooltip 顯示成本。配置成長的 144,000 bytes 已經精確解釋為欄位本身的參考位元組成本，判讀清楚；但真機 UI thread 的實際負擔仍待 Windows 真機量測，屬於第 7 節既有門檻裡尚未完成的部分。
5. **【低】可理解性與無障礙（角度 6）——檢查後沒有發現問題。** 牌位與分數用欄位位置＋斜體字重雙重區分，不只靠顏色；失敗狀態共用一個中性符號但各自有完整白話 tooltip 句子；`StatusTextNeverLeaksDeveloperJargon`、`NoTooltipOrStatusTextEverMentionsMmrEloOrWinRateWording` 等測試把「不出現 PUUID／LEAGUE-V4／rate limit」與「不出現 MMR／ELO／勝率」都釘成自動測試。
6. **【低】可維護性與測試性（角度 8）——檢查後沒有發現問題。** `OfficialRankAttachment`／`HistoricalPanelPresenter` 都是無 IO 的靜態純函式，格式化只在 Core 做一次；`OverlayWindow` 只負責顯示既有字串，不做第二次格式化；`App.xaml.cs` 的 re-attach 邏輯統一鎖在既有的 `_historyGate`，沒有新鎖也沒有新的競爭窗口；39 項新測試全部斷言 observable outcome，沒有測 private method 或 WPF visual tree。
7. **【低】產品誠實性（角度 9）——檢查後沒有發現問題。** 每個狀態（含全部失敗狀態）都附上「牌位是 Riot 官方資料，分數是本場相對表現」句子；`NoRankedLadderFailureReasonMatchesTheProfilePresentNoLadderDisplayExactly` 釘住兩條不同程式路徑的輸出必須逐字相同；牌位與分數永不合併運算或換算成單一數值；`PlayStyle` 缺樣本時維持 `null`，不編造「平衡」讀數。
8. **【無新增發現】打包與維運（角度 7，功能本身）——檢查後沒有發現問題。** 新欄位名稱不在 `rawOverlayFieldNames` 阻擋清單內，不需要放寬 gate；`PackageBuilder.Tests` 本機重跑 29／29 通過；release scan 未因新欄位命名誤判。（上方第 2 點記錄的是文件漂移問題，不是打包流程本身的問題。）

### 距離發布門檻仍未完成

延續第 16 節既有清單，本輪沒有讓任何一項變得更好，也新增了以下項目：

- 三種模式的真實滑鼠拖曳、click-through、位置鎖定，以及新牌位欄本身的 hover／tooltip 真機互動——都還沒做。
- 多螢幕、不同 DPI、解析度切換下牌位欄與 tooltip 的實際呈現——都還沒驗證。
- 真實對局的 CPU／記憶體／UI 更新延遲量測（現有數字仍是邏輯層 proxy，本輪新增的牌位欄與 tooltip 繪製成本同樣不在其中）。
- 這四個實作 commit 尚未 push，CI 完全沒有在它們身上跑過；push 後必須實際確認 CI 綠燈並記錄通過的 commit，不能只憑本機 build／test 通過。
- 工作目錄裡未追蹤的 `Screen15.png`（見九角度審視第 1 點）建議在 push 前處理掉，避免被任何自動化或手動操作誤加進版本控制。
- 兩台乾淨 Windows 環境的下載／安裝／完整對局／移除驗收、未參與開發的朋友測試——仍未開始。
- `outputs/` 目錄若有舊 package，早於本輪所有變動，展示前需重新打包。

## 18. 2026-08-20 後續：沒有天梯的 queue 改用 Solo／Flex 牌位 fallback

第 17 節做完後，使用者指出一個第 17 節本身沒解決的落差：ARAM 這類沒有排位天梯的 queue，牌位欄整格是空的——transport 在送出任何 HTTP 請求前就直接回報 `NoRankedLadder` 短路徑（見第 17 節「決策與捨棄的方案」）。玩家在 ARAM 時通常還是想知道隊友的 Solo／Flex 牌位，而 LEAGUE-V4 的 `entries/by-puuid` 本來就會回傳這位玩家「每一個」queue 的 entry，不是只有查詢的那一個——單一回應裡已經有 fallback 需要的所有資料，缺的只是「挑哪一個 entry」的邏輯。這節記錄這次補做的決策；本節之前的內容維持不動，落差處已用「[狀態：...]」標記回指到這裡。

### 改動範圍

- `src/LolPerformanceOverlay.Core/Historical/RiotHistoricalProfileTransport.cs`：移除 ARAM 短路徑，改成 `FindPreferredEntry`／`PreferenceOrder`——同一次 `entries/by-puuid` 回應裡，依序找「目前 queue 自己的天梯（如果有）→ Solo → Flex」的第一筆 entry，找到誰就把 `OfficialRank` 標成誰的 queue，不是查詢時的 queue。仍然只有兩次 HTTP 呼叫（account-v1 + league-v4），fallback 純粹是同一份回應裡挑資料的邏輯，不會多打第三次請求。
- `src/LolPerformanceOverlay.Core/Historical/HistoricalProfileCoordinator.cs`：`IsValid` 原本要求 `OfficialRank.Queue` 必須和 `profile.Queue`（查詢的 queue）完全一致，這條規則在 fallback 之後會誤殺所有合法的跨 queue 牌位；改成只要求 `OfficialRank.Queue` 必須是一個真的排位天梯（`HistoricalQueue.IsRankedLadder`，即 Solo 或 Flex），不再要求和 `profile.Queue` 相同。
- `src/LolPerformanceOverlay.Core/Historical/HistoricalModels.cs`：新增 `HistoricalQueue.IsRankedLadder`（`QueueId` 是 420 或 440）給 coordinator 和 presentation 共用；移除 `HistoricalFailureReason.NoRankedLadder`（理由見下）。
- `src/LolPerformanceOverlay.Core/Presentation/OfficialRankAttachment.cs`：`Describe()` 的「沒有天梯」分支整個併回「未定位」；`FormatRank` 新增 `IsFromDifferentQueue` 判斷與 `CrossQueueNote` 措辭；`Unranked()` 的句子改成不提「這個模式」，因為現在「未定位」對任何 queue 都是同一件事實。
- `src/LolPerformanceOverlay.Core/Models.cs`：`OfficialRankDisplay` 新增 `IsFromDifferentQueue` 欄位（trailing optional，維持既有的可成長 positional record 慣例）。
- `src/LolPerformanceOverlay/UI/OverlayWindow.cs`：`UpdatePlayerRank` 依 `IsFromDifferentQueue` 加上點狀底線（`CrossQueueRankDecorations`），欄寬與 padding 完全不變。
- 測試：`RiotHistoricalProfileTransportTests.cs`、`HistoricalCoordinatorTests.cs`、`OfficialRankAttachmentTests.cs` 三個檔案都有大幅調整，明細見下方「測試」小節。

### 選擇的優先順序

`目前 queue 自己的天梯（如果有）→ Solo(420) → Flex(440)`，去重後實作。Solo 場找不到 Solo entry 就退到 Flex；Flex 場找不到 Flex entry 就退到 Solo；ARAM（或任何沒有天梯的 queue）沒有「自己的」entry 可以優先，順序直接從 Solo 開始。三者都找不到才是「未定位」。這個順序完全在同一次 LEAGUE-V4 回應裡挑選，不多打一次請求——`RiotHistoricalProfileTransportTests` 裡好幾個案例都直接斷言 `handler.CallCount == 2`，包含 ARAM 最壞情況（自己的天梯不存在、Solo 也沒有，第三順位 Flex 才命中）。

### `HistoricalFailureReason.NoRankedLadder` 的決定：整個移除

第 17 節新增這個 reason，是為了把「這個 queue 沒有天梯」和「來源真的壞掉」（`ProviderUnavailable`）分開講清楚——這在當時是對的，因為 transport 那時候看到沒有天梯的 queue 就直接放棄，回報一個失敗原因。

fallback 做完之後，這整條路徑消失了：transport 不再因為「queue 沒有天梯」而放棄查詢，它一律嘗試 Solo／Flex fallback，三者都查不到時是 `Available` 且 `OfficialRank` 為 `null` 的「未定位」（不是 `NoRankedLadder`，也不是 `RecordNotFound`——後者的修正見下方「account 解析成功但沒有任何排位」小節）。檢查過整個 repository 後，沒有任何地方——transport、coordinator、synthetic provider——還會產生 `NoRankedLadder`。與其把一個沒有人能再產生的 reason 留在 enum 裡（下一個維護者看到它會合理地以為某條路徑還在用它），這次直接把它從 `HistoricalFailureReason` 移除，`OfficialRankAttachment.Describe()` 對應的「沒有天梯」分支也整個併回 `Unranked`——現在「這個 queue 沒有天梯」和「這個 queue 有天梯但這位玩家還沒爬」在查完 Solo／Flex 之後是同一個事實（玩家沒有任何一邊的牌位），沒有理由再分成兩種呈現。`Unranked()` 的句子因此也從「這個模式目前還沒有牌位」改成「這位玩家目前沒有單雙排或彈性積分的官方牌位」，不再暗示「這個 queue」本身有天梯概念。

### Cell 標記：點狀底線，寬度不變

`OfficialRankDisplay` 新增 `IsFromDifferentQueue`：只有在「目前 queue 自己就是一個排位天梯（Solo 或 Flex）」且「顯示的牌位來自另一個天梯」時才是 `true`——例如 Solo 場沒有 Solo 牌位、改顯示 Flex 牌位。ARAM 這類沒有天梯的 queue 永遠是 `false`：那裡每一列本來就是 fallback，標記等於每局都出現在十列上，是第 8 節已經處理掉的「一種失敗一個符號」同一種雜訊問題；tooltip 仍然照實講出牌位真正屬於哪個 queue，只是 cell 本身不加標記。

選的標記是「點狀底線」（`OverlayWindow.CrossQueueRankDecorations`），不是新字元、不是新顏色：

- **不佔寬度**：底線是 `TextBlock.TextDecorations`，畫在既有文字下方，不是額外字元，rank 欄原本的 25px（已經放得下最長的真實內容 `"GM*"`）完全不用變，champion 欄的算法也不用重算。往回推算給後人核對：Expanded 視窗 520px，`teamsGrid` 的 `Margin(10,0,10,8)` 留下 500px 給兩個隊伍欄加一個 12px 間距，每個隊伍欄 `(500-12)/2 = 244px`；隊伍卡片 `Padding(7)` 留下 `244-14 = 230px`；單行 row 自己的 `Padding(4,0,5,0)` 留下 `230-9 = 221px`；扣掉其餘四個固定欄（`28+38+25+34 = 125px`），champion 欄剩下 `221-125 = 96px`，和加牌位欄那次的算法完全一樣，因為 rank 欄本身沒有變寬。
- **不靠顏色**：底線用和文字相同的金色（`#D9B36C`），不是另一種顏色；區分靠的是「有沒有底線、底線是點狀還是實線」這種形狀差異，符合 AGENTS.md 第 6 點色覺不能是唯一依據的要求。
- **不是抽象符號**：點狀底線是瀏覽器、Wiki 常見的「這裡還有更多資訊，滑鼠移過去看」慣例（`<abbr title="">` 用的就是這個），不需要另外的圖例——滑鼠移過去，tooltip 就會照實講出牌位真正的來源 queue。

### 誠實性：tooltip 一律照實講

不管 cell 有沒有標記，`FormatRank` 只要偵測到 `rank.Queue` 和 `profile.Queue`（目前這場的 queue）不同，就會在 tooltip 加一句「這是＿的牌位，不是＿的牌位」（`CrossQueueNote`），點名牌位真正屬於哪個 queue、以及它不是目前這場的牌位。這句話不看 cell 有沒有標記就一定會加——ARAM 場的牌位一定會觸發它，因為 ARAM 永遠不是 Solo 或 Flex；只有 `StatusText`（cell 旁的白話句子）和 cell 的點狀底線才依「目前 queue 是不是排位天梯」決定要不要顯示，理由和上面 cell 標記的理由相同：ARAM 每列都是 fallback，重複標記是雜訊，但 tooltip 仍然是一對一、每次都誠實的。

### API 用量：驗證而非假設

ARAM 以前完全不打 Riot API；現在每一場 ARAM 都會對每位有 Riot ID 的玩家跑一次和排位場完全一樣的兩次呼叫（account-v1 + league-v4）。實際讀過 `HistoricalProfileCoordinator` 的程式碼確認：

- **並發上限**：`HistoricalProfileCoordinatorOptions.Default.MaximumConcurrency = 3`——`_concurrency`（`SemaphoreSlim`）限制同時間最多 3 位玩家的查詢在跑，不管 queue 是不是 ARAM，這條限制對 fallback 之前之後完全一樣。
- **單次請求人數上限**：`MaximumPlayersPerRequest = 10`——`GetProfilesAsync` 一開始就對超過 10 人的請求丟例外，剛好等於一場 10 人對局的人數上限，不會無限增長。
- **每位玩家的呼叫數不變**：fallback 完全發生在「挑同一份 LEAGUE-V4 回應裡的哪個 entry」，不是多打一次請求——`RiotHistoricalProfileTransportTests` 的多個新案例（包含 ARAM 最壞情況：自己的天梯沒有、Solo 也沒有，第三順位 Flex 才命中）都直接斷言 `handler.CallCount == 2`，證明 fallback 不會把 2 次呼叫變成 3 次或更多。
- **快取邊界呼叫觸發時機**：`App.xaml.cs` 的 `BeginHistoricalLookup` 只在名單（`HistoryRosterMatches`）真正變動時才觸發一次查詢，不是每個 frame 都查；`HistoricalProfileCoordinatorOptions.Default` 的 `FreshLifetime = 15 分鐘`、`StaleLifetime = 2 小時`，同一位玩家在 15 分鐘內出現在下一場（不論排位或 ARAM）會直接吃快取，不會重新打。

結論：fallback 沒有讓「每次查詢」變貴，變的是「以前完全不查的 ARAM，現在和排位場一樣查」——對一個 Personal key（20 requests/1s、100 requests/2min）而言，等於把原本只有排位場才有的用量，擴大到玩家花在 ARAM 上的時間比例。上面列的三層邊界（並發、單次人數、快取）在 fallback 之前就已經存在，fallback 沒有新增或移除任何一層，只是讓 ARAM 開始受它們保護，而不是完全繞過查詢。

### Review 補漏：account 解析成功但沒有任何排位，不該回報「查無資料」

上面的 fallback 做完後，coordinator 端 review 時抓到一個 pre-existing、不是這次改動造成、但正好在同一個地方的缺陷：`FindPreferredEntry` 找不到任何 entry 時，原本的程式碼直接回報 `HistoricalProfileAvailability.NotFound` / `HistoricalFailureReason.RecordNotFound`——但走到這一步，ACCOUNT-V1 早就已經成功解析出這個玩家的帳號，LEAGUE-V4 也確實回應了（只是回應裡沒有 Solo 或 Flex 的 entry）。「這位玩家存在，但沒有排位牌位」和「查無這個帳號」是兩件不同的事實，前者用「查無資料」的措辭形容，等於暗示查詢本身失敗，實際上什麼都沒壞——這和第 8 節「沒有排位天梯」誤用 `ProviderUnavailable` 是同一種缺陷形狀：presentation 層本來就有誠實的「未定位」狀態可以呈現，只是 shipping 的 live transport 走不到那條路徑。fallback 讓「沒有 Solo 也沒有 Flex 排位」變成一個常見結果（尤其是還沒打過幾場排位的新帳號），這個缺陷因此變得更容易被玩家看到。

修法：`RiotHistoricalProfileTransport.FetchAsync` 在 `FindPreferredEntry` 回傳 `null` 時，現在建立一個 `Availability = Available`、`OfficialRank = null` 的 `HistoricalProfile`（`SampleCount = 0`、`Confidence = InsufficientSample`、`CommonChampions`／`CommonRoles` 空陣列、`PlayStyle = null`——誠實地表示「這是一次只查得到帳號、查不到排位樣本的查詢」，不是編造數字）。`HistoricalProfileAvailability.NotFound` / `HistoricalFailureReason.RecordNotFound` 現在只保留給 ACCOUNT-V1 本身解析失敗（404）那一條路徑——`RiotIdNotFoundStopsBeforeAnyLeagueLookup` 測試釘住這是唯一還會製造 `RecordNotFound` 的地方。`HistoricalProfileCoordinator.IsValid` 不用改：這次 fallback 修正的 `profile.OfficialRank is not null && !profile.OfficialRank.Queue.IsRankedLadder` 檢查本來就會在 `OfficialRank` 為 `null` 時直接短路過去；另外重新逐條核對了 `SampleCount < 5` 必須搭配 `InsufficientSample` 那條規則，也確認新的零樣本 profile 符合，沒有假設——新增 `AnAvailableProfileWithNoOfficialRankPassesValidationAndCaches` 測試直接用這個確切形狀跑過完整的 coordinator 驗證與快取路徑。`Unranked()` 的措辭（「這位玩家目前沒有單雙排或彈性積分的官方牌位，尚未定位」）在上面加入跨 queue 措辭時已經改成不提「這個模式」，重新核對後確認同一句話原封不動適用於這個新路徑，不需要再改。

### 測試

- `RiotHistoricalProfileTransportTests.cs`：新增／改寫涵蓋——目前 queue 優先於 Solo／Flex（含「目前 queue 是 Flex 時不會被 Solo-first 的通用順序騙走」這個反例）、Solo 在目前 queue 沒有 entry 時當 fallback、Flex 在目前與 Solo 都沒有 entry 時當 fallback、沒有天梯的 queue（ARAM）分別 fallback 到 Solo／Flex、account 解析成功但三個 queue 都沒有 entry（含全空陣列與「只有不相干 queueType」兩種情境）時回傳 `Available`＋`OfficialRank` 為 `null` 的未定位 profile 而不是 `RecordNotFound`、`RecordNotFound` 現在只保留給 ACCOUNT-V1 本身解析失敗、牌位標的是真正來源的 queue 而不是查詢的 queue；每個案例都斷言 `handler.CallCount == 2`，釘住「fallback 不多打請求」。
- `HistoricalCoordinatorTests.cs`：`IsValid` 現在接受「跨天梯」的合法 fallback（Solo 查詢、Flex 牌位）、仍然拒絕「牌位指向根本不是天梯的 queue」（例如 ARAM）、也接受 `OfficialRank` 為 `null` 的零樣本未定位 profile；新增 ARAM 查詢走完整快取／去重路徑的案例，取代原本釘住 `NoRankedLadder` 的測試。
- `OfficialRankAttachmentTests.cs`：新增涵蓋——目前 queue 自己的牌位沒有跨 queue 標記、跨 queue fallback 牌位在排位 queue 裡有標記與 `StatusText`、同一份 fallback 在沒有天梯的 queue 裡沒有 cell 標記但 tooltip 仍照實講、「未定位」在有天梯與沒有天梯的 queue 下輸出完全相同（`ShortCode`／`StatusText`／`IsStale` 一致，只有 tooltip 裡的 queue 名稱不同）；移除／改寫所有原本釘住 `NoRankedLadder` 的測試；原本「同一組輸入貼兩次不產生 diff／不觸發 reducer」的回歸測試（`AttachingTheSameMixOfRankedUnrankedCrossQueueAndFailedProfilesTwiceProducesNoDiffOrReducerUpdate`）保留並加入跨 queue 案例，確認新欄位一樣遵守既有的無變化不重繪規則。這個 review 補漏沒有改動 `OfficialRankAttachment` 本身（`Describe()` 原本就會把「Profile 存在、`OfficialRank` 為 `null`」正確畫成「未定位」），缺陷完全在 transport 端，所以這個檔案不需要新增案例。

現況（獨立重跑確認）：核心測試 263／263（第 17 節基準 254，第 18 節 fallback 本身淨增 7，這次 review 補漏再淨增 2）、Windows-adapter 測試 11／11、PackageBuilder 政策測試 29／29 全數通過；`LolPerformanceOverlay.Core`、`LolPerformanceOverlay`（WPF）兩個專案 `--no-incremental` 全新建置都是 0 警告／0 錯誤。`IsFromDifferentQueue`、`IsRankedLadder` 等新名稱同樣不在 `eng/package-config.json` 的 `rawOverlayFieldNames` 阻擋清單內，本輪不需要放寬 gate。`OfficialRankAttachment.Attach` 無變化回傳同一個 snapshot instance 的規則沒有被這次任何一項修改觸碰。

尚未做、延續第 17 節既有清單的部分：這幾個 commit 尚未建立、尚未 push，CI 完全沒有在它們身上跑過；真機滑鼠 hover 是否能清楚看到點狀底線、tooltip 觸發是否順手——screen lock 環境下同樣未驗證，仍然是「未驗證」而不是「已通過」。
