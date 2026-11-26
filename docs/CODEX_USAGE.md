# Dùng Codex và Antigravity trong Trà Hiên V2

Tên file giữ nguyên để không gãy các liên kết cũ. Tài liệu này áp dụng cho cả Codex và Antigravity. Luật bắt buộc ở [AGENTS.md](../AGENTS.md), trình tự cổng ở [WORKFLOW.md](WORKFLOW.md), chữ ký lệnh và mã lỗi chi tiết ở [TOOLING.md](TOOLING.md).

## 1. Mỗi công cụ đọc gì

| Thành phần | Codex | Antigravity |
|---|---|---|
| Luật chung `AGENTS.md` | Có | Có (nạp như rule ở mọi lượt) |
| Skills `.agents/skills/*/SKILL.md` | Có, gọi bằng `$tên-skill` | Có, gọi bằng tên skill hoặc đường dẫn file |
| Subagent `.codex/agents/*.toml`, `.codex/config.toml` | Có (tùy chọn; trần kỹ thuật trong `.codex/config.toml` là `3` thread/session, không phải hạn mức cố định toàn quy trình) | **Không đọc**. Mở hội thoại mới cho G4 độc lập |
| Sandbox | Theo cấu hình Codex | Mặc định chặn mạng/Docker/ghi `.git`: các lệnh git fetch/worktree/commit/push và `verify-linux.ps1` cần bypass hoặc approval |

Sáu skill dùng chung: `spec-audit` (G1), `architecture-plan` (G2 & Design Spike), `implementation-slice` (G3 & remediation), `review-gate` (G4 & re-review), `base-integration`, `publish-pr`. Tất cả dùng chung **Unified Handoff Contract** ([WORKFLOW.md](WORKFLOW.md) §3).

## 2. Đồng bộ và tạo worktree

Chỉ tạo worktree khi task/chore đã sẵn sàng vào G3 (không tạo worktree sớm chỉ để lưu file plan chưa sẵn sàng; với Design Spike hoặc Planning riêng chỉ đọc thì làm trực tiếp trên repo đọc-chỉ):

```powershell
git status --short
git fetch origin --prune
./scripts/new-task-worktree.ps1 -Task <slug> -Branch <feature|fix|chore|docs>/<short-name> -Base origin/main
```

Lệnh tạo `D:\tra-hien-<slug>` với nhánh mới không track `main`. Mở công cụ viết mã tại đúng thư mục worktree đó. **Một writer duy nhất trên mỗi worktree**; không dùng `reset --hard` hay `clean -fd`; không thao tác lên local `main` khi đang có thay đổi dở.

## 3. Chính sách độc lập mô hình và vệ sinh hội thoại

- **Model-independent:** Owner tự chọn mô hình và mức suy luận trên giao diện hoặc qua cấu hình do Owner kiểm soát. Quy trình không yêu cầu tên mô hình, nhà cung cấp hay review chéo hãng; agent không tự đổi mô hình.
- **Tính độc lập của G4:** đảm bảo bằng hội thoại mới/độc lập với Implementer, quyền chỉ đọc, quy trình Blind-First và kiểm chứng gắn đúng `candidate.manifest`.
- **Quản lý ngữ cảnh:**
  - *Technical Lead:* khi ngữ cảnh bắt đầu suy giảm, lưu quyết định/trạng thái vào bằng chứng bền vững (checkpoint/ADR/snapshot có mốc thời gian khi được phép) rồi compact hoặc mở phiên Lead mới tiếp quản từ Git.
  - *Remediation (G3):* tái sử dụng phiên G3 nếu nguyên nhân gốc rõ, ngữ cảnh còn sạch và sửa gọn; mở phiên G3 mới nếu lỗi nghiêm trọng lặp lại, ngữ cảnh suy giảm hoặc cần đánh giá lại thiết kế/bất biến chung.
  - *Re-review (G4):* có thể tái sử dụng phiên G4 độc lập nếu ngữ cảnh và tính độc lập còn nguyên; mở phiên G4 mới nếu ngữ cảnh suy giảm hoặc cần đánh giá độc lập toàn diện.

## 4. Prompt mẫu ngắn (thay `<...>`)

Chỉ ghi quyết định/ràng buộc cụ thể vào dòng `Owner notes`. Không chép lại toàn bộ `docs/spec/` vào prompt.

