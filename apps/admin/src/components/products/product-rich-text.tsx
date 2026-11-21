"use client";

import { useEffect, useRef, useState } from "react";
import { EditorContent, Extension, Node, useEditor, useEditorState, type Editor, type JSONContent } from "@tiptap/react";
import type { Node as DocumentNode } from "@tiptap/pm/model";
import { EditorState } from "@tiptap/pm/state";
import StarterKit from "@tiptap/starter-kit";
import Image from "@tiptap/extension-image";
import { TableKit } from "@tiptap/extension-table";

const richImage = Image.extend({
  parseHTML() { return [{ tag: 'img[src^="https://"]' }]; }
}).configure({ HTMLAttributes: { referrerpolicy: "no-referrer" } });

const figure = Node.create({
  name: "figure", group: "block", content: "(block | caption)*",
  parseHTML: () => [{ tag: "figure" }], renderHTML: () => ["figure", 0]
});
const caption = Node.create({
  name: "figcaption", group: "caption", content: "inline*",
  parseHTML: () => [{ tag: "figcaption", getAttrs: element => hasBlocks(element) ? false : null }],
  renderHTML: () => ["figcaption", 0]
});
const blockTags = "p,h2,h3,h4,ul,ol,blockquote,table,figure";
const hasBlocks = (element: HTMLElement) => Array.from(element.children).some(child => child.matches(blockTags));
const blockCaption = Node.create({
  name: "figcaptionBlock", group: "caption", content: "block+",
  parseHTML: () => [{ tag: "figcaption", getAttrs: element => hasBlocks(element) ? null : false }],
  renderHTML: () => ["figcaption", 0]
});
const inlineCell = Extension.create({
  name: "productInlineCell",
  addGlobalAttributes() {
    return [{ types: ["tableCell", "tableHeader"], attributes: {
      inlineCell: {
        default: false,
        parseHTML: (element: HTMLElement) => element.hasChildNodes() && !hasBlocks(element),
        renderHTML: attributes => attributes.inlineCell ? { "data-product-inline-cell": "" } : {}
      }
    } }];
  }
});

const extensions = [
  StarterKit.configure({ code: false, codeBlock: false, horizontalRule: false, strike: false, underline: false,
    trailingNode: false, heading: { levels: [2, 3, 4] }, link: { openOnClick: false, autolink: false,
      HTMLAttributes: { target: null, rel: null, class: null } } }),
  TableKit, richImage, figure, caption, blockCaption, inlineCell
];

