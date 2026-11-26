# Agent Tooling & Verification Contracts (Trà Hiên V2)

Tài liệu này là nguồn chính cho chữ ký lệnh thực tế, token mức phủ kiểm chứng (`coverage`), mã lỗi (`exit_code`), cơ chế khóa PG cấp máy chủ, giới hạn vận hành và hạng mục follow-up độ tin cậy cục bộ. Luật chung nằm ở [AGENTS.md](../AGENTS.md); vòng đời cổng nằm ở [WORKFLOW.md](WORKFLOW.md); prompt mẫu ở [CODEX_USAGE.md](CODEX_USAGE.md).

## 1. Danh mục công cụ trong repository

| Công cụ / tập tin | Vai trò | Trạng thái |
|---|---|---|
| [Project skills](../.agents/skills) | 6 skill dùng chung (`spec-audit`, `architecture-plan`, `implementation-slice`, `review-gate`, `base-integration`, `publish-pr`) cho Codex và Antigravity | **Committed** |
| [verify-linux.ps1](../scripts/verify-linux.ps1) + [verify-linux.sh](../scripts/verify-linux.sh) + [Dockerfile.tools](../infra/Dockerfile.tools) | Kiểm chứng tương đương CI trong Docker Linux (Windows Application Control chặn bộ sinh OpenAPI với lỗi `0x800711C7`) | **Committed** · nguồn bằng chứng G3/G4 |
| [candidate-manifest.ps1](../scripts/candidate-manifest.ps1) | Đóng dấu nội dung candidate (`candidate-manifest v2`: Git mode + blob ID / `DELETED`), kiểm `RequireBase`, `-Both`, `-PublishMode` | **Committed** · bắt buộc cả Tầng A & B |
| [ci-fastpath.py](../scripts/ci-fastpath.py) | Phân loại phạm vi CI fail-closed (`docs`/`frontend`/`backend`/`full`) và kiểm chứng Markdown/link/UTF-8 cho ứng viên docs-only | **Committed** · dùng bởi CI và `-Mode docs` |
| [new-task-worktree.ps1](../scripts/new-task-worktree.ps1) | Tạo worktree cô lập cho từng task/chore từ `origin/main` hoặc `-Base` chỉ định | **Committed** |
| [task-integrate.ps1](../scripts/task-integrate.ps1) | Tích hợp base mới cho nhánh chưa publish bằng ref riêng `refs/task-integration/<task>` (không dùng `refs/stash`) | **Committed** · dùng bởi `base-integration` |
| [check-test-results.ps1](../scripts/check-test-results.ps1) + [migrations.ps1](../scripts/migrations.ps1) | Kiểm 3 tệp TRX backend không rỗng/0 skip; sinh `artifacts/migrations.sql` idempotent không cần DB | **Committed** |
| [test-agent-tooling.ps1](../scripts/test-agent-tooling.ps1) | Tự kiểm thử regression cho bộ script tooling trong repo tạm với fake Docker | **Committed** · chạy sau mọi sửa đổi `scripts/` |
| Công cụ ngoài (`spec-kit`, `superpowers`, `coderabbit`, `reviewdog`) | Tùy chọn bên ngoài, không thuộc workflow bắt buộc; không ghi đè `docs/spec/` hay `AGENTS.md` | **Optional** (xem §7) |

## 2. Hợp đồng kiểm chứng `scripts/verify-linux.ps1`

### 2.1 Chữ ký lệnh và tổ hợp tham số hợp lệ

```powershell
./scripts/verify-linux.ps1
  [-Mode backend|api|frontend|full|docs]   # mặc định: backend (case-insensitive trên PS1, chuẩn hóa chữ thường cho .sh)
  [-Filter <dotnet-test-filter>]           # chỉ dùng với backend|full; CẤM đi kèm -Manifest; cấm chuỗi toàn khoảng trắng
  [-WriteBack]                             # chỉ dùng với -Mode api; ghi đè và kiểm SHA-256 cả openapi.json + schema.ts
  [-Rebuild]                               # ép build lại image tra-hien-tools:<12-hex>
  [-Image <image-ref>]                     # CẤM đi kèm -Manifest
  [-Manifest <path>]                       # ràng buộc vào manifest v2; dựng snapshot từ .git/objects và kiểm label Dockerfile
  [-Base <ref|sha>]                        # chỉ dùng với -Mode docs KHÔNG có -Manifest (mặc định: origin/main)
  [-LockWaitSeconds <0..3600>]             # mặc định 0 (fail-fast); chỉ dùng với backend|full
```

