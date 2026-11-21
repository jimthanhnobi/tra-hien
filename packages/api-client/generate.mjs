import { spawnSync } from "node:child_process";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
import openapiTS, { astToString } from "openapi-typescript";
import ts from "typescript";

const root = fileURLToPath(new URL("../../", import.meta.url));
const output = path.join(root, "artifacts/openapi");
await mkdir(output, { recursive: true });
const source = path.join(output, "openapi.json");
await rm(source, { force: true });
// MSBuild caches the file list rather than the JSON output; force regeneration on every check.
await rm(path.join(root, "backend/src/TeaPremium.Api/obj/TeaPremium.Api.OpenApiFiles.cache"), { force: true });
const result = spawnSync("dotnet", [
  "build", "backend/src/TeaPremium.Api", "--no-restore", "-warnaserror",
  "-p:OpenApiGenerateDocuments=true", `-p:OpenApiDocumentsDirectory=${output}`,
], {
  cwd: root, stdio: "inherit",
  // Contract generation must never inherit a developer's database connection.
  env: { ...process.env, ASPNETCORE_ENVIRONMENT: "CI", ConnectionStrings__Default: "" },
});
if (result.error) throw result.error;
if (result.status !== 0) process.exit(result.status ?? 1);

const document = JSON.parse(await readFile(source, "utf8"));
if (!document.openapi?.startsWith("3.") || !document.paths || !document.info) {
  throw new Error("API did not generate a valid OpenAPI contract");
}
// Stable key ordering/newlines across Windows and Linux; never remove paths or schemas.
function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
  }
  return value;
}
const json = JSON.stringify(canonical(document), null, 2) + "\n";
// TypeScript cannot recursively index a component's own property (TS2502).
// Hoist this JSON union into a named recursive alias, deriving its complete body
// from the API schema rather than maintaining a second handwritten JSON model.
const jsonName = "SettingsJsonValue";
const aliases = [];
const ast = await openapiTS(JSON.parse(json), {
  alphabetize: true,
  postTransform(type, { path }) {
    if (path !== `#/components/schemas/${jsonName}`) return;
    const rewritten = ts.transform(type, [(context) => (root) => {
      function visit(node) {
        if (ts.isIndexedAccessTypeNode(node)
          && node.indexType.literal?.text === jsonName
          && ts.isIndexedAccessTypeNode(node.objectType)
          && node.objectType.indexType.literal?.text === "schemas"
          && ts.isTypeReferenceNode(node.objectType.objectType)
          && node.objectType.objectType.typeName.text === "components") {
          return ts.factory.createTypeReferenceNode(jsonName);
        }
        return ts.visitEachChild(node, visit, context);
      }
      return ts.visitNode(root, visit);
    }]);
    aliases.push(ts.factory.createTypeAliasDeclaration(
      [ts.factory.createModifier(ts.SyntaxKind.ExportKeyword)], jsonName, undefined, rewritten.transformed[0],
    ));
    rewritten.dispose();
    return ts.factory.createTypeReferenceNode(jsonName);
  },
});
// openapi-typescript models an exploded query object under its parameter name.
// openapi-fetch takes the wire keys directly. Flatten only this Store parameter.
function flattenStoreAttributes(nodes) {
  const route = "/api/v1/store/products";
  const parameter = document.paths[route].get.parameters.find(p => p.name === "attributes" && p.in === "query");
  const pattern = "^attr\\.[a-z0-9_]{2,40}$";
  if (parameter?.schema?.patternProperties?.[pattern]?.type !== "string" || parameter.schema.additionalProperties !== false) {
    throw new Error("Store attribute query metadata changed");
  }
  const member = (node, name) => node?.members?.find(p => ts.isPropertySignature(p) && p.name?.text === name);
  const paths = nodes.find(node => ts.isInterfaceDeclaration(node) && node.name.text === "paths");
  const get = member(member(paths, route)?.type, "get");
  const query = member(member(get?.type, "parameters")?.type, "query")?.type;
  const attributes = member(query, parameter.name);
  if (!attributes || !ts.isTypeLiteralNode(query)) throw new Error("Store query type was not generated");
  const index = ts.factory.createIndexSignature(undefined, [
    ts.factory.createParameterDeclaration(undefined, undefined, "key", undefined,
      ts.factory.createTemplateLiteralType(ts.factory.createTemplateHead("attr."), [
        ts.factory.createTemplateLiteralTypeSpan(ts.factory.createKeywordTypeNode(ts.SyntaxKind.StringKeyword), ts.factory.createTemplateTail("")),
      ])),
  ], ts.factory.createUnionTypeNode([
    ts.factory.createKeywordTypeNode(ts.SyntaxKind.StringKeyword),
    ts.factory.createKeywordTypeNode(ts.SyntaxKind.UndefinedKeyword),
  ]));
  const result = ts.transform(nodes, [(context) => (node) => {
    function visit(child) {
      return child === query
        ? ts.factory.updateTypeLiteralNode(query, [...query.members.filter(p => p !== attributes), index])
        : ts.visitEachChild(child, visit, context);
    }
    return ts.visitNode(node, visit);
  }]);
  const output = result.transformed;
  result.dispose();
  return output;
}

const types = "// Generated from the actual API by corepack pnpm generate:api. Do not edit.\n"
  + astToString([...aliases, ...flattenStoreAttributes(ast)]).replaceAll("\r\n", "\n");
const targets = [
  [new URL("./openapi.json", import.meta.url), json],
  [new URL("./src/generated/schema.ts", import.meta.url), types],
];
const check = process.argv.includes("--check");
for (const [target, content] of targets) {
  if (check) {
    const committed = await readFile(target, "utf8");
    if (committed.replaceAll("\r\n", "\n") !== content) {
      throw new Error(`Stale generated output: ${fileURLToPath(target)}. Run corepack pnpm generate:api.`);
    }
  } else {
    await mkdir(new URL(".", target), { recursive: true });
    await writeFile(target, content);
  }
}
console.log(check ? "OpenAPI/client are current." : "OpenAPI/client generated.");