// Project only the generated presentation attributes removed by the server.
// Keep the persisted side independent of ProseMirror and its normalization.
function htmlStructure(html: string, fromEditor = false) {
  const body = new DOMParser().parseFromString(html, "text/html").body;
  if (fromEditor) {
    for (const colgroup of body.querySelectorAll("table > colgroup")) colgroup.remove();
    for (const table of body.querySelectorAll("table[style]")) table.removeAttribute("style");
    for (const image of body.querySelectorAll('img[referrerpolicy="no-referrer"]')) image.removeAttribute("referrerpolicy");
    for (const link of body.querySelectorAll("a[href]")) {
      if (/^https?:\/\//i.test(link.getAttribute("href") ?? "")) link.setAttribute("rel", "noopener nofollow");
    }
  }
  function structure(node: globalThis.Node): unknown {
    if (!(node instanceof Element)) return [node.nodeType, node.nodeValue];
    const attrs = new Map(Array.from(node.attributes, ({ name, value }) => [name, value]));
    if (node.matches("th,td")) {
      if (!attrs.has("colspan")) attrs.set("colspan", "1");
      if (!attrs.has("rowspan")) attrs.set("rowspan", "1");
    }
    return [node.localName, [...attrs].sort(), Array.from(node.childNodes, structure)];
  }
  return JSON.stringify(Array.from(body.childNodes, structure));
}

function htmlForSave(editor: Editor) {
  const html = editor.getHTML();
  if (!html.includes("data-product-inline-cell")) return html;
  const { body } = new DOMParser().parseFromString(html, "text/html");
  for (const cell of body.querySelectorAll("td[data-product-inline-cell],th[data-product-inline-cell]")) {
    cell.removeAttribute("data-product-inline-cell");
    const paragraph = cell.firstChild;
    // ProseMirror wraps inline-only cells. Unwrap while the cell remains a single paragraph.
    if (cell.childNodes.length === 1 && paragraph instanceof Element && paragraph.matches("p")) {
      paragraph.replaceWith(...paragraph.childNodes);
    }
  }
  return body.innerHTML;
}

const jsonNodeAttributes = new Set(["level", "src", "alt", "width", "height", "colspan", "rowspan", "colwidth", "start"]);
const jsonLinkAttributes = new Set(["href", "rel", "target"]);
const blockNodes = new Set(["paragraph", "heading", "bulletList", "orderedList", "blockquote", "table", "figure", "image"]);

function editorJson(node: JSONContent): JSONContent {
  const type = node.type === "figcaption" && node.content?.some(child => blockNodes.has(child.type ?? "")) ? "figcaptionBlock" : node.type;
  return { ...node, type, content: node.content?.map(editorJson) };
}

function wireJson(node: JSONContent): JSONContent {
  function attributes(values: JSONContent["attrs"], allowed: Set<string>) {
    const attrs = Object.fromEntries(Object.entries(values ?? {}).filter(([key, value]) => allowed.has(key) && value != null).sort(([a], [b]) => a.localeCompare(b)));
    return Object.keys(attrs).length ? { attrs } : {};
  }
  return { type: node.type === "figcaptionBlock" ? "figcaption" : node.type,
    ...attributes(node.attrs, jsonNodeAttributes),
    ...(node.text !== undefined && { text: node.text }),
    ...(node.marks?.length && { marks: node.marks.map(mark => ({ type: mark.type, ...attributes(mark.attrs, jsonLinkAttributes) })) }),
    ...(node.content?.length && { content: node.content.map(wireJson) }) };
}

function sameWireSource(original: JSONContent, roundTrip: JSONContent): boolean {
  if (original.type !== roundTrip.type || original.text !== roundTrip.text) return false;
  const before = original.attrs ?? {};
  const after = roundTrip.attrs ?? {};
  const isCell = original.type === "tableCell" || original.type === "tableHeader";
  const keys = new Set([...Object.keys(before), ...Object.keys(after)]);
  for (const key of keys) {
    if (JSON.stringify(before[key]) === JSON.stringify(after[key])) continue;
    // Tiptap emits these defaults for omitted attributes. Explicit values
    // remain authoritative, including spans greater than one.
    const defaultOne = (isCell && (key === "rowspan" || key === "colspan")) ||
      (original.type === "orderedList" && key === "start");
    if (!Object.hasOwn(before, key) && defaultOne && after[key] === 1) continue;
    return false;
  }
  const sameList = (first: JSONContent[] = [], second: JSONContent[] = []) =>
    first.length === second.length && first.every((node, index) => sameWireSource(node, second[index]));
  return sameList(original.content, roundTrip.content) && sameList(original.marks, roundTrip.marks);
}

type RichChange = { html: string | null } | { json: JSONContent };
function loadDocument(editor: Editor, content: string | DocumentNode) {
  editor.commands.setContent(content, { emitUpdate: false });
  // setContent dispatches synchronously. The resulting document includes
  // every appended normalization transaction. Keep that document and reset
  // the history so undo cannot cross between authoritative sources.
  const { doc, plugins } = editor.state;
  editor.view.updateState(EditorState.create({ doc, plugins }));
  return editor.state.doc;
}

function installSource(editor: Editor, html: string | null | undefined, json: string | null | undefined) {
  if (json != null) {
    try {
      const source: JSONContent = JSON.parse(json);
      const doc = editor.schema.nodeFromJSON(editorJson(structuredClone(source)));
      doc.check();
      const settled = loadDocument(editor, doc);
      if (sameWireSource(source, wireJson(settled.toJSON()))) return true;
    } catch { /* The saved HTML remains available as a read-only preview. */ }
    loadDocument(editor, html ?? "");
    return false;
  }
  loadDocument(editor, html ?? "");
  return !html || htmlStructure(html) === htmlStructure(htmlForSave(editor), true);
}

export function ProductRichText({ id, label, initialHtml, initialJson, onChange, disabled }: {
  id: string; label: string; initialHtml?: string | null; initialJson?: string | null; onChange?: (value: RichChange | undefined) => void; disabled?: boolean;
}) {
  const [verified, setVerified] = useState<{ editor: Editor; html: typeof initialHtml; json: typeof initialJson; editing: boolean; supported: boolean }>();
  const baseline = useRef<DocumentNode | null>(null);
  const editing = !!onChange;
  const currentVerification = verified?.html === initialHtml && verified?.json === initialJson && verified?.editing === editing ? verified : undefined;
  const editor = useEditor({ extensions, immediatelyRender: false, editable: false,
    editorProps: { attributes: { id, role: "textbox", "aria-multiline": "true", "aria-label": label, class: "product-rich-content" } },
    onUpdate: ({ editor }) => {
      if (disabled || !editing || currentVerification?.editor !== editor || !currentVerification.supported || !baseline.current) return;
      if (baseline.current.eq(editor.state.doc)) onChange?.(undefined);
      else if (initialJson != null) onChange?.({ json: wireJson(editor.getJSON()) });
      else onChange?.({ html: editor.isEmpty ? null : htmlForSave(editor) });
    } });
  const supported = currentVerification?.editor === editor && !!currentVerification?.supported;
  const editable = editing && supported && !disabled;
  const marks = useEditorState({ editor, selector: ({ editor }) => ({ bold: editor?.isActive("bold"), italic: editor?.isActive("italic"),
    heading: editor?.isActive("heading"), list: editor?.isActive("bulletList"), ordered: editor?.isActive("orderedList") }) });
  useEffect(() => {
    if (!editor) return;
    editor.setEditable(editable, false);
    editor.view.dom.setAttribute("aria-readonly", String(!editable));
  }, [editor, editable]);
  useEffect(() => {
    if (!editor) return;
    editor.setEditable(false, false);
    baseline.current = null;
    const supported = installSource(editor, initialHtml, initialJson);
    if (supported) baseline.current = editor.state.doc;
    // Compatibility is determined by parsing with the live browser editor.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setVerified({ editor, html: initialHtml, json: initialJson, editing, supported });
  }, [editor, initialHtml, initialJson, editing]);
  if (!editor) return <p role="status">Đang mở trình soạn thảo…</p>;
  return <section className="product-rich" aria-label={label}>
    <strong>{label}</strong>
    {editing && currentVerification?.editor === editor && !supported && <p className="hint" role="status">Nội dung này có cấu trúc mà trình soạn thảo chưa thể lưu nguyên vẹn. Bạn vẫn có thể sửa và lưu các thông tin khác.</p>}
    {editing && supported && <div className="catalog-actions" role="group" onMouseDown={event => { if (event.target instanceof HTMLButtonElement) event.preventDefault(); }} aria-label={`Định dạng ${label.toLowerCase()}`}>
      <button type="button" disabled={disabled} className="button secondary" aria-pressed={marks?.bold} onClick={() => editor.chain().toggleBold().run()}>Đậm</button>
      <button type="button" disabled={disabled} className="button secondary" aria-pressed={marks?.italic} onClick={() => editor.chain().toggleItalic().run()}>Nghiêng</button>
      <button type="button" disabled={disabled} className="button secondary" aria-pressed={marks?.heading} onClick={() => editor.chain().toggleHeading({ level: 2 }).run()}>Tiêu đề</button>
      <button type="button" disabled={disabled} className="button secondary" aria-pressed={marks?.list} onClick={() => editor.chain().toggleBulletList().run()}>Danh sách dấu đầu dòng</button>
      <button type="button" disabled={disabled} className="button secondary" aria-pressed={marks?.ordered} onClick={() => editor.chain().toggleOrderedList().run()}>Danh sách đánh số</button>
      <button type="button" disabled={disabled} className="button secondary" onClick={() => editor.chain().insertTable({ rows: 2, cols: 2, withHeaderRow: true }).run()}>Thêm bảng</button>
    </div>}
    <EditorContent editor={editor} />
  </section>;
}