Các tổ hợp bị từ chối với `exit_code=2` và `coverage=NONE`:
- `-Manifest` + `-Filter` (lọc test không bao giờ được làm bằng chứng full candidate verification).
- `-Filter` ngoài `backend`/`full`, hoặc `-Filter` chỉ chứa khoảng trắng.
- `-Manifest` + `-Image`, `-Manifest` + `-Base`.
- `-WriteBack` ngoài `-Mode api`.
- `-Base` ngoài `-Mode docs`.
- `-LockWaitSeconds` âm, `> 3600`, hoặc ngoài `backend`/`full`.

### 2.2 Dòng tóm tắt chuẩn và các giá trị `coverage`

Mọi lần chạy đều ghi dòng tóm tắt vào stdout và `artifacts/verify-linux/<timestamp>-<mode>/verify.log`:

- Thành công không lọc: `verify-linux RESULT=PASS mode=<Mode> coverage=<FULL_BACKEND|FULL_ALL|API|FRONTEND|DOCS> manifest_sha256=<digest|NONE> exit_code=0 elapsed=<N>s log=<path>`
- Thành công có `-Filter`: `verify-linux RESULT=FOCUSED_PASS mode=<backend|full> coverage=<FOCUSED_BACKEND|FOCUSED_ALL> manifest_sha256=NONE exit_code=0 elapsed=<N>s log=<path>`
- Thất bại: `verify-linux RESULT=FAIL mode=<Mode> coverage=NONE manifest_sha256=<digest|NONE> exit_code=<rc> ...`

Luôn kiểm tra đồng thời 4 trường: **`RESULT`**, **`coverage`**, **`manifest_sha256`** và **`exit_code`**.

| `coverage` | Nguồn phát sinh | Lưu ý bắt buộc |
|---|---|---|
| `FULL_BACKEND` | `-Mode backend` (không `-Filter`) | Chạy `test-ci-fastpath.py`, `dotnet restore --locked-mode`, `dotnet build -warnaserror`, `dotnet format whitespace`, toàn bộ backend tests trên PG17 Testcontainers, kiểm 0 skip (`check-test-results.ps1`), `migrations.ps1`, `pnpm check:api` |
| `FULL_ALL` | `-Mode full` (không `-Filter`) | Chạy `backend` + `frontend` (`pnpm install --frozen-lockfile`, `lint`, `typecheck`, Playwright Chromium + `test`, `build`). **Không** chứng minh GitHub Actions hay `Docker regression builds` đã chạy |
| `FOCUSED_BACKEND` | `-Mode backend -Filter ...` | Trả `RESULT=FOCUSED_PASS`; bỏ qua kiểm tra 0 skip; **không** thay thế full candidate verification |
| `FOCUSED_ALL` | `-Mode full -Filter ...` | Trả `RESULT=FOCUSED_PASS`; bỏ qua kiểm tra 0 skip; **không** thay thế full candidate verification |
| `API` | `-Mode api` (`[-WriteBack]`) | Sinh `openapi.json` và `schema.ts` từ API thật; không mount Docker socket, không dùng khóa PG |
| `FRONTEND` | `-Mode frontend` | Chỉ chạy phần frontend; không mount Docker socket, không dùng khóa PG |
| `DOCS` | `-Mode docs` | Kiểm chứng fail-closed candidate docs-only trong container (không mount Docker socket, không dùng khóa PG). Chạy không có `-Manifest` sẽ ghi `manifest_sha256=NONE` và không phải bằng chứng manifest-bound |
| `NONE` | Mọi lần chạy `RESULT=FAIL` | Không bao giờ coi là đạt |

### 2.3 Mã lỗi (`exit_code`) của `verify-linux.ps1`