**Technical Lead — tiếp quản / kiểm readiness:**

```text
Act as Technical Lead (read-only) at <D:\tra-hien>.
Run git fetch origin --prune, inspect live Git state, worktrees, docs/decisions/ADR-P2-01-phase2-contracts.md, and docs/plans/task-readiness.md.
Assess readiness, dependencies, hot files, shared contracts, and Owner blockers for <P*-* or scope>.
Do not write production code or edit files. Return one Vietnamese summary with VERIFIED/REPORTED/UNKNOWN/NOT_RUN labels.
```

**G1/G2 riêng hoặc Read-only Design Spike (cho task phức tạp / chưa sẵn sàng G3):**

```text
Use $architecture-plan (.agents/skills/architecture-plan/SKILL.md) in <COMPLEX_PLAN | DESIGN_SPIKE> mode (read-only) for <P*-*>.
Base: latest origin/main. Do not create a worktree or edit application code/docs/spec/.
Check dependencies, shared contracts, risks, and Owner decision blockers. Return the Unified Handoff Contract in Vietnamese.
```

**G3 — triển khai một task/chore đã được Owner duyệt:**

```text
Use $implementation-slice (.agents/skills/implementation-slice/SKILL.md) for <P*-* or approved chore>.
Worktree: <D:\tra-hien-slug> (create with scripts/new-task-worktree.ps1 at approved BASE <sha> if missing), branch <branch>.
Owner notes: <decisions/allowlist/constraints or "none">.
Follow AGENTS.md and docs/WORKFLOW.md. Freeze manifest, run required verification, return one Unified Handoff Contract in Vietnamese, then STOP (no commit/push/PR).
```

**G4 — Independent Blind-First review (hội thoại độc lập, chỉ đọc):**

```text
Use $review-gate (.agents/skills/review-gate/SKILL.md) for <P*-* or approved chore>, Tier <A | B>.
Worktree: <D:\tra-hien-slug>, branch <branch>, base <approved BASE sha>.
Manifest: artifacts/<slug>/candidate.manifest sha256 <digest>.
Fresh independent session, read-only, Blind-First (verify candidate, inspect full diff + untracked files, run required verification before comparing G3 summary). Return one Unified Handoff Contract in Vietnamese.
```

**Remediation (tái sử dụng phiên G3 hoặc mở phiên G3 mới trên cùng worktree):**

```text
Use $implementation-slice in remediation mode for <P*-* or chore> at <D:\tra-hien-slug>.
Fix only these G4 findings: <paste finding IDs and path:line>.
Rewrite candidate manifest after edits, run required verification (do not substitute filtered tests for full candidate verification), return one Unified Handoff Contract with FINDINGS closure, then STOP.
```

**Focused re-review (tái sử dụng phiên G4 độc lập hoặc mở phiên G4 mới):**

```text
Use $review-gate as focused re-review for <P*-* or chore> at <D:\tra-hien-slug>.
New manifest: artifacts/<slug>/candidate.manifest sha256 <new-digest>.
Prior findings: <IDs>. Assess actual delta, root cause, blast radius, and shared invariants; verify each prior finding CLOSED/OPEN with evidence; return one Unified Handoff Contract in Vietnamese.
```

**Base integration (khi `origin/main` dịch chuyển):**

```text
Use $base-integration (.agents/skills/base-integration/SKILL.md) for <P*-* or chore> at <D:\tra-hien-slug>: origin/main moved (<details>).
Follow scripts/task-integrate.ps1 (unpublished) or git merge --no-ff --no-commit (published). Rewrite manifest, run required verification, return one Unified Handoff Contract; no commit/push.
```

**Publish (chỉ dùng sau G4 PASS và có lệnh cho phép của Owner):**

```text
Use $publish-pr (.agents/skills/publish-pr/SKILL.md) for <P*-* or chore> at <D:\tra-hien-slug>.
Mode: <initial | republish>. G4 PASS for manifest artifacts/<slug>/candidate.manifest sha256 <digest>.
Owner explicitly authorizes publishing this task in this session. Pass all 4 publish checkpoints (A–D), verify all 4 GitHub Actions jobs at published HEAD, return one Unified Handoff Contract. Do not merge.
```

