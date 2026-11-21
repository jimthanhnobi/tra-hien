"use client";

import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { ApiError, isConcurrencyConflict } from "../../lib/api";
import { productTabs, publishIssues, tabLabels, type ProductTab } from "../../lib/product-forms";
import { ProductsApi, editorKey, productRoute, productsKey, type Product, type ProductImage, type Variant } from "../../lib/products-api";
import { useAdmin } from "../providers";
import { Feedback, FormError, Pending } from "../ui";
import { AttributeFields, ProductAttributesForm } from "./product-attributes";
import { ProductError, StatusBadge, type EditorActions } from "./product-fields";
import { ProductInformation } from "./product-information";
import { ProductImageForm, ProductImages } from "./product-media";
import { useProductNavigationGuard } from "./product-navigation";
import { ArchiveForm, PublicationChecklist } from "./product-publication";
import { useAttributes, useTaxonomy } from "./product-references";
import { ProductSeo, ThinDescriptionNote } from "./product-seo";
import type { ProductUser } from "./products-screen";
import { VariantDetails, VariantForm, VariantPrice } from "./product-variants";

type Edit = { product: Product } & (
  { kind: "information" | "attributes" | "seo" | "new-variant" | "attach" | "archive" }
  | { kind: "variant"; row: Variant } | { kind: "image"; row: ProductImage }
);

