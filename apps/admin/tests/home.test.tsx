import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import Home from "../src/app/page";

test("admin scaffold renders its heading and construction message", () => {
  const html = renderToStaticMarkup(<Home />);
  assert.match(html, /<h1[^>]*>Trà Hiên Admin<\/h1>/);
  assert.match(html, /Nền tảng quản trị đang được xây dựng\./);
});

test("admin uses the same-origin API prefix from the shared client", () => {
  assert.match(renderToStaticMarkup(<Home />), /data-api-base-path="\/api"/);
});
