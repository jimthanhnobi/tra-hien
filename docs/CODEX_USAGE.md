# Dùng Codex trực tiếp trong Trà Hiên V2

## 1. Đồng bộ repository an toàn
```powershell
git status --short
git fetch origin --prune
git switch main
git pull --ff-only origin main
git log -1 --oneline
```
Chỉ switch/pull sau khi xử lý an toàn local changes. Không `reset --hard`/`clean -fd`. Mở Codex tại root repo; kiểm project trust và restart Codex nếu không thấy skill/agent.

## 2. Repo đã có gì?
- `AGENTS.md`: luật scope, spec precedence, G0–G4.
- `.agents/skills/{spec-audit,architecture-plan,implementation-slice,review-gate}/SKILL.md`: **quy trình**.
- `.codex/agents/{spec_analyst,planner,implementer,reviewer}.toml`: **subagents tùy chọn**.
- `.codex/config.toml`: bật agents, tối đa 3 thread đồng thời (trần giới hạn, không phải mục tiêu).
- `docs/WORKFLOW.md`: G1/G2 evidence reuse và publish gate.

**Chú ý:** gọi skill không bắt buộc spawn subagent. Subagents kế thừa permission/sandbox của parent; 'read-only' trong instruction không phải giới hạn kỹ thuật. Chọn session/quyền phù hợp khi review.

## 3. Cách dùng theo loại công việc
| Khi nào | Skill | Subagent |
|---|---|---|
| Full spec audit hoặc spec đổi lớn | `$spec-audit` | `spec_analyst` nếu cần độc lập |
| Kiến trúc/phụ thuộc chưa chốt | `$architecture-plan` | `planner` nếu thật sự cần |
| Một P task đã GO | `$implementation-slice` | Main thread mặc định |
| G4 trước merge | `$review-gate` trong session mới | `reviewer` tùy chọn, không trùng implementer |

Không lặp toàn bộ spec audit/architecture planning trên từng PR; nhưng luôn kiểm spec SHA/diff và scoped G1/G2.

## 4. Prompt ngắn cho task
**G3 implement (ví dụ P0-10):**
```text
Use $implementation-slice for P0-10 from docs/spec/07-roadmap.md.
Follow AGENTS.md and docs/WORKFLOW.md.
Start from latest origin/main on a dedicated feature branch; preserve local edits.
Reuse verified G1/G2 after checking spec changes and dependencies.
Audit code/tests first; implement only missing task requirements.
Work in main Codex thread; no subagents for a bounded task.
Run real build/tests/format; provide commands and results.
Commit/push feature branch, prepare PR; no docs/spec edits or merge.
```
**G4 independent review (session khác):**
```text
Use $review-gate for P0-10.
Review feature diff against latest origin/main and task spec.
Check correctness, tests, scope and regressions; reproduce P0/P1 if possible.
Report file:line, impact, minimal fix, VERIFIED / NOT RUN.
Return PASS / CONDITIONAL / BLOCK. Read-only: no edits/push/merge.
```

## 5. Khi nào gọi subagent?
Chỉ khi có workstream độc lập, cần góc nhìn khác hoặc lợi ích vượt chi phí context. Giao file ownership rõ. Không spawn nhiều agent cùng đọc một tập tài liệu. G4 ưu tiên session mới; Codex CLI có thể hỗ trợ `/subagents`.

## 6. Khi spec đổi
Specs tại `docs/spec/00-README.md`, `01`–`09` và `schema.sql` là authoritative; `08-chatgpt-playbook.md` chỉ hướng dẫn workflow lịch sử. Kiểm diff rồi làm lại G1/G2 tương ứng; không sửa spec để hợp thức hóa code.
