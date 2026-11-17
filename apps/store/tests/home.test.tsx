import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import Home from "../src/app/page";

test("store scaffold renders its heading and construction message", () => {
  const html = renderToStaticMarkup(<Home />);
  assert.match(html, /<h1[^>]*>Trà Hiên<\/h1>/);
  assert.match(html, /Nền tảng cửa hàng đang được xây dựng\./);
});

test("store uses the same-origin API prefix from the shared client", () => {
  assert.match(renderToStaticMarkup(<Home />), /data-api-base-path="\/api"/);
});