export function ProductEditor({ id, user }: { id: string; user: ProductUser }) {
  const { api: admin, queries } = useAdmin();
  const api = new ProductsApi(admin);
  const productRead = useQuery({ queryKey: editorKey(id), queryFn: ({ signal }) => api.get(id, signal) });
  const [tab, setTab] = useState<ProductTab>("information");
  const [edit, setEdit] = useState<Edit>();
  const [dirty, setDirty] = useState(false);
  const [pending, setPending] = useState(false);
  const [notice, setNotice] = useState("");
  const [failure, setFailure] = useState<unknown>();
  const [stale, setStale] = useState(false);
  const [publicationErrors, setPublicationErrors] = useState<Record<string, string[]>>({});
  const [jump, setJump] = useState<{ tab: ProductTab; target?: string }>();
  const busy = useRef(false);
  const product = edit?.product ?? productRead.data;
  const definitions = useAttributes(product?.productTypeId);
  const categories = useTaxonomy("categories");
  const write = user.permissions.includes("catalog.write");
  const publish = user.permissions.includes("catalog.publish");
  const price = user.permissions.includes("pricing.write");
  const canEnableUniquePiece = user.permissions.includes("inventory.read") && user.permissions.includes("inventory.write");
  const owner = user.roles.includes("owner");
  const editPermission = edit?.kind === "archive" ? publish : write;
  const editing = Boolean(edit) || pending;
  useProductNavigationGuard(dirty, pending);

  useEffect(() => {
    if (jump?.tab !== tab) return;
    const target = jump.target ? document.getElementById(jump.target) : null;
    if (target instanceof HTMLElement && !target.matches(":disabled")) target.focus();
    else document.getElementById(`product-panel-${tab}`)?.focus();
  }, [jump, tab]);

  function inspect(next: ProductTab, target?: string, fromTab = false) {
    if (busy.current) return;
    if (edit && dirty) { setNotice("Lưu hoặc hủy phần đang chỉnh sửa trước khi chuyển tab."); return; }
    if (edit) { setEdit(undefined); if (!stale) setFailure(undefined); }
    setTab(next); setJump(fromTab ? undefined : { tab: next, target });
  }

  function tabKeys(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    const keys: Record<string, number> = { ArrowLeft: (index + 4) % 5, ArrowRight: (index + 1) % 5, Home: 0, End: 4 };
    if (!Object.hasOwn(keys, event.key)) return;
    event.preventDefault(); document.getElementById(`product-tab-${productTabs[keys[event.key]]}`)?.focus();
  }

  async function perform<T>(work: () => Promise<T>, accept?: (value: T) => void | Promise<void>): Promise<T | undefined> {
    if (busy.current) return;
    busy.current = true; setPending(true); setFailure(undefined); setNotice("");
    try {
      const result = await work();
      await accept?.(result);
      return result;
    } catch (error) {
      setFailure(error);
      if (isConcurrencyConflict(error)) setStale(true);
      if (error instanceof ApiError && error.code === "publish_requirements_not_met") setPublicationErrors(error.fields);
      return undefined;
    } finally { busy.current = false; setPending(false); }
  }

  async function commit(work: () => Promise<Product>, message = "Đã lưu thay đổi.", allowed = editPermission) {
    if (stale || !allowed) return;
    await perform(work, async result => {
      if (result.id !== id || !result.rowVersion) throw new ApiError(502, {});
      await queries.cancelQueries({ queryKey: editorKey(id), exact: true });
      queries.setQueryData(editorKey(id), result);
      void queries.invalidateQueries({ queryKey: [...productsKey, "list"] });
      void queries.invalidateQueries({ queryKey: ["catalog", "product", id] });
      void queries.invalidateQueries({ queryKey: ["catalog", "product-search"] });
      setFailure(undefined); setEdit(undefined); setDirty(false); setPublicationErrors({}); setNotice(message);
      setJump({ tab });
    });
  }

  function begin(next: Edit) {
    if (busy.current || edit || stale) return;
    setEdit(next); setDirty(false); setFailure(undefined); setNotice("");
  }

  function clearEdit() {
    setEdit(undefined); setDirty(false); setNotice(""); setJump({ tab });
    if (!stale) setFailure(undefined);
  }

  function cancel() {
    if (busy.current || (dirty && !window.confirm("Bỏ thay đổi chưa lưu trong phần này?"))) return;
    clearEdit();
  }

  function reload() {
    if (busy.current || (dirty && !window.confirm("Bỏ phần đang sửa và tải bản mới nhất từ hệ thống?"))) return;
    void perform(() => productRead.refetch({ throwOnError: true }), result => {
      if (!result.data?.rowVersion) throw new ApiError(502, {});
      setEdit(undefined); setDirty(false); setPublicationErrors({}); setNotice("Đã tải phiên bản mới nhất.");
      setStale(false);
    });
  }

  if (!product) return <><Link href={productRoute}>← Danh sách sản phẩm</Link>{productRead.isError
    ? <><h1>{productRead.error instanceof ApiError && productRead.error.status === 404 ? "Không tìm thấy sản phẩm" : "Không tải được sản phẩm"}</h1>
      <FormError error={productRead.error} /><button className="button secondary" onClick={() => void productRead.refetch()}>Thử tải lại</button></>
    : <Pending>Đang tải sản phẩm…</Pending>}</>;

  const allDefinitions = definitions.data ?? [];
  const productDefinitions = allDefinitions.filter(def => def.scope === "product");
  const variantDefinitions = allDefinitions.filter(def => def.scope === "variant");
  const actions: EditorActions = { pending, failure, stale, cancel, discardAndReload: reload, dirty: (value = true) => setDirty(value) };
  const rowVersion = product.rowVersion!;
  const activeEdit = edit && editPermission ? edit.kind : undefined;
  const locked = editing || stale || !rowVersion;
  const issues = publishIssues(publicationErrors, product, allDefinitions);
  const simple = tab === "information" || tab === "attributes" || tab === "seo";
  const publishFailed = failure instanceof ApiError && failure.code === "publish_requirements_not_met" && issues.length > 0;
  let panel: ReactNode;
  if (tab === "information") {
    panel = <ProductInformation product={product}
      actions={activeEdit === tab ? actions : undefined} save={body => commit(() => api.patch(id, body))} />;
  } else if (tab === "attributes") {
    panel = <>
      {definitions.isError && <><p role="alert">{definitions.data ? "Chưa tải lại được định nghĩa thuộc tính. Thay đổi đang nhập được giữ lại. Hãy tải lại trước khi lưu." : "Chưa tải được định nghĩa thuộc tính."}</p>
        <button className="button secondary" type="button" onClick={() => void definitions.refetch()}>Tải lại thuộc tính</button></>}
      {!definitions.data && definitions.isPending && <Pending>Đang tải thuộc tính…</Pending>}
      {definitions.data && (activeEdit === tab ? <ProductAttributesForm definitions={productDefinitions} original={product.attributes ?? []} actions={actions}
          verificationPending={definitions.isError || definitions.isFetching}
          onEmptyAfterDiscard={() => { if (!busy.current) clearEdit(); }}
          save={attributes => commit(() => api.patch(id, { rowVersion, attributes }))} />
          : <AttributeFields definitions={productDefinitions} original={product.attributes ?? []} changes={{}} onChange={() => {}} readOnly />)}
    </>;
  } else if (tab === "seo") {
    panel = <ProductSeo key={activeEdit === tab ? "editing" : "view"} product={product}
      actions={activeEdit === tab ? actions : undefined} save={body => commit(() => api.patch(id, body))} />;
  } else if (tab === "variants") {
    const variant = edit?.kind === "variant" ? edit.row : undefined;
    panel = <>
      <div className="product-heading"><h2 id="variants" tabIndex={-1}>Biến thể và giá niêm yết</h2>{write && <button type="button" className="button" disabled={locked || !definitions.data}
        onClick={() => begin({ kind: "new-variant", product })}>Thêm biến thể</button>}</div>
      {definitions.isError && <><FormError error={definitions.error} /><button className="button secondary" type="button" onClick={() => void definitions.refetch()}>Tải lại thuộc tính</button></>}
      {(activeEdit === "variant" || activeEdit === "new-variant") && <VariantForm variant={variant} rowVersion={variant?.rowVersion ?? rowVersion} canPrice={price} owner={owner}
        canEnableUniquePiece={canEnableUniquePiece} definitions={variantDefinitions} actions={actions} save={body => commit(() => api.saveVariant(id, variant?.id, body))} />}
      {!product.variants?.length && <p>Chưa có biến thể.</p>}
      <ul className="product-variants">{product.variants?.map((row, index) => <li key={row.id} className="product-card" id={`variant-${row.id}`} tabIndex={-1}>
        <div className="product-heading"><div><h3>{row.name || (index === 0 ? "Biến thể mặc định" : `Biến thể ${index + 1}`)}</h3><p>{row.sku} · {row.status === "active" ? "Hoạt động" : "Ngừng bán"} · <VariantPrice amount={row.priceVnd} /></p></div>
          {write && !!row.id && <button className="button secondary" type="button" disabled={locked || !definitions.data} aria-label={`Chỉnh sửa biến thể ${row.sku}`}
            onClick={() => void perform(() => api.variant(id, row.id!), result => {
              if (!result.id || result.id !== row.id || !result.rowVersion) throw new ApiError(502, {});
              if (result.rowVersion !== row.rowVersion) throw new ApiError(409, { code: "concurrency_conflict" });
              setEdit({ kind: "variant", product, row: result }); setDirty(false);
            })}>Chỉnh sửa</button>}</div>
        <details><summary>Thông số và thuộc tính</summary><VariantDetails variant={row} definitions={variantDefinitions} /></details>
      </li>)}</ul>
    </>;
  } else {
    const image = edit?.kind === "image" ? edit.row : undefined;
    panel = <>
      <ProductImages product={product} editable={write} disabled={locked}
        onAttach={() => begin({ kind: "attach", product })} onEdit={row => begin({ kind: "image", product, row })}
        onCover={childId => void commit(() => api.setCover(id, childId, rowVersion))}
        onOrder={ids => void commit(() => api.orderImages(id, ids, rowVersion))}
        onRemove={childId => void commit(() => api.removeImage(id, childId, rowVersion))} />
      {(activeEdit === "attach" || activeEdit === "image") && <ProductImageForm product={product} image={image} actions={actions}
        upload={file => perform(() => api.upload(file, product.slug))}
        save={body => commit(() => image?.id ? api.patchImage(id, image.id, { rowVersion, ...body }) : api.attach(id, { rowVersion, ...body }))} />}
    </>;
  }

  const primaryVisible = categories.data?.some(row => row.id === product.primaryCategoryId && row.status === "visible");
  return <>
    <Link href={productRoute}>← Danh sách sản phẩm</Link>
    <header className="page-heading product-heading"><div><h1>{product.name}</h1><p>SKU gốc · {product.skuBase}</p></div><StatusBadge status={product.status} /></header>
    {product.archivedRedirectPath && <p>Đường dẫn chuyển hướng · {product.archivedRedirectPath}</p>}
    {notice && <Feedback>{notice}</Feedback>}
    {notice === "Đã công khai sản phẩm." && <ThinDescriptionNote product={product} />}
    {productRead.isError && !edit && <FormError error={productRead.error} />}
    {!activeEdit && !publishFailed && <ProductError error={failure} />}
    {!activeEdit && stale && <button className="button secondary" type="button" disabled={pending} onClick={reload}>Tải phiên bản mới</button>}
    {!rowVersion && <p role="alert">Không nhận được phiên bản sản phẩm. Hãy tải lại trước khi chỉnh sửa.</p>}
    {edit && !editPermission && <p role="alert">Quyền cập nhật đã thay đổi. <button type="button" onClick={cancel}>Đóng phần đang sửa</button></p>}
    <PublicationChecklist product={product} definitions={definitions.data} primaryVisible={primaryVisible} issues={issues} inspect={inspect} compact={editing} />
    {publish && <div className="catalog-actions product-publication-actions">
      {product.status === "draft" && <button type="button" className="button" disabled={locked}
        onClick={() => void commit(() => api.transition(id, "publish", { rowVersion }), "Đã công khai sản phẩm.", publish)}>Công khai</button>}
      {product.status !== "archived" && <button type="button" className="button secondary" disabled={locked} onClick={() => begin({ kind: "archive", product })}>Lưu trữ</button>}
      {product.status === "archived" && <button type="button" className="button secondary" disabled={locked} onClick={() => {
        if (window.confirm("Khôi phục sản phẩm về bản nháp?")) void commit(() => api.transition(id, "restore-draft", { rowVersion }), "Đã khôi phục bản nháp.", publish);
      }}>Khôi phục bản nháp</button>}
      {editing && dirty && <p className="hint">Lưu hoặc hủy thay đổi trước khi đổi trạng thái sản phẩm.</p>}
    </div>}
    {activeEdit === "archive" && <ArchiveForm product={product} actions={actions} save={redirectTo => commit(
      () => api.transition(id, "archive", { rowVersion, ...(redirectTo && { redirectTo }) }), "Đã lưu trữ sản phẩm.", publish)} />}
    {pending && <Pending>Đang xử lý yêu cầu…</Pending>}
    <div role="tablist" aria-label="Chỉnh sửa sản phẩm" className="product-tabs">
      {productTabs.map((item, index) => <button key={item} type="button" role="tab" id={`product-tab-${item}`} aria-selected={tab === item}
        aria-controls={`product-panel-${item}`} tabIndex={tab === item ? 0 : -1} onKeyDown={event => tabKeys(event, index)} onClick={() => { if (item !== tab) inspect(item, undefined, true); }}>
        {tabLabels[item]}</button>)}
    </div>
    <section role="tabpanel" id={`product-panel-${tab}`} aria-labelledby={`product-tab-${tab}`} tabIndex={-1} className="product-tab-panel">
      {simple && write && !activeEdit && <button className="button secondary" type="button" disabled={locked || (tab === "attributes" && !definitions.data)}
        onClick={() => begin({ kind: tab, product })}>Chỉnh sửa {tabLabels[tab].toLowerCase()}</button>}
      {panel}
    </section>
    {productTabs.filter(item => item !== tab).map(item => <div key={item} role="tabpanel" id={`product-panel-${item}`} aria-labelledby={`product-tab-${item}`} hidden />)}
  </>;
}