| Exit code | Ý nghĩa |
|---|---|
| `0` | `RESULT=PASS` hoặc `RESULT=FOCUSED_PASS` |
| `1` | Lỗi chung (Git, mismatch manifest/snapshot, image build/verify, hoặc lỗi script bên trong container trả về `1`) |
| `2` | Sai đối số dòng lệnh hoặc tổ hợp cờ bị cấm |
| `3` | `-Mode api` thiếu tệp sinh ra hoặc `-WriteBack` lỗi ghi / lệch SHA-256 |
| `4` | Phát hiện tên tệp dạng secret (`.env*` trừ `*.example`, `*.pfx`, `*.p12`, `*.pem`, `*.key`, `*.secret`, `secrets/`) |
| `5` | Tranh chấp khóa PG cấp máy chủ (`host-lock: CONTENTION`) |
| `6` | Phát hiện container heavy verification sót lại mang nhãn khóa (`host-lock: RESIDUAL_WORKLOAD_DETECTED`) |
| `7` | Lỗi filesystem/quyền truy cập tại đường dẫn tệp khóa PG |
| Mã khác `!= 0` | Truyền nguyên mã thoát từ tiến trình trong container (`dotnet test`, `pnpm`, ...) |

### 2.4 Cô lập snapshot và an toàn đường dẫn

- Container không bao giờ mount trực tiếp worktree: chỉ mount read-only snapshot đã lọc (`git ls-files -co --exclude-standard`) tại `/src:ro`. Các thư mục bị ignore (`infra/.env`, `artifacts/`, `bin/obj`, `node_modules`, `.next`) không đi vào container.
- Khi dùng `-Manifest`, `verify-linux.ps1` đọc file manifest một lần vào bộ nhớ, kiểm khớp working tree, dựng snapshot từ các Git object bất biến (base tree + manifest entries qua temporary index), kiểm lại SHA-256 từng file và số lượng file trên đĩa trước khi ghi dòng `manifest-check: CANDIDATE MATCH: ... tree <snapshotTree>`, đồng thời bắt buộc label `org.tra-hien.tools.dockerfile-sha256` của tools image khớp `infra/Dockerfile.tools` trong snapshot.
- Từ chối mọi symlink/junction/reparse point trên đĩa và mọi tên file dạng secret ngay trên host (kể cả khi đã `git add -f`).
- Docker socket chỉ mount cho `-Mode backend` và `-Mode full`. Agent trong sandbox cần bypass/approval cho Docker và các lệnh Git ghi `.git`/mạng.

## 3. Phân loại phạm vi CI (`scripts/ci-fastpath.py`) và kiểm chứng Docs-Only

- **Allowlist docs-only duy nhất:** `README.md` và `docs/**/*.md` (đường dẫn POSIX an toàn, không phải secret, trạng thái `A`/`M`/`D`, không symlink `120000` hay gitlink `160000`).
- **Ngoài docs-only (kích hoạt `full` nếu không thuần `.cs` backend hay `apps/{admin,store}/{src,tests,public}`):** `AGENTS.md`, `.agents/**`, `.codex/**`, `scripts/**`, `.github/**`, `docs/spec/schema.sql`, `infra/**`, `packages/**`, lockfiles.
- **Dùng `-Mode docs` đúng cách:** chỉ chạy `./scripts/verify-linux.ps1 -Mode docs -Manifest <manifest>` khi toàn bộ candidate nằm trong allowlist `README.md` / `docs/**/*.md`. Không sửa classifier để ép docs fast-path cho thay đổi governance/scripts/workflows.
- **Lệnh kiểm chứng docs cục bộ trực tiếp (bổ trợ):**
  - `py scripts/ci-fastpath.py --local-base <ref|sha> --root .` (chỉ PASS khi candidate là docs-only).
  - Kiểm tra tĩnh Markdown (UTF-8, trailing whitespace, cân bằng fence, local links, cấm reference definition nhiều dòng/blockquote) cho mọi tệp `.md` đã sửa bằng hàm `validate_docs(paths, root)` của `scripts/ci-fastpath.py`.
- **Phân biệt Local vs GitHub Actions CI ([.github/workflows/ci.yml](../.github/workflows/ci.yml)):**
  - CI gồm **4 job**: (1) `Classify changes and validate docs`, (2) `Backend and API contract`, (3) `Frontend verification`, (4) `Docker regression builds`.
  - Khi CI trả `mode=docs` / `backend` / `frontend`, các job không liên quan vẫn kết thúc `success` ở trạng thái fast-path / `not applicable`. Do đó khi xác minh CI phải phân biệt rõ: job `success`, suite thực sự chạy và fast-path/`not applicable`.
  - Local `FULL_ALL` chạy backend + frontend trong `Dockerfile.tools`, **không** chạy GitHub Actions và **không** chạy `Docker regression builds` (`Dockerfile.api`, `Dockerfile.web` cho `store` và `admin`).