## 5. Kiểm chứng trên Windows

Windows Application Control chặn công cụ sinh OpenAPI (`0x800711C7`), nên kiểm chứng tương đương CI chạy qua Docker bằng `scripts/verify-linux.ps1` (chi tiết đầy đủ cờ, `coverage`, mã lỗi `0–7` và khóa PG xem [TOOLING.md](TOOLING.md)):

```powershell
# Trong lúc code: chạy test tập trung (trả RESULT=FOCUSED_PASS, KHÔNG thay full verification)
./scripts/verify-linux.ps1 -Filter "FullyQualifiedName~Inventory"

# Sinh lại hợp đồng OpenAPI + TypeScript client
./scripts/verify-linux.ps1 -Mode api -WriteBack

# Đóng băng manifest sau lần sửa cuối (đã hoàn tất tracked Immutable Record trước bước này)
./scripts/candidate-manifest.ps1 -Write -Base <approved-base-sha> -Out artifacts/<slug>/candidate.manifest

# Kiểm chứng cuối gắn manifest (kiểm đồng thời RESULT, coverage, manifest_sha256, exit_code)
./scripts/verify-linux.ps1 -Manifest artifacts/<slug>/candidate.manifest              # coverage=FULL_BACKEND
./scripts/verify-linux.ps1 -Mode full -Manifest artifacts/<slug>/candidate.manifest   # coverage=FULL_ALL (backend + frontend)
./scripts/verify-linux.ps1 -Mode frontend -Manifest artifacts/<slug>/candidate.manifest # coverage=FRONTEND
./scripts/verify-linux.ps1 -Mode docs -Manifest artifacts/<slug>/candidate.manifest   # CHỈ dùng khi toàn bộ file thuộc README.md / docs/**/*.md

# Nếu bị host PG lock contention (exit 5), chờ có giới hạn (cấm dùng TRA_HIEN_TEST_LOCK_PATH)
./scripts/verify-linux.ps1 -Mode full -Manifest artifacts/<slug>/candidate.manifest -LockWaitSeconds 300
```

Các lệnh manifest và base-integration thường dùng:

```powershell
# G4 xác minh candidate integrity
./scripts/candidate-manifest.ps1 -Verify artifacts/<slug>/candidate.manifest

# Publish Mốc A (Pre-stage) và Mốc B (Post-stage, pre-commit với -Both)
./scripts/candidate-manifest.ps1 -Verify artifacts/<slug>/candidate.manifest -RequireBase origin/main -PublishMode initial
./scripts/candidate-manifest.ps1 -Verify artifacts/<slug>/candidate.manifest -Both -RequireBase origin/main -PublishMode initial
./scripts/candidate-manifest.ps1 -Verify artifacts/<slug>/candidate.manifest -Both -RequireBase origin/main -PublishMode republish -PublishedHead origin/<branch>

# Base integration không dùng git stash (nhánh chưa publish)
./scripts/task-integrate.ps1 -Task <slug> -Action start|finish|drop|show -Onto origin/main
```

Log, TRX và `migrations.sql` nằm ở `artifacts/verify-linux/<timestamp>-<mode>/` (git-ignored). Lưu ý: local `FULL_ALL` không thay thế GitHub Actions CI hay `Docker regression builds`.

## 6. Khi nào gọi subagent

Chỉ gọi khi có workstream độc lập, cần góc nhìn kỹ thuật riêng hoặc lợi ích rõ ràng hơn chi phí ngữ cảnh. Giao file ownership rạch ròi; không để hai agent cùng ghi một file hay một worktree. Trong Codex có thể dùng `.codex/agents/*` (tùy chọn). Với G4, luôn dùng hội thoại độc lập thay vì subagent phụ thuộc ngữ cảnh của chính phiên G3.

## 7. Khi đặc tả thay đổi

`docs/spec/` (`00-README.md`, `01`–`09`, `schema.sql`) là nguồn có thẩm quyền; `08-chatgpt-playbook.md` chỉ là hướng dẫn lịch sử. Kiểm diff so với **audited spec SHA** thực tế (không nhầm với commit chạm spec gần nhất), rồi chạy G1/G2 tương ứng (hoặc Full G1 nếu ảnh hưởng rộng). Không tự sửa `docs/spec/` để hợp thức hóa code.
