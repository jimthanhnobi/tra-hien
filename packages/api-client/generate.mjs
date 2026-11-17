import { spawnSync } from "node:child_process";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
import openapiTS, { astToString } from "openapi-typescript";

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
const types = "// Generated from the actual API by corepack pnpm generate:api. Do not edit.\n"
  + astToString(await openapiTS(JSON.parse(json), { alphabetize: true })).replaceAll("\r\n", "\n");
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