## 4. Hợp đồng Manifest, Publish Guards và Tích hợp Base

### 4.1 `scripts/candidate-manifest.ps1`

```powershell
# Ghi manifest sau lần sửa cuối (định dạng: # candidate-manifest v2 base=<sha>)
./scripts/candidate-manifest.ps1 -Write [-Base <ref|sha>] -Out <path>

# Xác minh working tree (mặc định), staged index (-Index), hoặc cả hai (-Both)
./scripts/candidate-manifest.ps1 -Verify <path>
  [-Index | -Both]                       # loại trừ nhau (exit 2 nếu dùng chung)
  [-RequireBase <ref|sha>]               # tách biệt base freshness khỏi candidate integrity
  [-PublishMode initial|republish]       # kiểm Git topology cục bộ trước commit
  [-PublishedHead <ref|sha>]             # chỉ hợp lệ khi -PublishMode republish (exit 2 nếu sai)
```

- **Exit codes:** `0` (`CANDIDATE MATCH`), `1` (mismatch / `BASE MISMATCH` / `TOPOLOGY MISMATCH` / `MANIFEST ERROR` / `UNSAFE PATH` / lỗi Git), `2` (sai đối số).
- **Ranh giới `-PublishMode`:**
  - `initial`: cấm thao tác Git dở dang, cấm detached HEAD, cấm `MERGE_HEAD`, bắt buộc `HEAD == manifest base`. Vì vậy **không chạy `-PublishMode initial` sau khi đã commit** (lúc đó `HEAD` đã tiến qua `base`).
  - `republish`: chấp nhận nhánh đã publish có commit trên `base..HEAD` hoặc đang có `MERGE_HEAD == manifest base` từ `base-integration` (`git merge --no-ff --no-commit origin/main`); từ chối commit WIP `task-integrate WIP `, orphan commit, octopus merge và merge từ nguồn ngoài ancestry của `base`.
  - **Lưu ý quan trọng về `-PublishedHead`:** cờ này kiểm tra `<PublishedHead>` là ancestor của `HEAD` (`merge-base --is-ancestor`). Khi republish, agent **phải kiểm tra trực tiếp remote HEAD thực tế** (`git ls-remote --heads origin <branch>` và `git rev-parse origin/<branch>`) cùng lịch sử commit được ủy quyền, không chỉ dựa vào kiểm tra ancestry.
- **Kiểm tra tương đương cây candidate không tuần hoàn (Checkpoint B/C của `publish-pr`):**
  - Không tự lấy `git write-tree` của staged index rồi so với chính nó.
  - Phải đối chiếu `git write-tree` (Checkpoint B) và `git rev-parse HEAD^{tree}` (Checkpoint C) với cây candidate đã review được dựng độc lập từ `<base>` + các entry trong manifest bằng `GIT_INDEX_FILE` tạm (`git read-tree <base>` → `git update-index --add --cacheinfo ...` / `--force-remove` → `git write-tree`), hoặc đối chiếu với `tree <snapshotTree>` đã được ghi trong log `verify-linux.ps1 -Manifest` khớp đúng `manifest_sha256`.

### 4.2 `scripts/new-task-worktree.ps1` và `scripts/task-integrate.ps1`

```powershell
# Tạo worktree cô lập (tự chạy git fetch origin --prune, không track origin/main)
./scripts/new-task-worktree.ps1 -Task <slug> -Branch <feature|fix|chore|docs|planning>/<name> [-Base <ref|sha>] [-Install]

# Tích hợp base cho nhánh chưa publish (KHÔNG dùng git stash)
./scripts/task-integrate.ps1 -Task <slug> -Action start|finish|drop|show [-Onto origin/main]
```

- `task-integrate.ps1` exit codes: `0` thành công, `1` lỗi chung, `2` xung đột / đang dở thao tác / WIP commit còn ở `HEAD`, `4` phát hiện secret-like file.
- Nhánh đã publish phải dùng `git merge --no-ff --no-commit origin/main` theo `.agents/skills/base-integration/SKILL.md` (không rebase, không force push).

## 5. Khóa PostgreSQL cấp máy chủ (Host PG Lock) và giới hạn vận hành

