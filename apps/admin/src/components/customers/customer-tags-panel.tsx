"use client";

import { useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Pencil, Plus } from "lucide-react";
import { CustomersApi, customerTagsKey, customersRootKey, type CustomerTag } from "../../lib/customers-api";
import { presetTagColors, validateTagForm } from "../../lib/customers-forms";
import { useAdmin } from "../providers";
import { Feedback, FormError, Pending } from "../ui";
import { TagChip } from "./customer-badges";

export function CustomerTagsPanel() {
  const { api: admin, queries } = useAdmin();
  const api = useMemo(() => new CustomersApi(admin), [admin]);

  const [newName, setNewName] = useState("");
  const [newColor, setNewColor] = useState("#214f40");
  const [createError, setCreateError] = useState<string | null>(null);
  const [createServerError, setCreateServerError] = useState<unknown>();
  const [createPending, setCreatePending] = useState(false);

  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editColor, setEditColor] = useState("");
  const [editError, setEditError] = useState<string | null>(null);
  const [editServerError, setEditServerError] = useState<unknown>();
  const [editPending, setEditPending] = useState(false);

  const [notice, setNotice] = useState("");
  const busy = useRef(false);

  // Page is strictly gated by `customers.write` in CustomersScreen (GAP-P202-02 = B).
  const tagsQuery = useQuery({
    queryKey: customerTagsKey,
    queryFn: ({ signal }) => api.listTags(signal)
  });

  function startEdit(tag: CustomerTag) {
    setEditingId(tag.id);
    setEditName(tag.name);
    setEditColor(tag.color ?? "");
    setEditError(null);
    setEditServerError(undefined);
    setNotice("");
  }

  function cancelEdit() {
    setEditingId(null);
    setEditError(null);
    setEditServerError(undefined);
  }

  async function handleCreate(event: React.FormEvent) {
    event.preventDefault();
    if (busy.current) return;
    setNotice("");
    setCreateServerError(undefined);
    const check = validateTagForm(newName, newColor);
    if (check.error || !check.payload) {
      setCreateError(check.error ?? "Thông tin nhãn chưa hợp lệ.");
      return;
    }
    setCreateError(null);
    busy.current = true;
    setCreatePending(true);
    try {
      const created = await api.createTag(check.payload);
      setNewName("");
      setNotice(`Đã tạo nhãn "${created.name}".`);
      await queries.invalidateQueries({ queryKey: customerTagsKey });
    } catch (error) {
      setCreateServerError(error);
    } finally {
      busy.current = false;
      setCreatePending(false);
    }
  }

  async function handleSaveEdit(tag: CustomerTag) {
    if (busy.current) return;
    setNotice("");
    setEditServerError(undefined);
    const check = validateTagForm(editName, editColor);
    if (check.error || !check.payload) {
      setEditError(check.error ?? "Thông tin nhãn chưa hợp lệ.");
      return;
    }
    setEditError(null);
    const patch: { name?: string; color?: string | null } = {};
    if (check.payload.name !== tag.name) patch.name = check.payload.name;
    if ((check.payload.color ?? null) !== (tag.color ?? null)) patch.color = check.payload.color;
    if (Object.keys(patch).length === 0) {
      cancelEdit();
      return;
    }
    busy.current = true;
    setEditPending(true);
    try {
      const updated = await api.patchTag(tag.id, patch);
      setEditingId(null);
      setNotice(`Đã cập nhật nhãn "${updated.name}".`);
      await queries.invalidateQueries({ queryKey: customersRootKey });
    } catch (error) {
      setEditServerError(error);
    } finally {
      busy.current = false;
      setEditPending(false);
    }
  }

  return (
    <section className="inventory-panel" aria-labelledby="customer-tags-heading">
      <div className="inventory-section-header">
        <div>
          <h2 id="customer-tags-heading">Quản lý nhãn khách hàng</h2>
          <p className="hint">
            Tạo và đổi tên nhãn dùng chung cho toàn bộ hồ sơ khách hàng, bộ lọc danh sách và import khách cũ.
          </p>
        </div>
      </div>

      {notice && <Feedback>{notice}</Feedback>}

      <form className="catalog-form inventory-form-card" onSubmit={event => void handleCreate(event)} noValidate>
        <h3>Tạo nhãn mới</h3>
        <FormError error={createServerError} />
        {createError && <div className="alert" role="alert"><div><p>{createError}</p></div></div>}

        <div className="catalog-fields">
          <div className="product-field">
            <label htmlFor="tag-new-name">Tên nhãn *</label>
            <input
              id="tag-new-name"
              type="text"
              maxLength={60}
              disabled={createPending}
              value={newName}
              onChange={e => setNewName(e.target.value)}
              placeholder="Ví dụ: Yêu thích tử sa, Quà biếu Tết…"
            />
          </div>

          <div className="product-field">
            <label htmlFor="tag-new-color">Màu hiển thị (#RRGGBB, tùy chọn)</label>
            <input
              id="tag-new-color"
              type="text"
              maxLength={7}
              disabled={createPending}
              value={newColor}
              onChange={e => setNewColor(e.target.value)}
              placeholder="#214f40"
            />
            <div className="customer-color-swatches" role="group" aria-label="Chọn nhanh màu nhãn">
              {presetTagColors.map(color => (
                <button
                  key={color}
                  type="button"
                  className={`customer-swatch ${newColor.toLowerCase() === color ? "is-selected" : ""}`}
                  style={{ backgroundColor: color }}
                  aria-label={`Chọn màu ${color}`}
                  aria-pressed={newColor.toLowerCase() === color}
                  onClick={() => setNewColor(color)}
                />
              ))}
              <button
                type="button"
                className="inventory-copy-button"
                onClick={() => setNewColor("")}
              >
                Không màu
              </button>
            </div>
          </div>
        </div>

        <div className="catalog-actions">
          <button type="submit" className="button" disabled={createPending}>
            <Plus size={17} aria-hidden="true" />
            <span>{createPending ? "Đang tạo…" : "Thêm nhãn"}</span>
          </button>
        </div>
      </form>

      {tagsQuery.isPending ? (
        <Pending>Đang tải danh sách nhãn…</Pending>
      ) : tagsQuery.isError ? (
        <div>
          <FormError error={tagsQuery.error} />
          <button type="button" className="button secondary" onClick={() => void tagsQuery.refetch()}>
            Thử tải lại
          </button>
        </div>
      ) : (tagsQuery.data?.length ?? 0) === 0 ? (
        <div className="empty-state">
          <h3>Chưa có nhãn nào</h3>
          <p>Tạo nhãn đầu tiên ở biểu mẫu phía trên để phân nhóm khách hàng nhanh hơn.</p>
        </div>
      ) : (
        <ul className="customer-card-list" aria-label="Danh sách nhãn khách hàng">
          {(tagsQuery.data ?? []).map(tag => {
            const isEditing = editingId === tag.id;
            return (
              <li key={tag.id} className="customer-card">
                {isEditing ? (
                  <div className="inventory-stack">
                    <FormError error={editServerError} />
                    {editError && <p className="field-error" role="alert">{editError}</p>}
                    <div className="catalog-fields">
                      <div className="product-field">
                        <label htmlFor={`edit-tag-name-${tag.id}`}>Tên nhãn</label>
                        <input
                          id={`edit-tag-name-${tag.id}`}
                          type="text"
                          maxLength={60}
                          disabled={editPending}
                          value={editName}
                          onChange={e => setEditName(e.target.value)}
                        />
                      </div>
                      <div className="product-field">
                        <label htmlFor={`edit-tag-color-${tag.id}`}>Màu hiển thị (#RRGGBB)</label>
                        <input
                          id={`edit-tag-color-${tag.id}`}
                          type="text"
                          maxLength={7}
                          disabled={editPending}
                          value={editColor}
                          onChange={e => setEditColor(e.target.value)}
                          placeholder="Để trống nếu không màu"
                        />
                      </div>
                    </div>
                    <div className="catalog-actions">
                      <button
                        type="button"
                        className="button"
                        disabled={editPending}
                        onClick={() => void handleSaveEdit(tag)}
                      >
                        {editPending ? "Đang lưu…" : "Lưu thay đổi"}
                      </button>
                      <button type="button" className="button secondary" disabled={editPending} onClick={cancelEdit}>
                        Hủy
                      </button>
                    </div>
                  </div>
                ) : (
                  <div className="customer-card-row">
                    <div className="customer-tag-preview">
                      <TagChip tag={tag} />
                      <span className="hint">{tag.color ?? "Màu mặc định"}</span>
                    </div>
                    <button
                      type="button"
                      className="button secondary"
                      aria-label={`Đổi tên / màu nhãn ${tag.name}`}
                      onClick={() => startEdit(tag)}
                    >
                      <Pencil size={15} aria-hidden="true" />
                      <span>Đổi tên / màu</span>
                    </button>
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
