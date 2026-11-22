# Dùng Codex và Antigravity trong Trà Hiên V2

Tên file giữ nguyên để không gãy các link cũ. Tài liệu này áp dụng cho cả hai tool.

## 1. Mỗi tool đọc gì

| Thành phần | Codex | Antigravity |
|---|---|---|
| Luật chung `AGENTS.md` | Có | Có (nạp như rule ở mọi lượt) |
| Skills `.agents/skills/*/SKILL.md` | Có, gọi bằng `$tên-skill` | Có, gọi bằng tên skill hoặc đường dẫn file |
| Subagent `.codex/agents/*.toml`, `.codex/config.toml` | Có (tùy chọn, tối đa 3 thread) | **Không đọc**. G4 mở conversation mới |
| Sandbox | Theo cấu hình Codex | Mặc định chặn mạng/Docker/ghi `.git`: lệnh git fetch/worktree/commit/push và `verify-linux.ps1` cần bypass hoặc approval |

Sáu skill dùng chung: `spec-audit` (G1), `architecture-plan` (G2), `implementation-slice` (G3 và remediation), `review-gate` (G4), `base-integration`, `publish-pr`. Mỗi skill có sẵn block kết quả cố định, nên prompt chỉ cần vài dòng.

## 2. Đồng bộ và tạo worktree

```powershell
git status --short
git fetch origin --prune
./scripts/new-task-worktree.ps1 -Task p1-10 -Branch feature/p1-10-inventory-alerts
```

Lệnh trên tạo `D:\tra-hien-p1-10` từ `origin/main` mới nhất, branch không track `main`. Mở Codex hoặc Antigravity tại thư mục đó. Mỗi worktree chỉ một tool được ghi. Không dùng `reset --hard` hay `clean -fd`.

## 3. Ghép model (G4 nên khác hãng với G3)

| Việc | Gợi ý |
|---|---|
| G3 tầng A (tiền, kho, đơn, checkout, migration) | Antigravity Claude Opus (High) hoặc Codex với model mạnh nhất đang có |
| G3 tầng B (UI Admin, CRUD, docs) | Antigravity Gemini Neon (High) hoặc Codex |
| G4 | Tool hoặc hãng còn lại: code bằng Codex thì review bằng Antigravity, code bằng Claude thì review bằng Gemini hoặc Codex |
| Việc vặt (format, tra cứu, docs nhỏ) | Model nhanh/rẻ |

Đây là gợi ý vận hành, không phải benchmark đã đo trên repo này.

## 4. Prompt mẫu (thay `<...>`)

**G3:**

```text
Use $implementation-slice (.agents/skills/implementation-slice/SKILL.md) for <P1-10>.
Worktree: <D:\tra-hien-p1-10> (create with scripts/new-task-worktree.ps1 if missing), branch <feature/p1-10-inventory-alerts>.
Owner notes: <decisions/constraints or "none">.
Follow AGENTS.md. Return the skill's handoff block, then STOP (no commit/push/PR).
```

**G4 (session mới, khác hãng):**

```text
Use $review-gate (.agents/skills/review-gate/SKILL.md) for <P1-10>, Tier <A>.
Worktree <D:\tra-hien-p1-10>, branch <feature/p1-10-inventory-alerts>, base latest origin/main.
Manifest: artifacts/<p1-10>/candidate.manifest sha256 <digest>.
Fresh independent session, read-only. Return the skill's report block in Vietnamese.
```

**Remediation (quay lại phiên G3 hoặc mở phiên mới trên cùng worktree):**

```text
Use $implementation-slice in remediation mode for <P1-10> at <D:\tra-hien-p1-10>.
Fix only these G4 findings: <paste findings with IDs and file:line>.
Return the handoff block with FINDINGS status, then STOP.
```

**Focused re-review:**

```text
Use $review-gate as a focused re-review for <P1-10> at <D:\tra-hien-p1-10>.
Prior findings: <IDs>. Mark each CLOSED/OPEN with evidence, review only the delta, return the report block.
```

**Main đổi trước khi publish:**

```text
Use $base-integration for <P1-13> at <D:\tra-hien-p1-13>: origin/main moved (<PR #NN merged>).
Return the skill's report block; no commit/push.
```

**Publish (chỉ dùng sau G4 PASS):**

```text
Use $publish-pr for <P1-10> at <D:\tra-hien-p1-10>.
G4 PASS by <tool/model> for manifest sha256 <digest>. Owner authorizes publishing this task.
Return the skill's block. Do not merge.
```

Khi task có hợp đồng cần lưu ý, ghi vào dòng `Owner notes`. Không chép lại spec vào prompt.

## 5. Kiểm chứng trên Windows
Windows Application Control chặn công cụ sinh OpenAPI (`0x800711C7`), nên mọi bước kiểm chứng giống CI chạy qua Docker:

```powershell
./scripts/verify-linux.ps1                       # job CI backend đầy đủ (~4 phút)
./scripts/verify-linux.ps1 -Filter "FullyQualifiedName~Inventory"
./scripts/verify-linux.ps1 -Mode api -WriteBack  # sinh lại openapi.json + schema.ts
./scripts/verify-linux.ps1 -Mode frontend        # lint, typecheck, test, build frontend
```

Lần đầu chạy, script tự build image `tra-hien-tools:<12 ký tự đầu SHA-256 của infra/Dockerfile.tools>` rồi chạy bằng image ID. Container chỉ nhận snapshot đã lọc (file tracked và untracked không bị ignore). Tên file giống secret bị từ chối ngay trên host, kể cả khi đã force-add. Docker socket chỉ mount cho mode `backend`/`full`. Log, TRX và `migrations.sql` nằm ở `artifacts/verify-linux/<thời điểm>-<mode>/`.

Manifest nội dung cho cả hai tầng (Git mode + blob ID):

```powershell
./scripts/candidate-manifest.ps1 -Write -Base origin/main -Out artifacts/p1-10/candidate.manifest   # G3, sau lần sửa cuối
./scripts/verify-linux.ps1 -Manifest artifacts/p1-10/candidate.manifest                             # G3, lần chạy full cuối
./scripts/candidate-manifest.ps1 -Verify artifacts/p1-10/candidate.manifest                         # G4 và publish
./scripts/candidate-manifest.ps1 -Verify artifacts/p1-10/candidate.manifest -Index                  # publish, sau git add
./scripts/task-integrate.ps1 -Task p1-13 -Action start|finish|drop -Onto origin/main                  # base-integration (không dùng stash)
```

## 6. Khi nào gọi subagent
Chỉ gọi khi có workstream độc lập, cần một góc nhìn khác, hoặc lợi ích lớn hơn chi phí context. Giao file ownership rõ ràng, không để hai agent cùng sửa một file. Trong Codex có thể dùng `.codex/agents/*`. Trong Antigravity, G4 nên là conversation mới chứ không dùng subagent của chính phiên G3.

## 7. Khi spec đổi
Specs tại `docs/spec/00-README.md`, `01`–`09` và `schema.sql` là nguồn có thẩm quyền. `08-chatgpt-playbook.md` chỉ là hướng dẫn lịch sử. Kiểm diff rồi chạy lại G1/G2 tương ứng. Không sửa spec để hợp thức hóa code.