- **Phạm vi khóa:** chỉ áp dụng cho `-Mode backend` và `-Mode full`. Đường dẫn mặc định: `<TempPath>\tra-hien-locks\heavy-pg-verify.lock`. Các chế độ `api`, `frontend`, `docs` không lấy khóa PG và không mount Docker socket.
- **Cơ chế chờ:** mặc định fail-fast (`-LockWaitSeconds 0`); có thể đặt `-LockWaitSeconds <1..3600>` khi cần chờ có giới hạn. Khi hết thời gian chờ → `exit_code=5` kèm metadata chủ khóa đang giữ (`pid`, `mode`, `run_id`, `container`, `branch`, `worktree`, `started_utc`).
- **Kiểm tra workload sót lại:** sau khi giữ khóa, script chạy `docker ps --filter label=org.tra-hien.verify-lock=<scopeHash>`. Nếu còn container heavy verification cũ → `exit_code=6` và từ chối chạy chồng. Lỗi tạo/mở tệp khóa → `exit_code=7`.
- **Giới hạn vận hành đã biết:**
  1. Worktree chưa tích hợp PR-2 (còn dùng bản `verify-linux.ps1` cũ) sẽ không tự động tham gia khóa này.
  2. Khóa tệp không điều phối được workload Docker chạy ngoài `verify-linux.ps1`, tiến trình thuộc OS user/session khác thư mục temp, hoặc Docker host khác.
  3. Kiểm tra residual chỉ phát hiện container verifier mang nhãn `org.tra-hien.verify-lock=<scopeHash>`; nếu tiến trình chết đột ngột, các **child container** (ví dụ PostgreSQL Testcontainers / Ryuk) không mang nhãn này có thể còn tồn tại độc lập trên Docker daemon.
- **Quy tắc nghiêm cấm:**
  - **Cấm** đặt `TRA_HIEN_TEST_LOCK_PATH` trong mọi lượt kiểm chứng candidate thật (biến này chỉ dành riêng cho self-test `scripts/test-agent-tooling.ps1`).
  - **Cấm** `docker kill` / `docker rm` workload của agent hoặc worktree khác.
  - **Cấm** retry vô hạn; nếu gặp lỗi hạ tầng/contention, lưu đủ log/chẩn đoán và chỉ retry có kiểm soát khi đã xác nhận nguyên nhân hợp lệ.

## 6. Follow-up: Local Integration Test Reliability & Performance (NGOÀI PHẠM VI PR-3)

> **Lưu ý phạm vi:** Mục này ghi nhận hạng mục kỹ thuật cần theo dõi ở một công việc riêng sau PR-3; **không** triển khai chỉnh sửa test suite, timeout, production logging hay hạ tầng Docker trong PR-3.

Các vùng cần khảo sát có bằng chứng trong đợt follow-up riêng:
- **Docker Desktop / WSL2:** độ ổn định cầu mạng `host.docker.internal` (IPv4/IPv6), áp lực CPU/RAM/I/O đĩa và tốc độ khởi động container dưới tải cao.
- **PostgreSQL Testcontainers:** vòng đời cấp phát/hủy container, thời gian khởi tạo DB/migration lặp lại, và khả năng sót child container khi tiến trình bị ngắt bất thường.
- **Test parallelism:** mức song song mặc định của runner test tích hợp so với giới hạn tài nguyên máy phát triển và cạnh tranh tài nguyên giữa các collection.
- **Connection lifecycle:** đóng/xả pool kết nối Npgsql giữa các fixture test và tránh cạn kết nối hoặc rò rỉ socket.
- **Testhost hangs:** cơ chế thu thập blame/hang dump và phân biệt timeout hạ tầng với lỗi deadlock ứng dụng thật.
- **Diagnostic evidence:** chuẩn hóa bằng chứng thu thập khi gặp failure (TRX, slowest test classes, `docker ps`, host-lock log, mã lỗi chính xác) thay vì suy đoán nguyên nhân gốc.

## 7. Công cụ ngoài tùy chọn

Spec Kit, Superpowers, CodeRabbit và reviewdog là tùy chọn ngoài repository (không bắt buộc cài đặt, không copy vào repo và không thay thế `AGENTS.md`, `docs/spec/` hay Independent G4). Nếu cài đặt thử nghiệm trong môi trường cá nhân, luôn kiểm tra quyền truy cập dữ liệu, rà diff trước khi commit và giữ `docs/spec/` làm nguồn có thẩm quyền duy nhất.
